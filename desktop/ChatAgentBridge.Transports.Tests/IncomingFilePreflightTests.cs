using System.Net;
using System.Text;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports.Tests;

public sealed class IncomingFilePreflightTests
{
    [Fact]
    public async Task Slack_incomplete_event_size_falls_back_to_canonical_preflight()
    {
        var handler = new Handler(_ => Json("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}"""));
        await using var transport = new SlackSocketModeTransport(new HttpClient(handler), new Uri("https://slack.com/api/"), "token");
        var request = new ChatRequest("slack", "C1", "8.0", "U1", "inspect", "8.2")
        { IncomingFiles = [new("F1", "a.txt", 0, null)] };
        var file = Assert.Single(await transport.GetIncomingFileCandidatesAsync(request, default));
        Assert.Equal(3, file.Size);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Slack_preflight_uses_event_metadata_but_download_still_checks_canonical_membership()
    {
        var handler = new Handler(request => request.RequestUri!.Host == "slack.com"
            ? Json("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"a.txt","size":3,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"8.2","thread_ts":"8.0"}]}}}}""")
            : new(HttpStatusCode.OK) { Content = new StringContent("abc") });
        await using var transport = new SlackSocketModeTransport(new HttpClient(handler), new Uri("https://slack.com/api/"), "token");
        var request = new ChatRequest("slack", "C1", "8.0", "U1", "inspect", "8.2")
        { IncomingFiles = [new("F1", "a.txt", 3, null)] };
        var files = await transport.GetIncomingFileCandidatesAsync(request, default);
        Assert.Equal(0, handler.Count);
        using var output = new MemoryStream();
        await transport.DownloadFileAsync(request, Assert.Single(files), output, 3, default);
        Assert.Equal(2, handler.Count); // one fresh files.info, one content GET; no redundant preflight GET
        Assert.Equal("abc", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData("different.txt", "8.2")]
    [InlineData("a.txt", "another-message")]
    public async Task Slack_does_not_download_changed_or_unrelated_event_candidates(string name, string ts)
    {
        var handler = new Handler(_ => Json("""{"ok":true,"file":{"id":"F1","mode":"hosted","is_external":false,"name":"__NAME__","size":3,"url_private_download":"https://files.slack.com/download/F1","shares":{"private":{"C1":[{"ts":"__TS__","thread_ts":"8.0"}]}}}}""".Replace("__NAME__", name).Replace("__TS__", ts)));
        await using var transport = new SlackSocketModeTransport(new HttpClient(handler), new Uri("https://slack.com/api/"), "token");
        var request = new ChatRequest("slack", "C1", "8.0", "U1", "inspect", "8.2")
        { IncomingFiles = [new("F1", "a.txt", 3, null)] };
        var file = Assert.Single(await transport.GetIncomingFileCandidatesAsync(request, default));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, file, output, 3, default));
        Assert.Equal(1, handler.Count);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discord_preflight_avoids_rest_and_rejects_canonical_changes_before_cdn(bool changed)
    {
        var api = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/messages/original", StringComparison.Ordinal)
            ? Json($$"""{"id":"original","channel_id":"parent","author":{"id":"123"},"attachments":[{"id":"file","filename":"{{(changed ? "changed.txt" : "a.txt")}}","size":3,"url":"https://cdn.discordapp.com/attachments/a/b/a.txt"}]}""")
            : Json("""{"id":"parent","guild_id":"456","type":0}"""));
        var cdn = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent("abc") };
        });
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri("https://discord.test/api/v10/"), "token", cdnHttp: new HttpClient(cdn));
        transport.UpdateAccessSettings(new BridgeSettings { DiscordBotToken = "token", DiscordAllowedUserIds = "123", DiscordAllowedGuildIds = "456" });
        var request = new ChatRequest("discord", "parent", "thread", "123", "inspect", "original")
        { GuildId = "456", SourceChannelId = "parent", IncomingFiles = [new("file", "a.txt", 3, null)] };
        var file = Assert.Single(await transport.GetIncomingFileCandidatesAsync(request, default));
        Assert.Equal(0, api.Count);
        using var output = new MemoryStream();
        if (changed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, file, output, 3, default));
            Assert.Equal(0, cdn.Count);
            Assert.Empty(output.ToArray());
        }
        else
        {
            await transport.DownloadFileAsync(request, file, output, 3, default);
            Assert.Equal(1, cdn.Count);
            Assert.Equal("abc", Encoding.UTF8.GetString(output.ToArray()));
        }
        Assert.Equal(2, api.Count);
    }

    [Fact]
    public async Task Mattermost_shares_only_preflight_post_snapshot_and_rechecks_each_download()
    {
        int postReads = 0;
        bool removed = false;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/posts/post-1", StringComparison.Ordinal))
            {
                postReads++;
                return Json($$"""{"id":"post-1","channel_id":"C1","user_id":"U1","root_id":"root-1","file_ids":{{(removed ? "[]" : "[\"F1\",\"F2\"]")}}}""");
            }
            if (path.EndsWith("/info", StringComparison.Ordinal))
            {
                string id = path.Contains("/F1/", StringComparison.Ordinal) ? "F1" : "F2";
                return Json($$"""{"id":"{{id}}","name":"a.txt","size":3,"post_id":"post-1"}""");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("abc") };
        });
        await using var transport = new MattermostWebSocketTransport(new HttpClient(handler), new Uri("https://mattermost.example/"), "token", "C1");
        var request = new ChatRequest("mattermost", "C1", "root-1", "U1", "inspect", "post-1")
        { IncomingFiles = [new("F1", "", 0, null), new("F2", "", 0, null)] };
        var files = await ((IChatIncomingFileTransport)transport).GetIncomingFileCandidatesAsync(request, default);
        Assert.Equal(3, handler.Count);
        Assert.Equal(1, postReads);
        using var first = new MemoryStream();
        await transport.DownloadFileAsync(request, files[0], first, 3, default);
        removed = true;
        using var second = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(request, files[1], second, 3, default));
        Assert.Equal(3, postReads); // fresh membership for each file, including revoked one
        Assert.Empty(second.ToArray());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Count++; return Task.FromResult(respond(request)); }
    }
}
