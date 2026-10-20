using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class FileAttachmentTransportTests
{
    private static readonly ChatRequest SlackRequest = new("slack", "channel-1", "1712345678.000100", "user-1", "prompt", "1712345678.000101");
    private static readonly ChatRequest MattermostRequest = SlackRequest with { Provider = "mattermost", ThreadId = "root-1" };

    [Fact]
    public async Task Slack_uploads_raw_bytes_without_authorization_then_completes_in_the_requested_thread()
    {
        var file = Prepared("report.txt", "hello"u8.ToArray());
        var handler = new RecordingHandler(async (request, ct) =>
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("files.getUploadURLExternal", StringComparison.Ordinal))
            {
                Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
                Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                Assert.Contains("filename=report.txt", Encoding.UTF8.GetString(body));
                Assert.Contains("length=5", Encoding.UTF8.GetString(body));
                return Json(HttpStatusCode.OK, "{\"ok\":true,\"upload_url\":\"https://files.slack.com/upload/v1/opaque?sig=abc\",\"file_id\":\"F123\"}");
            }
            if (request.RequestUri.Host == "files.slack.com")
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(5, body.Length);
                Assert.Equal("hello", Encoding.UTF8.GetString(body));
                return Json(HttpStatusCode.OK, "");
            }
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            Assert.EndsWith("files.completeUploadExternal", request.RequestUri.AbsolutePath);
            var json = Encoding.UTF8.GetString(body);
            Assert.Contains("\"channel_id\":\"channel-1\"", json);
            Assert.Contains("\"thread_ts\":\"1712345678.000100\"", json);
            Assert.Contains("\"id\":\"F123\"", json);
            return Json(HttpStatusCode.OK, "{\"ok\":true,\"files\":[{\"id\":\"F123\"}]}");
        });
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");

        var ids = await transport.UploadFilesAsync(SlackRequest, [file], default);

        Assert.Equal(new[] { "F123" }, ids);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Slack_rejects_foreign_upload_url_before_sending_file_bytes()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"ok\":true,\"upload_url\":\"https://evil.example/upload\",\"file_id\":\"F123\"}")));
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(SlackRequest, [Prepared("report.txt", [1])], default));

        Assert.Contains("destination", error.Message.ToLowerInvariant());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Slack_missing_scope_has_actionable_sanitized_error_and_does_not_fall_through()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"missing_scope\"}")));
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "sensitive-token");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(SlackRequest, [Prepared("report.txt", [1])], default));

        Assert.Contains("files:write", error.Message);
        Assert.DoesNotContain("sensitive-token", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("wrong-provider")]
    [InlineData("empty")]
    [InlineData("too-many")]
    public async Task Slack_rejects_invalid_request_or_batch_before_http(string scenario)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");
        var request = scenario == "wrong-provider" ? SlackRequest with { Provider = "mattermost" } : SlackRequest;
        IReadOnlyList<PreparedFileAttachment> files = scenario switch
        {
            "empty" => [],
            "too-many" => Enumerable.Range(0, 11).Select(i => Prepared($"{i}.txt", [])).ToArray(),
            _ => [Prepared("report.txt", [])]
        };

        if (scenario == "wrong-provider")
            await Assert.ThrowsAsync<ArgumentException>(() => transport.UploadFilesAsync(request, files, default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(request, files, default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_after_url_response_prevents_the_raw_upload_step()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((request, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":true,\"upload_url\":\"https://files.slack.com/upload/x\",\"file_id\":\"F123\"}"));
        });
        using var http = new HttpClient(handler);
        var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.UploadFilesAsync(SlackRequest, [Prepared("report.txt", [1])], cancellation.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Mattermost_uploads_multipart_then_creates_a_post_with_returned_file_ids()
    {
        var file = Prepared("report.txt", "hello"u8.ToArray());
        var handler = new RecordingHandler(async (request, ct) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            if (request.RequestUri!.AbsolutePath == "/api/v4/files")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("name=channel_id", body);
                Assert.Contains("channel-1", body);
                Assert.Contains("name=files; filename=report.txt", body);
                Assert.Contains("hello", body);
                return Json(HttpStatusCode.OK, "{\"file_infos\":[{\"id\":\"file-1\",\"name\":\"report.txt\",\"size\":5}]}");
            }
            Assert.Equal("/api/v4/posts", request.RequestUri.AbsolutePath);
            Assert.Contains("\"root_id\":\"root-1\"", body);
            Assert.Contains("\"message\":\"\"", body);
            Assert.Contains("\"file_ids\":[\"file-1\"]", body);
            return Json(HttpStatusCode.Created, "{\"id\":\"post-2\",\"channel_id\":\"channel-1\",\"root_id\":\"root-1\"}");
        });
        using var http = new HttpClient(handler);
        var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.test/"), "secret", "channel-1");

        var ids = await transport.UploadFilesAsync(MattermostRequest, [file], default);

        Assert.Equal(new[] { "file-1" }, ids);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Mattermost_rejects_mismatched_uploaded_file_metadata_without_creating_post()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"file_infos\":[{\"id\":\"file-1\",\"name\":\"other.txt\",\"size\":5}]}")));
        using var http = new HttpClient(handler);
        var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.test/"), "secret", "channel-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(MattermostRequest, [Prepared("report.txt", [1, 2, 3, 4, 5])], default));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Mattermost_rejects_channel_and_connection_mismatches_before_http()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.test/"), "secret", "channel-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(MattermostRequest with { ChannelId = "other" }, [Prepared("report.txt", [1])], default));
        await Assert.ThrowsAnyAsync<Exception>(() => transport.UploadFilesAsync(MattermostRequest, [Prepared("report.txt", [1])], new CancellationToken(canceled: true)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Mattermost_http_failure_does_not_create_a_post()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Forbidden, "private response")));
        using var http = new HttpClient(handler);
        var transport = new MattermostWebSocketTransport(http, new Uri("https://mattermost.test/"), "secret", "channel-1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.UploadFilesAsync(MattermostRequest, [Prepared("report.txt", [1])], default));

        Assert.DoesNotContain("private response", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Both_transports_reject_file_uploads_when_disconnected()
    {
        var slackHandler = new RecordingHandler();
        using var slackHttp = new HttpClient(slackHandler);
        var slack = new SlackSocketModeTransport(slackHttp, new Uri("https://slack.test/api/"));
        var mattermost = new MattermostWebSocketTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(() => slack.UploadFilesAsync(SlackRequest, [Prepared("report.txt", [1])], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mattermost.UploadFilesAsync(MattermostRequest, [Prepared("report.txt", [1])], default));

        Assert.Empty(slackHandler.Requests);
    }

    [Fact]
    public async Task Unsafe_file_names_are_rejected_before_network_access()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var slack = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => slack.UploadFilesAsync(SlackRequest, [Prepared("..\\secrets.txt", [1])], default));

        Assert.Empty(handler.Requests);
    }

    private static PreparedFileAttachment Prepared(string name, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var constructor = typeof(PreparedFileAttachment).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, new[] { typeof(string), typeof(string), typeof(byte[]), typeof(string) }, modifiers: null)!;
        return (PreparedFileAttachment)constructor.Invoke(new object?[] { name, name, bytes, hash });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler() : this((_, _) => throw new InvalidOperationException("Unexpected HTTP request.")) { }
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await _send(request, cancellationToken);
        }
    }
}
