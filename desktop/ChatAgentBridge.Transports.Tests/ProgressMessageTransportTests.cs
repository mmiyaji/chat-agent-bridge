using System.Net;
using System.Text;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ProgressMessageTransportTests
{
    private static readonly ChatRequest Request = new("mattermost", "channel-1", "root-1", "user-1", "prompt", "input-post-1");

    [Fact]
    public async Task Mattermost_posts_and_updates_its_own_progress_post()
    {
        var handler = new RecordingHandler(
            _ => Json(HttpStatusCode.Created, "{\"id\":\"progress-post-7\",\"channel_id\":\"channel-1\",\"root_id\":\"root-1\"}"),
            _ => Json(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.test/"), "secret", "channel-1");

        var handle = await transport.PostProgressAsync(Request, "Starting", default);
        await transport.UpdateProgressAsync(Request, handle, "Finished", default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://mattermost.test/api/v4/posts", handler.Requests[0].Uri!.ToString());
        Assert.Contains("\"channel_id\":\"channel-1\"", handler.Requests[0].Body);
        Assert.Contains("\"root_id\":\"root-1\"", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("https://mattermost.test/api/v4/posts/progress-post-7/patch", handler.Requests[1].Uri!.ToString());
        Assert.DoesNotContain("input-post-1", handler.Requests[1].Uri!.ToString());
        Assert.Contains("\"message\":\"Finished\"", handler.Requests[1].Body);
        Assert.All(handler.Requests, call => Assert.Equal("Bearer secret", call.Authorization));
    }

    [Fact]
    public async Task Slack_posts_and_updates_its_own_progress_message()
    {
        var handler = new RecordingHandler(
            _ => Json(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"1712345678.000200\",\"channel\":\"channel-1\",\"message\":{\"thread_ts\":\"root-1\"}}"),
            _ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");
        var request = Request with { Provider = "slack" };

        var handle = await transport.PostProgressAsync(request, "Starting", default);
        await transport.UpdateProgressAsync(request, handle, "Finished", default);

        Assert.Equal("chat.postMessage", handler.Requests[0].Uri!.Segments[^1]);
        Assert.Contains("\"thread_ts\":\"root-1\"", handler.Requests[0].Body);
        Assert.Equal("chat.update", handler.Requests[1].Uri!.Segments[^1]);
        Assert.Contains("\"ts\":\"1712345678.000200\"", handler.Requests[1].Body);
        Assert.DoesNotContain("input-post-1", handler.Requests[1].Body);
        Assert.All(handler.Requests, call => Assert.Equal("Bearer secret", call.Authorization));
    }

    [Fact]
    public async Task Handles_from_other_owner_or_request_scope_are_rejected_before_http()
    {
        var handler1 = new RecordingHandler(_ => Json(HttpStatusCode.Created, "{\"id\":\"progress-1\",\"channel_id\":\"channel-1\",\"root_id\":\"root-1\"}"));
        var handler2 = new RecordingHandler();
        using var http1 = new HttpClient(handler1);
        using var http2 = new HttpClient(handler2);
        var owner = new MattermostWebSocketTransport(http1, new Uri("https://mattermost.test/"), "secret", "channel-1");
        var other = new MattermostWebSocketTransport(http2, new Uri("https://mattermost.test/"), "secret", "channel-1");
        var handle = await owner.PostProgressAsync(Request, "Starting", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => other.UpdateProgressAsync(Request, handle, "No", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.UpdateProgressAsync(Request with { ThreadId = "other-root" }, handle, "No", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.UpdateProgressAsync(Request with { UserId = "other-user" }, handle, "No", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.UpdateProgressAsync(Request with { ChannelId = "other-channel" }, handle, "No", default));
        Assert.Empty(handler2.Requests);
        Assert.Single(handler1.Requests);
    }

    [Fact]
    public async Task Reconnect_invalidates_old_handles_before_http()
    {
        var handler = new RecordingHandler(
            _ => Json(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"progress-1\",\"channel\":\"channel-1\",\"message\":{\"thread_ts\":\"root-1\"}}"),
            _ => Json(HttpStatusCode.OK, "{\"ok\":true,\"user_id\":\"bot-1\"}"),
            _ => Json(HttpStatusCode.OK, "{\"ok\":true,\"url\":\"wss://slack.test/socket\"}"));
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");
        var request = Request with { Provider = "slack" };
        var handle = await transport.PostProgressAsync(request, "Starting", default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.RunAsync(
            new ChatAgentBridge.Core.BridgeSettings { SlackBotToken = "token", SlackAppToken = "app-token" },
            (_, _) => Task.CompletedTask, new CancellationToken(canceled: true)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UpdateProgressAsync(request, handle, "No", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Api_failures_do_not_echo_credentials()
    {
        const string secret = "top-secret-token";
        var mmHandler = new RecordingHandler(_ => Json(HttpStatusCode.InternalServerError, secret));
        using var mmHttp = new HttpClient(mmHandler);
        var mattermost = new MattermostWebSocketTransport(mmHttp, new Uri("https://mattermost.test/"), secret, "channel-1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mattermost.PostProgressAsync(Request, "Starting", default));
        Assert.DoesNotContain(secret, error.ToString());

        var slackHandler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"invalid_auth\"}"));
        using var slackHttp = new HttpClient(slackHandler);
        var slack = new SlackSocketModeTransport(slackHttp, new Uri("https://slack.test/api/"), secret);
        var slackError = await Assert.ThrowsAsync<InvalidOperationException>(() => slack.PostProgressAsync(Request with { Provider = "slack" }, "Starting", default));
        Assert.DoesNotContain(secret, slackError.ToString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(request.Method, request.RequestUri,
                request.Headers.Authorization?.ToString() ?? "", body));
            return _responses.Dequeue()(request);
        }
    }

    private sealed record RequestSnapshot(HttpMethod Method, Uri? Uri, string Authorization, string Body);
}
