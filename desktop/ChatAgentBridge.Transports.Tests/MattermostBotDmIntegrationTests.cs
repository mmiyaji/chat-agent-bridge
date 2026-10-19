using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
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

            using var temporary = new TemporaryDirectory();
            var workspace = Path.Combine(temporary.Path, "workspace");
            Directory.CreateDirectory(workspace);
            var directSessionDirectory = Path.Combine(temporary.Path, "direct-sessions");
            var directSessions = new SessionStore(directSessionDirectory);
            var accepted = Channel.CreateUnbounded<ChatRequest>();
            var mentionPostId = await RunDirectDmFlowAsync(http, server, state, settings, botUsername,
                directSessions, accepted, cancellation.Token);
            var ownerKey = ChatSessionScope.Create("mattermost", memberDmId, mentionPostId,
                state.MemberUser.Id!, state.ServerUrl);
            await directSessions.SaveAsync(new SessionBinding(ownerKey, Path.GetFullPath(workspace),
                "codex", "", Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));

            var reloadedSessions = new SessionStore(directSessionDirectory);
            Assert.NotNull(await reloadedSessions.FindAsync(ownerKey, cancellation.Token));
            await RunPersistedDmReconnectAsync(http, server, state, settings, botUsername,
                mentionPostId, reloadedSessions, cancellation.Token);
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

    private static async Task<string> RunDirectDmFlowAsync(HttpClient http, Uri server, BootstrapState state,
        BridgeSettings settings, string botUsername, SessionStore sessions, Channel<ChatRequest> accepted,
        CancellationToken ct)
    {
        await using var transport = new MattermostWebSocketTransport(sessions);
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

            using var wrongChannel = await PostAsync(http, server, state.MemberUser.Token,
                state.Channel.Id, $"@{botUsername} wrong channel", ct);
            using var ownPost = await PostAsync(http, server, settings.MattermostToken,
                settings.MattermostChannelId!, $"@{botUsername} own message", ct);
            using var malformedMention = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "hello @" + botUsername + " later", ct);
            using var wrongMention = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "@cab-no-such-bot should be ignored", ct);
            await AssertNoRequestAsync(accepted.Reader, ct);

            const string mentionPrompt = "please inspect 日本語と🌊";
            using var mentionedPost = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, $"@{botUsername} {mentionPrompt}", ct);
            var mentionPostId = mentionedPost.RootElement.GetProperty("id").GetString()!;
            var mentioned = await accepted.Reader.ReadAsync(ct).AsTask().WaitAsync(OperationTimeout, ct);
            Assert.Equal("mattermost", mentioned.Provider);
            Assert.Equal(settings.MattermostChannelId, mentioned.ChannelId);
            Assert.Equal(mentionPostId, mentioned.ThreadId);
            Assert.Equal(state.MemberUser.Id, mentioned.UserId);
            Assert.Equal(mentionPrompt, mentioned.Text);

            var ownerKey = ChatSessionScope.Create("mattermost", settings.MattermostChannelId!, mentionPostId,
                state.MemberUser.Id!, state.ServerUrl);
            Assert.Null(await sessions.FindAsync(ownerKey, ct));
            const string immediateText = "plain continuation: keep   spacing\nand this second line";
            using var immediateReply = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, immediateText, ct, mentionPostId);
            var immediateRequest = await accepted.Reader.ReadAsync(ct).AsTask().WaitAsync(OperationTimeout, ct);
            Assert.Equal(immediateReply.RootElement.GetProperty("id").GetString(), immediateRequest.MessageId);
            Assert.Equal(mentionPostId, immediateRequest.ThreadId);
            Assert.Equal(state.MemberUser.Id, immediateRequest.UserId);
            Assert.Equal(immediateText, immediateRequest.Text);

            using var topLevelPlain = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "normal top-level DM must be ignored", ct);
            await AssertNoRequestAsync(accepted.Reader, ct);
            using var unrelatedRoot = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "unrelated thread root", ct);
            using var unrelatedReply = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "plain reply on an unrelated thread", ct,
                unrelatedRoot.RootElement.GetProperty("id").GetString());
            await AssertNoRequestAsync(accepted.Reader, ct);

            using var fallbackPost = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "!cab existing trigger fallback", ct);
            var fallbackPostId = fallbackPost.RootElement.GetProperty("id").GetString()!;
            var fallback = await accepted.Reader.ReadAsync(ct).AsTask().WaitAsync(OperationTimeout, ct);
            Assert.Equal(fallbackPostId, fallback.ThreadId);
            Assert.Equal("existing trigger fallback", fallback.Text);
            const string fallbackReplyText = "plain reply after the !cab thread start";
            using var fallbackReply = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, fallbackReplyText, ct, fallbackPostId);
            var fallbackContinuation = await accepted.Reader.ReadAsync(ct).AsTask().WaitAsync(OperationTimeout, ct);
            Assert.Equal(fallbackPostId, fallbackContinuation.ThreadId);
            Assert.Equal(fallbackReplyText, fallbackContinuation.Text);

            const string replyText = "Bot DM reply: 返信できました 🟢";
            await transport.ReplyAsync(mentioned, replyText, ct);
            using var mentionThread = await GetThreadAsync(http, server, settings.MattermostToken,
                mentionPostId, ct);
            Assert.Contains(mentionThread.RootElement.GetProperty("posts").EnumerateObject(), item =>
                item.Value.TryGetProperty("message", out var message) && message.GetString() == replyText &&
                item.Value.TryGetProperty("root_id", out var rootId) && rootId.GetString() == mentionPostId);
            return mentionPostId;
        }
        finally
        {
            transport.Connected -= connectedHandler;
            runCancellation.Cancel();
            if (run is not null) await AssertCanceledOrCompletedAsync(run, runCancellation);
        }
    }

    private static async Task RunPersistedDmReconnectAsync(HttpClient http, Uri server, BootstrapState state,
        BridgeSettings settings, string botUsername, string ownerThreadId, SessionStore sessions,
        CancellationToken ct)
    {
        var accepted = Channel.CreateUnbounded<ChatRequest>();
        await using var transport = new MattermostWebSocketTransport(sessions);
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

            using var topLevel = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "top-level plain DM after reconnect", ct);
            await AssertNoRequestAsync(accepted.Reader, ct);
            using var unrelatedRoot = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "new unrelated root", ct);
            using var unrelatedReply = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, "unknown root must stay ignored", ct,
                unrelatedRoot.RootElement.GetProperty("id").GetString());
            await AssertNoRequestAsync(accepted.Reader, ct);

            const string continuedText = "persisted continuation keeps exact text: 日本語   preserved";
            using var continuedPost = await PostAsync(http, server, state.MemberUser.Token,
                settings.MattermostChannelId!, continuedText, ct, ownerThreadId);
            var continuation = await accepted.Reader.ReadAsync(ct).AsTask().WaitAsync(OperationTimeout, ct);
            Assert.Equal(continuedPost.RootElement.GetProperty("id").GetString(), continuation.MessageId);
            Assert.Equal(ownerThreadId, continuation.ThreadId);
            Assert.Equal(state.MemberUser.Id, continuation.UserId);
            Assert.Equal(continuedText, continuation.Text);
        }
        finally
        {
            transport.Connected -= connectedHandler;
            runCancellation.Cancel();
            if (run is not null) await AssertCanceledOrCompletedAsync(run, runCancellation);
        }
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
            if (run is not null) await AssertCanceledOrCompletedAsync(run, runCancellation);
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

    private static async Task AssertCanceledOrCompletedAsync(Task task, CancellationTokenSource cancellation)
    {
        Assert.True(cancellation.IsCancellationRequested);
        try { await task.WaitAsync(OperationTimeout); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cab-mm-bot-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
