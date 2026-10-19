using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostBotDmIntegrationTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(25);

    [Fact]
    public async Task BotToken_DirectMessageMentionAndFallbackWorkAcrossReconnect()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_NATIVE_MATTERMOST_E2E"), "1", StringComparison.Ordinal))
            return;

        var statePath = Environment.GetEnvironmentVariable("CAB_NATIVE_MATTERMOST_STATE_FILE");
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
            throw new InvalidOperationException("Native Mattermost integration state file is missing.");

        var state = await LoadStateAsync(statePath);
        var server = new Uri(state.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (!server.IsLoopback || server.Scheme != Uri.UriSchemeHttp || server.Port != 8070)
            throw new InvalidOperationException("Bot integration mutations are restricted to the dedicated localhost Mattermost server on port 8070.");

        using var http = new HttpClient { Timeout = OperationTimeout };
        using var cancellation = new CancellationTokenSource(OperationTimeout * 4);
        var botUsername = "cc-agent-bot-" + Guid.NewGuid().ToString("N")[..10];
        string? botId = null;
        string? botTokenId = null;
        string? botToken = null;
        try
        {
            var bot = await CreateBotAsync(http, server, state.BridgeUser.Token, botUsername,
                createdBotId => botId = createdBotId, cancellation.Token);
            var token = await CreateBotTokenAsync(http, server, state.BridgeUser.Token, botId!, cancellation.Token);
            botTokenId = token.Id;
            botToken = token.Token;

            var memberDmId = await CreateDirectChannelAsync(http, server, state.MemberUser.Token,
                state.MemberUser.Id!, botId!, cancellation.Token);
            var botDmId = await CreateDirectChannelAsync(http, server, botToken!, botId!,
                state.MemberUser.Id!, cancellation.Token);
            Assert.Equal(memberDmId, botDmId);

            var unauthorizedDmId = await CreateDirectChannelAsync(http, server, state.UnauthorizedUser.Token,
                state.UnauthorizedUser.Id!, botId!, cancellation.Token);
            var settings = new BridgeSettings
            {
                MattermostServerUrl = state.ServerUrl,
                MattermostChannelId = memberDmId,
                MattermostToken = botToken,
                MattermostAllowedUserIds = state.MemberUser.Id
            };
            await AssertUnauthorizedDmIgnoredAsync(http, server, state, settings with
            {
                MattermostChannelId = unauthorizedDmId
            }, botUsername, cancellation.Token);

            var accepted = Channel.CreateUnbounded<ChatRequest>();
            await using var transport = new MattermostWebSocketTransport();
            using var firstRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            using var secondRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            Task? firstRun = null;
            Task? secondRun = null;
            var firstConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action firstConnectedHandler = () => firstConnected.TrySetResult();
            var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action secondConnectedHandler = () => secondConnected.TrySetResult();
            transport.Connected += firstConnectedHandler;
            try
            {
                firstRun = transport.RunAsync(settings,
                    (request, ct) => accepted.Writer.WriteAsync(request, ct).AsTask(), firstRunCancellation.Token);
                await firstConnected.Task.WaitAsync(OperationTimeout, cancellation.Token);

                using var wrongChannel = await PostAsync(http, server, state.MemberUser.Token,
                    state.Channel.Id, $"@{botUsername} wrong channel", cancellation.Token);
                using var ownPost = await PostAsync(http, server, botToken,
                    memberDmId, $"@{botUsername} own message", cancellation.Token);
                using var malformedMention = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, "hello @" + botUsername + " later", cancellation.Token);
                using var wrongMention = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, "@cab-no-such-bot should be ignored", cancellation.Token);
                await AssertNoRequestAsync(accepted.Reader, cancellation.Token);

                const string mentionPrompt = "please inspect 日本語と🌊";
                using var mentionedPost = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, $"@{botUsername} {mentionPrompt}", cancellation.Token);
                var mentionPostId = mentionedPost.RootElement.GetProperty("id").GetString()!;
                var mentioned = await accepted.Reader.ReadAsync(cancellation.Token).AsTask()
                    .WaitAsync(OperationTimeout, cancellation.Token);
                Assert.Equal("mattermost", mentioned.Provider);
                Assert.Equal(memberDmId, mentioned.ChannelId);
                Assert.Equal(mentionPostId, mentioned.ThreadId);
                Assert.Equal(state.MemberUser.Id, mentioned.UserId);
                Assert.Equal(mentionPrompt, mentioned.Text);

                const string replyText = "Bot DM reply: 返信できました 🟢";
                await transport.ReplyAsync(mentioned, replyText, cancellation.Token);
                using var mentionThread = await GetThreadAsync(http, server, botToken, mentionPostId, cancellation.Token);
                Assert.Contains(mentionThread.RootElement.GetProperty("posts").EnumerateObject(), item =>
                    item.Value.TryGetProperty("message", out var message) && message.GetString() == replyText &&
                    item.Value.TryGetProperty("root_id", out var rootId) && rootId.GetString() == mentionPostId);

                using var continuationPost = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, $"@{botUsername} continue in this thread", cancellation.Token, mentionPostId);
                var continuation = await accepted.Reader.ReadAsync(cancellation.Token).AsTask()
                    .WaitAsync(OperationTimeout, cancellation.Token);
                Assert.Equal(continuationPost.RootElement.GetProperty("id").GetString(), continuation.MessageId);
                Assert.Equal(mentionPostId, continuation.ThreadId);
                Assert.Equal("continue in this thread", continuation.Text);

                using var fallbackPost = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, "!cab existing trigger fallback", cancellation.Token);
                var fallbackPostId = fallbackPost.RootElement.GetProperty("id").GetString()!;
                var fallback = await accepted.Reader.ReadAsync(cancellation.Token).AsTask()
                    .WaitAsync(OperationTimeout, cancellation.Token);
                Assert.Equal(fallbackPostId, fallback.ThreadId);
                Assert.Equal("existing trigger fallback", fallback.Text);

                transport.Connected -= firstConnectedHandler;
                firstRunCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun.WaitAsync(OperationTimeout));

                transport.Connected += secondConnectedHandler;
                secondRun = transport.RunAsync(settings,
                    (request, ct) => accepted.Writer.WriteAsync(request, ct).AsTask(), secondRunCancellation.Token);
                await secondConnected.Task.WaitAsync(OperationTimeout, cancellation.Token);
                using var reconnectedPost = await PostAsync(http, server, state.MemberUser.Token,
                    memberDmId, $"@{botUsername} after reconnect", cancellation.Token);
                var reconnected = await accepted.Reader.ReadAsync(cancellation.Token).AsTask()
                    .WaitAsync(OperationTimeout, cancellation.Token);
                Assert.Equal(reconnectedPost.RootElement.GetProperty("id").GetString(), reconnected.ThreadId);
                Assert.Equal("after reconnect", reconnected.Text);
            }
            finally
            {
                transport.Connected -= firstConnectedHandler;
                firstRunCancellation.Cancel();
                transport.Connected -= secondConnectedHandler;
                secondRunCancellation.Cancel();
                if (firstRun is not null) await ObserveShutdownAsync(firstRun);
                if (secondRun is not null) await ObserveShutdownAsync(secondRun);
            }
        }
        finally
        {
            try
            {
                if (botId is not null && botTokenId is not null)
                    await RevokeBotTokenAsync(http, server, state.BridgeUser.Token, botTokenId);
            }
            finally
            {
                if (botId is not null)
                    await DisableBotAsync(http, server, state.BridgeUser.Token, botId);
            }
        }
    }

    private static async Task<BootstrapState> LoadStateAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<BootstrapState>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (state is null || string.IsNullOrWhiteSpace(state.ServerUrl) ||
            string.IsNullOrWhiteSpace(state.Channel?.Id) || string.IsNullOrWhiteSpace(state.BridgeUser?.Token) ||
            string.IsNullOrWhiteSpace(state.BridgeUser.Id) || string.IsNullOrWhiteSpace(state.MemberUser?.Token) ||
            string.IsNullOrWhiteSpace(state.MemberUser.Id) || string.IsNullOrWhiteSpace(state.UnauthorizedUser?.Token) ||
            string.IsNullOrWhiteSpace(state.UnauthorizedUser.Id))
            throw new InvalidOperationException("Native Mattermost bot integration state is incomplete.");
        return state;
    }

    private static async Task<BotInfo> CreateBotAsync(HttpClient http, Uri server, string adminToken,
        string username, Action<string> rememberBotId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, server, "api/v4/bots", adminToken,
            JsonContent.Create(new { username, display_name = "CAB E2E bot", description = "Temporary integration regression bot" }));
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response, "Mattermost bot creation");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var returnedBot = json.RootElement.Deserialize<BotCreateResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var createdId = returnedBot?.UserId ?? returnedBot?.Id;
        if (string.IsNullOrWhiteSpace(createdId))
            throw new InvalidOperationException("Mattermost bot creation returned no bot user ID.");
        rememberBotId(createdId);

        using var lookup = CreateRequest(HttpMethod.Get, server,
            $"api/v4/users/{Uri.EscapeDataString(createdId)}", adminToken);
        using var lookupResponse = await http.SendAsync(lookup, ct);
        EnsureSuccess(lookupResponse, "Mattermost created bot verification");
        using var lookupJson = await JsonDocument.ParseAsync(await lookupResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var bot = lookupJson.RootElement.Deserialize<BotInfo>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bot is null || bot.Id != createdId || !bot.IsBot || bot.Username != username)
            throw new InvalidOperationException("Mattermost created bot verification failed.");
        return bot;
    }

    private static async Task<BotToken> CreateBotTokenAsync(HttpClient http, Uri server, string adminToken,
        string botId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, server,
            $"api/v4/users/{Uri.EscapeDataString(botId)}/tokens", adminToken,
            JsonContent.Create(new { description = "Temporary CAB bot DM integration test" }));
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response, "Mattermost bot token creation");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var token = json.RootElement.Deserialize<BotToken>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (token is null || string.IsNullOrWhiteSpace(token.Id) || string.IsNullOrWhiteSpace(token.Token))
            throw new InvalidOperationException("Mattermost bot token creation returned incomplete token metadata.");
        return token;
    }

    private static async Task<string> CreateDirectChannelAsync(HttpClient http, Uri server, string actorToken,
        string actorId, string otherUserId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, server, "api/v4/channels/direct", actorToken,
            JsonContent.Create(new[] { actorId, otherUserId }));
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response, "Mattermost direct channel creation or lookup");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var id = json.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        return !string.IsNullOrWhiteSpace(id) ? id : throw new InvalidOperationException("Mattermost direct channel response omitted its ID.");
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, Uri server, string token,
        string channelId, string message, CancellationToken ct, string? rootId = null)
    {
        using var request = CreateRequest(HttpMethod.Post, server, "api/v4/posts", token,
            JsonContent.Create(new { channel_id = channelId, message, root_id = rootId ?? "" }));
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response, "Mattermost integration post");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task<JsonDocument> GetThreadAsync(HttpClient http, Uri server, string token,
        string postId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, server,
            $"api/v4/posts/{Uri.EscapeDataString(postId)}/thread", token);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response, "Mattermost integration thread lookup");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task AssertNoRequestAsync(ChannelReader<ChatRequest> accepted, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
        Assert.False(accepted.TryRead(out _));
    }

    private static async Task AssertUnauthorizedDmIgnoredAsync(HttpClient http, Uri server, BootstrapState state,
        BridgeSettings settings, string botUsername, CancellationToken ct)
    {
        var accepted = Channel.CreateUnbounded<ChatRequest>();
        await using var transport = new MattermostWebSocketTransport();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? run = null;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action connectedHandler = () => connected.TrySetResult();
        transport.Connected += connectedHandler;
        try
        {
            run = transport.RunAsync(settings,
                (request, token) => accepted.Writer.WriteAsync(request, token).AsTask(), runCancellation.Token);
            await connected.Task.WaitAsync(OperationTimeout, ct);
            using var unauthorizedPost = await PostAsync(http, server, state.UnauthorizedUser.Token,
                settings.MattermostChannelId!, $"@{botUsername} unauthorized sender", ct);
            await AssertNoRequestAsync(accepted.Reader, ct);
        }
        finally
        {
            transport.Connected -= connectedHandler;
            runCancellation.Cancel();
            if (run is not null) await ObserveShutdownAsync(run);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri server, string path,
        string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(server, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed with HTTP {(int)response.StatusCode}.");
    }

    private static async Task RevokeBotTokenAsync(HttpClient http, Uri server, string token, string tokenId)
    {
        using var request = CreateRequest(HttpMethod.Post, server, "api/v4/users/tokens/revoke", token,
            JsonContent.Create(new { token_id = tokenId }));
        using var response = await http.SendAsync(request);
        EnsureSuccess(response, "Mattermost bot token revocation");
    }

    private static async Task DisableBotAsync(HttpClient http, Uri server, string token, string botId)
    {
        using var request = CreateRequest(HttpMethod.Post, server,
            $"api/v4/bots/{Uri.EscapeDataString(botId)}/disable", token);
        using var response = await http.SendAsync(request);
        EnsureSuccess(response, "Mattermost bot disable");
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception) { }
    }

    private sealed class BootstrapState
    {
        public string ServerUrl { get; set; } = "";
        public MattermostChannel Channel { get; set; } = new();
        public BootstrapUser BridgeUser { get; set; } = new();
        public BootstrapUser MemberUser { get; set; } = new();
        public BootstrapUser UnauthorizedUser { get; set; } = new();
    }

    private sealed class MattermostChannel { public string Id { get; set; } = ""; }
    private sealed class BootstrapUser { public string Id { get; set; } = ""; public string Token { get; set; } = ""; }
    private sealed class BotCreateResponse
    {
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }
    private sealed class BotInfo
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("is_bot")]
        public bool IsBot { get; set; }
    }
    private sealed class BotToken
    {
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";
    }
}
