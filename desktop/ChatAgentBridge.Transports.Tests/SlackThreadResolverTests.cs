using System.Net;
using System.Text;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class SlackThreadResolverTests
{
    private const string Channel = "C123456789";
    private const string RootTs = "1712345678.000001";
    private const string ReplyTs = "1712345678.000008";

    [Fact]
    public async Task Resolves_reply_link_to_api_confirmed_root_and_channel()
    {
        var requests = new List<string>();
        using var resolver = new SlackThreadResolver(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("auth.test", StringComparison.Ordinal)) return Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""");
            if (path.EndsWith("conversations.replies", StringComparison.Ordinal))
                return request.RequestUri.Query.Contains("ts=" + Uri.EscapeDataString(ReplyTs), StringComparison.Ordinal)
                    ? Json($$"""{"ok":true,"messages":[{"ts":"{{ReplyTs}}","thread_ts":"{{RootTs}}"}]}""")
                    : Json($$"""{"ok":true,"messages":[{"ts":"{{RootTs}}"}]}""");
            return Json("""{"ok":false,"error":"missing_scope","needed":"channels:read"}""");
        }));

        var result = await resolver.ResolveAsync("token",
            $"https://example.slack.com/archives/{Channel}/p1712345678000008?thread_ts={Uri.EscapeDataString(RootTs)}&cid={Channel}");

        Assert.Equal(new SlackThreadIdentity(RootTs, Channel, null, "T123456789", "Example"), result);
        Assert.Equal(4, requests.Count);
        Assert.Contains("ts=1712345678.000008", requests[1], StringComparison.Ordinal);
        Assert.Contains("ts=1712345678.000001", requests[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_root_permalink_without_extra_history_request()
    {
        using var resolver = new SlackThreadResolver(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth.test", StringComparison.Ordinal)) return Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""");
            if (path.EndsWith("conversations.replies", StringComparison.Ordinal)) return Json($$"""{"ok":true,"messages":[{"ts":"{{RootTs}}"}]}""");
            return Json("""{"ok":true,"channel":{"id":"C123456789","name":"general"}}""");
        }));

        var result = await resolver.ResolveAsync("token", $"https://example.slack.com/archives/{Channel}/p1712345678000001");

        Assert.Equal(RootTs, result.RootTimestamp);
        Assert.Equal("general", result.ChannelName);
    }

    [Fact]
    public async Task Rejects_link_from_another_workspace_after_authentication()
    {
        using var resolver = new SlackThreadResolver(new Handler(_ => Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""")));
        var error = await Assert.ThrowsAsync<SlackThreadLookupException>(() => resolver.ResolveAsync("token",
            $"https://attacker.slack.com/archives/{Channel}/p1712345678000001"));
        Assert.Equal(SlackThreadLookupFailure.WrongWorkspace, error.Failure);
    }

    [Theory]
    [InlineData("http://example.slack.com/archives/C123456789/p1712345678000001")]
    [InlineData("https://user@example.slack.com/archives/C123456789/p1712345678000001")]
    [InlineData("https://example.slack.com/archives/C123456789/p1712345678000001?cid=C987654321")]
    [InlineData("https://example.slack.com:444/archives/C123456789/p1712345678000001")]
    [InlineData("https://example.slack.com/archives/C123456789/p1712345678000001?extra=1")]
    [InlineData("https://example.slack.com/archives/C123456789/pbad")]
    public async Task Rejects_malformed_links_before_networking(string link)
    {
        using var resolver = new SlackThreadResolver(new Handler(_ => throw new Xunit.Sdk.XunitException("Unexpected request.")));
        var error = await Assert.ThrowsAsync<SlackThreadLookupException>(() => resolver.ResolveAsync("token", link));
        Assert.Equal(SlackThreadLookupFailure.InvalidLink, error.Failure);
    }

    [Fact]
    public async Task Rejects_oversized_link_before_networking()
    {
        using var resolver = new SlackThreadResolver(new Handler(_ => throw new Xunit.Sdk.XunitException("Unexpected request.")));
        var link = $"https://example.slack.com/archives/{Channel}/p1712345678000001?x={new string('a', 2100)}";
        var error = await Assert.ThrowsAsync<SlackThreadLookupException>(() => resolver.ResolveAsync("token", link));
        Assert.Equal(SlackThreadLookupFailure.InvalidLink, error.Failure);
    }

    [Fact]
    public async Task Requires_api_derived_root_to_match_optional_permalink_thread_hint()
    {
        using var resolver = new SlackThreadResolver(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth.test", StringComparison.Ordinal)) return Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""");
            return Json($$"""{"ok":true,"messages":[{"ts":"{{ReplyTs}}","thread_ts":"{{RootTs}}"}]}""");
        }));
        var link = $"https://example.slack.com/archives/{Channel}/p1712345678000008?thread_ts=1712345678.000002";
        var error = await Assert.ThrowsAsync<SlackThreadLookupException>(() => resolver.ResolveAsync("token", link));
        Assert.Equal(SlackThreadLookupFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task Uses_slacks_needed_scope_from_safe_allowlist_instead_of_guessing_from_channel_prefix()
    {
        using var resolver = new SlackThreadResolver(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth.test", StringComparison.Ordinal)) return Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""");
            return Json("""{"ok":false,"error":"missing_scope","needed":"groups:history","provided":"app_mentions:read"}""");
        }));

        var error = await Assert.ThrowsAsync<SlackThreadLookupException>(() => resolver.ResolveAsync("token",
            $"https://example.slack.com/archives/{Channel}/p1712345678000001"));

        Assert.Equal(SlackThreadLookupFailure.MissingScope, error.Failure);
        Assert.Equal("groups:history", error.RequiredScope);
        Assert.DoesNotContain("provided", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Optional_channel_metadata_rate_limit_does_not_discard_verified_root()
    {
        using var resolver = new SlackThreadResolver(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth.test", StringComparison.Ordinal)) return Json("""{"ok":true,"team_id":"T123456789","team":"Example","url":"https://example.slack.com/"}""");
            if (path.EndsWith("conversations.replies", StringComparison.Ordinal)) return Json($$"""{"ok":true,"messages":[{"ts":"{{RootTs}}"}]}""");
            return new HttpResponseMessage((HttpStatusCode)429);
        }));

        var result = await resolver.ResolveAsync("token", $"https://example.slack.com/archives/{Channel}/p1712345678000001");

        Assert.Equal(RootTs, result.RootTimestamp);
        Assert.Equal(Channel, result.ChannelId);
        Assert.Null(result.ChannelName);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
