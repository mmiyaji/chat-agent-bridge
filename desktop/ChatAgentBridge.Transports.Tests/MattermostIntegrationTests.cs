using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostIntegrationTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Mattermost_WebSocketTransport_ReceivesFiltersRepliesAndReconnects()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_NATIVE_MATTERMOST_E2E"), "1", StringComparison.Ordinal))
            return;

        var statePath = Environment.GetEnvironmentVariable("CAB_NATIVE_MATTERMOST_STATE_FILE");
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
            throw new InvalidOperationException("Native Mattermost integration state file is missing.");
        var state = await LoadStateAsync(statePath);
        var settings = new BridgeSettings
        {
            MattermostServerUrl = state.ServerUrl,
            MattermostChannelId = state.Channel.Id,
            MattermostToken = state.BridgeUser.Token,
            MattermostAllowedUserIds = state.MemberUser.Id
        };

        using var http = new HttpClient { Timeout = OperationTimeout };
        using var cancellation = new CancellationTokenSource(OperationTimeout + TimeSpan.FromSeconds(10));
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
                (request, token) => accepted.Writer.WriteAsync(request, token).AsTask(), firstRunCancellation.Token);
            await firstConnected.Task.WaitAsync(OperationTimeout, cancellation.Token);

            var otherChannelId = await EnsureOtherMemberChannelAsync(http, state, cancellation.Token);
            await PostAsync(http, state, state.BridgeUser.Token, state.Channel.Id, "!cab self message", null, cancellation.Token);
            await PostAsync(http, state, state.MemberUser.Token, state.Channel.Id, "ordinary message", null, cancellation.Token);
            await PostAsync(http, state, state.MemberUser.Token, otherChannelId, "!cab wrong channel", null, cancellation.Token);
            if (!string.IsNullOrWhiteSpace(state.UnauthorizedUser.Token))
                await PostAsync(http, state, state.UnauthorizedUser.Token, state.Channel.Id, "!cab unlisted user", null, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token);
            Assert.False(accepted.Reader.TryRead(out _));

            const string incomingText = "!cab UTF-8 check: 日本語と🌊";
            using var incomingPost = await PostAsync(http, state, state.MemberUser.Token, state.Channel.Id, incomingText, null, cancellation.Token);
            var postId = incomingPost.RootElement.GetProperty("id").GetString()!;
            var request = await accepted.Reader.ReadAsync(cancellation.Token).AsTask().WaitAsync(OperationTimeout, cancellation.Token);
            Assert.Equal("mattermost", request.Provider);
            Assert.Equal(state.Channel.Id, request.ChannelId);
            Assert.Equal(postId, request.ThreadId);
            Assert.Equal("UTF-8 check: 日本語と🌊", request.Text);

            const string replyText = "Reply UTF-8: 返信できました 🟢";
            await transport.ReplyAsync(request, replyText, cancellation.Token);
            using var thread = await GetThreadAsync(http, state.ServerUrl, state.BridgeUser.Token, postId, cancellation.Token);
            Assert.Contains(thread.RootElement.GetProperty("posts").EnumerateObject(), item =>
                item.Value.TryGetProperty("message", out var message) && message.GetString() == replyText &&
                item.Value.TryGetProperty("root_id", out var rootId) && rootId.GetString() == postId);

            const string noticeText = "Chat Agent Bridge: integration connection notice";
            await transport.NotifyConnectionAsync(settings, noticeText, cancellation.Token);

            transport.Connected -= firstConnectedHandler;
            firstRunCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun.WaitAsync(OperationTimeout));

            transport.Connected += secondConnectedHandler;
            secondRun = transport.RunAsync(settings,
                (requestItem, token) => accepted.Writer.WriteAsync(requestItem, token).AsTask(), secondRunCancellation.Token);
            await secondConnected.Task.WaitAsync(OperationTimeout, cancellation.Token);
            await PostAsync(http, state, state.MemberUser.Token, state.Channel.Id, "!cab after reconnect", null, cancellation.Token);
            var reconnected = await accepted.Reader.ReadAsync(cancellation.Token).AsTask().WaitAsync(OperationTimeout, cancellation.Token);
            Assert.Equal("after reconnect", reconnected.Text);
            transport.Connected -= secondConnectedHandler;
            secondRunCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRun.WaitAsync(OperationTimeout));

            await using var invalidTransport = new MattermostWebSocketTransport();
            const string invalidToken = "invalid-native-integration-token";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invalidTransport.RunAsync(
                settings with { MattermostToken = invalidToken }, static (_, _) => Task.CompletedTask, cancellation.Token));
            Assert.DoesNotContain(invalidToken, error.Message, StringComparison.Ordinal);
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

    private static async Task<BootstrapState> LoadStateAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<BootstrapState>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (state is null || string.IsNullOrWhiteSpace(state.ServerUrl) || string.IsNullOrWhiteSpace(state.Channel?.Id) ||
            string.IsNullOrWhiteSpace(state.Team?.Id) || string.IsNullOrWhiteSpace(state.BridgeUser?.Token) ||
            string.IsNullOrWhiteSpace(state.BridgeUser.Id) || string.IsNullOrWhiteSpace(state.MemberUser?.Token) ||
            string.IsNullOrWhiteSpace(state.MemberUser.Id))
            throw new InvalidOperationException("Native Mattermost integration state is incomplete.");
        return state;
    }

    private static async Task<string> EnsureOtherMemberChannelAsync(HttpClient http, BootstrapState state, CancellationToken ct)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(state.ServerUrl.TrimEnd('/') + "/"), $"api/v4/users/{Uri.EscapeDataString(state.MemberUser.Id)}/teams/{Uri.EscapeDataString(state.Team.Id)}/channels")))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.MemberUser.Token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Mattermost integration channel listing failed.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var channels = await JsonSerializer.DeserializeAsync<List<MattermostChannel>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            var existing = channels?.FirstOrDefault(channel => !string.IsNullOrWhiteSpace(channel.Id) && channel.Id != state.Channel.Id);
            if (existing is not null) return existing.Id;
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(state.ServerUrl.TrimEnd('/') + "/"), "api/v4/channels"))
        {
            Content = JsonContent.Create(new { team_id = state.Team.Id, name = "cab-winui-filter", display_name = "CAB WinUI Filter", type = "O" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.BridgeUser.Token);
        using var created = await http.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost integration channel creation failed with HTTP {(int)created.StatusCode}.");
        using var createdJson = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var otherId = createdJson.RootElement.GetProperty("id").GetString()!;

        using var join = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(state.ServerUrl.TrimEnd('/') + "/"), $"api/v4/channels/{Uri.EscapeDataString(otherId)}/members"))
        {
            Content = JsonContent.Create(new { user_id = state.MemberUser.Id })
        };
        join.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.BridgeUser.Token);
        using var joined = await http.SendAsync(join, ct);
        if (!joined.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost integration channel membership failed with HTTP {(int)joined.StatusCode}.");
        return otherId;
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, BootstrapState state, string token,
        string channelId, string message, string? rootId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(state.ServerUrl.TrimEnd('/') + "/"), "api/v4/posts"))
        {
            Content = JsonContent.Create(new { channel_id = channelId, message, root_id = rootId ?? "" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost integration post failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<JsonDocument> GetThreadAsync(HttpClient http, string serverUrl, string token, string postId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(serverUrl.TrimEnd('/') + "/"), $"api/v4/posts/{Uri.EscapeDataString(postId)}/thread"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost integration thread lookup failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
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
        public MattermostTeam Team { get; set; } = new();
        public MattermostChannel Channel { get; set; } = new();
        public BootstrapUser BridgeUser { get; set; } = new();
        public BootstrapUser MemberUser { get; set; } = new();
        public BootstrapUser UnauthorizedUser { get; set; } = new();
    }

    private sealed class MattermostTeam { public string Id { get; set; } = ""; }
    private sealed class MattermostChannel { public string Id { get; set; } = ""; }
    private sealed class BootstrapUser { public string Id { get; set; } = ""; public string Token { get; set; } = ""; }
}
