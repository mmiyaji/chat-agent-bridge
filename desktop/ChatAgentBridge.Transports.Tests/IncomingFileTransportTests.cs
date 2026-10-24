using System.Net;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class IncomingFileTransportTests
{
    [Fact]
    public async Task Slack_rejects_a_file_shared_to_another_message_before_downloading()
    {
        var handler = new RouteHandler((request, _) => Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":2,"mimetype":"text/plain","url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.8"}]}}}}""")));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 2, "text/plain")] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Slack_rejects_a_file_shared_to_the_same_message_in_a_different_thread()
    {
        var handler = new RouteHandler((_, _) => Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":2,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"7.0"}]}}}}""")));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 2, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Slack_rejects_unapproved_download_origin_before_sending_authorization()
    {
        var handler = new RouteHandler((_, _) => Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":2,"url_private_download":"https://attacker.example/file","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}""")));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "secret-token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 2, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("Bearer secret-token", handler.AuthorizationSeen);
    }

    [Fact]
    public async Task Slack_rejects_external_remote_file_even_when_download_host_is_allowed()
    {
        var handler = new RouteHandler((_, _) => Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"external","is_external":true,"filetype":"remote","name":"a.txt","size":2,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}""")));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 2, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Slack_resolves_canonical_metadata_and_downloads_from_approved_host()
    {
        var handler = new RouteHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "slack.com"
            ? JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"canonical.txt","size":3,"mimetype":"text/plain","url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) }));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "event-name.txt", 3, null)] };
        var resolved = Assert.Single(await transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(new IncomingChatFile("F1", "canonical.txt", 3, "text/plain"), resolved);
        using var destination = new MemoryStream();

        await transport.DownloadFileAsync(request, resolved, destination, 3, CancellationToken.None);

        Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(3, handler.RequestCount);
        Assert.Contains(handler.Requests, r => r.Host == "files.slack.com" && r.Authorization == "Bearer token");
    }

    [Fact]
    public async Task Slack_rejects_redirect_from_approved_download_host_without_following_or_leaking_token()
    {
        var handler = new RouteHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "slack.com"
            ? JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}""")
            : new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://attacker.example/file") } }));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "secret-token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 3, null)] };
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, request.IncomingFiles[0], destination, 4, CancellationToken.None));

        Assert.Equal(2, handler.RequestCount);
        Assert.DoesNotContain(handler.Requests, r => r.Host == "attacker.example");
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task Revoked_live_access_rejects_incoming_file_before_http()
    {
        var handler = new RouteHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        transport.UpdateAccessSettings(new BridgeSettings { SlackAllowedUserIds = "U1", SlackAllowedChannelIds = "C2" });
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 1, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Access_revoked_during_file_metadata_lookup_prevents_binary_request()
    {
        SlackSocketModeTransport? transport = null;
        var handler = new RouteHandler((request, _) =>
        {
            transport!.UpdateAccessSettings(new BridgeSettings { SlackAllowedUserIds = "U1", SlackAllowedChannelIds = "C2" });
            return Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}"""));
        });
        using var http = new HttpClient(handler);
        transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        transport.UpdateAccessSettings(new BridgeSettings { SlackAllowedUserIds = "U1", SlackAllowedChannelIds = "C1" });
        await using var dispose = transport;
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 3, null)] };
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, request.IncomingFiles[0], destination, 4, CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task Mattermost_access_revoked_during_post_association_prevents_binary_request()
    {
        MattermostWebSocketTransport? transport = null;
        var handler = new RouteHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"mime_type":"text/plain","post_id":"post-1"}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/posts/post-1", StringComparison.Ordinal))
            {
                transport!.UpdateAccessSettings(new BridgeSettings { MattermostAllowedUserIds = "U2" });
                return Task.FromResult(JsonResponse("""{"id":"post-1","channel_id":"C1","user_id":"U1","root_id":"root-1","file_ids":["F1"]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) });
        });
        using var http = new HttpClient(handler);
        transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.example/"), "token", "C1");
        transport.UpdateAccessSettings(new BridgeSettings { MattermostAllowedUserIds = "U1" });
        await using var dispose = transport;
        var request = MattermostRequest() with { IncomingFiles = [new IncomingChatFile("F1", string.Empty, 0, null)] };
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, request.IncomingFiles[0], destination, 4, CancellationToken.None));

        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task Slack_download_enforces_max_bytes_even_when_response_length_is_unknown()
    {
        var handler = new RouteHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "slack.com")
                return Task.FromResult(JsonResponse("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":4,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes("12345")) });
        });
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.com/api/"), "token");
        var request = SlackRequest() with { IncomingFiles = [new IncomingChatFile("F1", "a.txt", 4, null)] };
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, request.IncomingFiles[0], destination, 4, CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
        Assert.InRange(destination.Length, 0, 4);
    }

    [Fact]
    public async Task Mattermost_resolves_on_same_server_checks_post_and_downloads_with_bounded_size()
    {
        var handler = new RouteHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"mime_type":"text/plain","post_id":"post-1"}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/posts/post-1", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""{"id":"post-1","channel_id":"C1","user_id":"U1","root_id":"root-1","file_ids":["F1"]}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/F1", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = new HttpClient(handler);
        await using var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.example/"), "token", "C1");
        var request = MattermostRequest() with { IncomingFiles = [new IncomingChatFile("F1", string.Empty, 0, null)] };
        var resolved = await transport.ResolveIncomingFilesAsync(request, CancellationToken.None);
        Assert.Equal(new IncomingChatFile("F1", "a.txt", 3, "text/plain"), Assert.Single(resolved));
        using var destination = new MemoryStream();
        await transport.DownloadFileAsync(request, resolved[0], destination, 3, CancellationToken.None);
        Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(5, handler.RequestCount);

        var unrelated = MattermostRequest() with { MessageId = "post-2", IncomingFiles = request.IncomingFiles };
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(unrelated, CancellationToken.None));
    }

    [Theory]
    [InlineData("other-channel", "U1", "root-1")]
    [InlineData("C1", "other-user", "root-1")]
    [InlineData("C1", "U1", "other-root")]
    public async Task Mattermost_rejects_file_with_correct_post_id_when_post_context_differs(string channel, string user, string root)
    {
        var handler = new RouteHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
            ? JsonResponse("""{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"post_id":"post-1"}""")
            : JsonResponse($$"""{"id":"post-1","channel_id":"{{channel}}","user_id":"{{user}}","root_id":"{{root}}","file_ids":["F1"]}""")));
        using var http = new HttpClient(handler);
        await using var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.example/"), "token", "C1");
        var request = MattermostRequest() with { IncomingFiles = [new IncomingChatFile("F1", string.Empty, 0, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Incoming_file_metadata_response_is_size_bounded()
    {
        var oversized = new ByteArrayContent(new byte[1024 * 1024 + 1]);
        var handler = new RouteHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = oversized }));
        using var http = new HttpClient(handler);
        await using var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.example/"), "token", "C1");
        var request = MattermostRequest() with { IncomingFiles = [new IncomingChatFile("F1", string.Empty, 0, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveIncomingFilesAsync(request, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    private static ChatRequest SlackRequest() => new("slack", "C1", "8.0", "U1", "inspect", "8.2");
    private static ChatRequest MattermostRequest() => new("mattermost", "C1", "root-1", "U1", "inspect", "post-1");
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public string? AuthorizationSeen { get; private set; }
        public List<(string Host, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            AuthorizationSeen ??= request.Headers.Authorization?.ToString();
            Requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            return route(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
