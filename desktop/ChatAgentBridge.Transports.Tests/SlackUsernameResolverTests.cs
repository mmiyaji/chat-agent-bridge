using System.Net;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class SlackUsernameResolverTests
{
    [Fact]
    public async Task Resolves_exact_username_across_cursor_pages_and_returns_workspace_identity()
    {
        var calls = new List<string>();
        using var resolver = new SlackUsernameResolver(new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
                ? Json("""{"ok":true,"team_id":"T123456789","team":"Example Team","url":"https://example.slack.com/"}""")
                : request.RequestUri.Query.Contains("cursor=next", StringComparison.Ordinal)
                    ? Json("""{"ok":true,"members":[{"id":"U123456789","team_id":"T123456789","name":"alice","real_name":"Alice Example","deleted":false,"is_bot":false}],"response_metadata":{"next_cursor":""}}""")
                    : Json("""{"ok":true,"members":[{"id":"U000000001","team_id":"T123456789","name":"other","deleted":false,"is_bot":false}],"response_metadata":{"next_cursor":"next"}}""");
        }));

        var result = await resolver.ResolveAsync("token", "@alice");

        Assert.Equal(new SlackUserIdentity("U123456789", "alice", "Alice Example", "T123456789", "Example Team"), result);
        Assert.Equal(3, calls.Count);
        Assert.Contains("users.list", calls[1], StringComparison.Ordinal);
        Assert.Contains("cursor=next", calls[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolves_member_id_directly_and_does_not_list_users()
    {
        using var resolver = new SlackUsernameResolver(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
            ? """{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}"""
            : """{"ok":true,"user":{"id":"U123456789","team_id":"T123456789","name":"alice","real_name":"Alice","deleted":false,"is_bot":false}}""")));

        var result = await resolver.ResolveAsync("token", "U123456789");

        Assert.Equal("alice", result.Name);
        Assert.Equal("Alice", result.RealName);
    }

    [Fact]
    public async Task Reports_missing_users_read_scope_without_response_details()
    {
        using var resolver = new SlackUsernameResolver(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
            ? """{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}"""
            : """{"ok":false,"error":"missing_scope","needed":"users:read","provided":"app_mentions:read"}""")));

        var error = await Assert.ThrowsAsync<SlackUsernameLookupException>(() => resolver.ResolveAsync("token", "alice"));

        Assert.Equal(SlackUsernameLookupFailure.MissingScope, error.Failure);
        Assert.Equal("users:read", error.RequiredScope);
        Assert.DoesNotContain("provided", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_guess_duplicate_exact_names()
    {
        using var resolver = new SlackUsernameResolver(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
            ? """{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}"""
            : """{"ok":true,"members":[{"id":"U123456789","team_id":"T123456789","name":"alice"},{"id":"U987654321","team_id":"T123456789","name":"alice"}],"response_metadata":{"next_cursor":""}}""")));

        var error = await Assert.ThrowsAsync<SlackUsernameLookupException>(() => resolver.ResolveAsync("token", "alice"));

        Assert.Equal(SlackUsernameLookupFailure.Ambiguous, error.Failure);
    }

    [Fact]
    public async Task Resolves_exact_display_name_with_internal_spaces_and_returns_canonical_identity()
    {
        using var resolver = new SlackUsernameResolver(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
            ? """{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}"""
            : """{"ok":true,"members":[{"id":"U123456789","team_id":"T123456789","name":"ruhenheim","real_name":"Mail User","deleted":false,"is_bot":false,"profile":{"display_name":"Ruhenheim Mail"}}],"response_metadata":{"next_cursor":""}}""")));

        var result = await resolver.ResolveAsync("token", "  ruhenheim mail  ");

        Assert.Equal("ruhenheim", result.Name);
        Assert.Equal("Mail User", result.RealName);
        Assert.Equal("U123456789", result.Id);
    }

    [Fact]
    public async Task Display_name_collision_with_another_members_username_is_ambiguous()
    {
        using var resolver = new SlackUsernameResolver(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal)
            ? """{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}"""
            : """{"ok":true,"members":[{"id":"U123456789","team_id":"T123456789","name":"sam"},{"id":"U987654321","team_id":"T123456789","name":"other","profile":{"display_name":"Sam"}}],"response_metadata":{"next_cursor":""}}""")));

        var error = await Assert.ThrowsAsync<SlackUsernameLookupException>(() => resolver.ResolveAsync("token", "Sam"));

        Assert.Equal(SlackUsernameLookupFailure.Ambiguous, error.Failure);
    }

    [Fact]
    public async Task Rejects_invalid_query_before_networking()
    {
        using var resolver = new SlackUsernameResolver(new Handler(_ => throw new Xunit.Sdk.XunitException("Unexpected request.")));
        foreach (var query in new[] { "alice@bob", "@alice/bob", "alice\nsmith" })
        {
            var error = await Assert.ThrowsAsync<SlackUsernameLookupException>(() => resolver.ResolveAsync("token", query));
            Assert.Equal(SlackUsernameLookupFailure.InvalidUsername, error.Failure);
        }
    }

    [Theory]
    [InlineData("USLACKBOT", "slackbot", false, false)]
    [InlineData("U123456789", "app", false, true)]
    public async Task Rejects_slackbot_and_app_user_accounts(string id, string name, bool isBot, bool isAppUser)
    {
        using var resolver = new SlackUsernameResolver(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal))
                return Json("""{"ok":true,"team_id":"T123456789","team":"Workspace","url":"https://team.slack.com/"}""");
            return Json(JsonSerializer.Serialize(new
            {
                ok = true,
                user = new { id, team_id = "T123456789", name, deleted = false, is_bot = isBot, is_app_user = isAppUser }
            }));
        }));

        var error = await Assert.ThrowsAsync<SlackUsernameLookupException>(() => resolver.ResolveAsync("token", id));

        Assert.Equal(SlackUsernameLookupFailure.InvalidResponse, error.Failure);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
