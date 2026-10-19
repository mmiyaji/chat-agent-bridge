using System.Net;
using System.Text;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostThreadResolverTests
{
    private const string Root = "0123456789abcdefghijklmnop";
    private const string Reply = "1123456789abcdefghijklmnop";
    private const string Other = "2123456789abcdefghijklmnop";
    private const string Channel = "3123456789abcdefghijklmnop";

    [Theory]
    [InlineData("https://chat.example.test/team/pl/1123456789abcdefghijklmnop")]
    [InlineData("https://chat.example.test/_redirect/pl/1123456789abcdefghijklmnop")]
    public async Task Resolves_reply_permalink_to_existing_root(string link)
    {
        var requested = new List<string>();
        using var resolver = new MattermostThreadResolver(new DelegateHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Json(request.RequestUri.AbsolutePath.EndsWith(Reply, StringComparison.Ordinal)
                ? Post(Reply, Channel, Root)
                : Post(Root, Channel));
        }));

        var result = await resolver.ResolveAsync("https://chat.example.test", "token", Channel, link);

        Assert.Equal(new MattermostThreadIdentity(Root, Channel), result);
        Assert.Equal(["https://chat.example.test/api/v4/posts/1123456789abcdefghijklmnop", "https://chat.example.test/api/v4/posts/0123456789abcdefghijklmnop"], requested);
    }

    [Fact]
    public async Task Accepts_a_root_post_link_or_bare_post_id()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => Json(Post(Root, Channel))));

        var fromLink = await resolver.ResolveAsync("https://chat.example.test", "token", Channel,
            "https://chat.example.test/team/pl/0123456789abcdefghijklmnop");
        var fromId = await resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root);

        Assert.Equal(new MattermostThreadIdentity(Root, Channel), fromLink);
        Assert.Equal(fromLink, fromId);
    }

    [Theory]
    [InlineData("http://chat.example.test/team/pl/0123456789abcdefghijklmnop")]
    [InlineData("https://other.example.test/team/pl/0123456789abcdefghijklmnop")]
    [InlineData("https://user@chat.example.test/team/pl/0123456789abcdefghijklmnop")]
    [InlineData("https://chat.example.test/team/pl/0123456789abcdefghijklmnop?x=1")]
    [InlineData("https://chat.example.test/team/pl/0123456789abcdefghijklmnop#x")]
    [InlineData("https://chat.example.test/team/pl/short")]
    [InlineData("https://chat.example.test/team/other/0123456789abcdefghijklmnop")]
    [InlineData("https://chat.example.test/_redirect/pl/0123456789abcdefghijklmnop/extra")]
    public async Task Rejects_malformed_or_cross_server_links_before_networking(string link)
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("No request should be sent.")));

        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, link));

        Assert.Equal(MattermostThreadLookupFailure.InvalidLink, error.Failure);
    }

    [Fact]
    public async Task Rejects_wrong_channel_without_adding_a_thread()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => Json(Post(Root, Other))));
        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root));
        Assert.Equal(MattermostThreadLookupFailure.WrongChannel, error.Failure);
    }

    [Fact]
    public async Task Rejects_deleted_posts()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => Json(Post(Root, Channel, deleted: true))));
        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root));
        Assert.Equal(MattermostThreadLookupFailure.DeletedPost, error.Failure);
    }

    [Fact]
    public async Task Rejects_a_reply_whose_root_is_not_a_root_in_the_same_channel()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(request => Json(
            request.RequestUri!.AbsolutePath.EndsWith(Reply, StringComparison.Ordinal)
                ? Post(Reply, Channel, Root)
                : Post(Root, Channel, Other))));

        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Reply));

        Assert.Equal(MattermostThreadLookupFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task Rejects_a_self_referential_root_id()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => Json(Post(Root, Channel, Root))));

        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root));

        Assert.Equal(MattermostThreadLookupFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task Uses_bearer_auth_and_rejects_redirects()
    {
        HttpRequestMessage? captured = null;
        using (var resolver = new MattermostThreadResolver(new DelegateHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example.test/") } };
        })))
        {
            var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
                resolver.ResolveAsync("https://chat.example.test", "secret", Channel, Root));
            Assert.Equal(MattermostThreadLookupFailure.Network, error.Failure);
        }
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MattermostThreadLookupFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, MattermostThreadLookupFailure.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, MattermostThreadLookupFailure.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, MattermostThreadLookupFailure.Network)]
    public async Task Maps_http_errors_without_exposing_response_body(HttpStatusCode status, MattermostThreadLookupFailure failure)
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("private server response")
        }));

        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root));

        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("private server response", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var resolver = new MattermostThreadResolver(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(Post(Root, Channel));
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", Channel, Root, cancellation.Token));
    }

    private static string Post(string id, string channel, string? rootId = null, bool deleted = false) =>
        $$"""{"id":"{{id}}","channel_id":"{{channel}}","root_id":"{{rootId ?? string.Empty}}","delete_at":{{(deleted ? 1 : 0)}}}""";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _respond(request, cancellationToken);
    }
}
