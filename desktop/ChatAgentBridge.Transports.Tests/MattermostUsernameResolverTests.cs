using System.Net;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostUsernameResolverTests
{
    [Fact]
    public async Task Resolves_exact_username_and_sends_bearer_token()
    {
        HttpRequestMessage? captured = null;
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse("""
                {"id":"0123456789abcdefghijklmnop","username":"alice","first_name":"Alice","last_name":"Example","is_bot":false,"delete_at":0}
                """);
        }));

        var result = await resolver.ResolveAsync("https://chat.example.test/", "secret-token", "@alice");

        Assert.Equal("0123456789abcdefghijklmnop", result.Id);
        Assert.Equal("alice", result.Username);
        Assert.Equal("Alice Example", result.DisplayName);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal("https://chat.example.test/api/v4/users/username/alice", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Escapes_username_path_segment()
    {
        Uri? requested = null;
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return JsonResponse("""{"id":"0123456789abcdefghijklmnop","username":"a.b","is_bot":false,"delete_at":0}""");
        }));

        await resolver.ResolveAsync("https://chat.example.test", "token", "a.b");

        Assert.Equal("https://chat.example.test/api/v4/users/username/a.b", requested!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MattermostUsernameLookupFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, MattermostUsernameLookupFailure.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, MattermostUsernameLookupFailure.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, MattermostUsernameLookupFailure.Network)]
    public async Task Maps_http_failures_without_exposing_response_body(HttpStatusCode status, MattermostUsernameLookupFailure expected)
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ =>
            new HttpResponseMessage(status) { Content = new StringContent("private server details") }));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice"));

        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("private server details", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":"0123456789abcdefghijklmnop","username":"alice","is_bot":true,"delete_at":0}""")]
    [InlineData("""{"id":"0123456789abcdefghijklmnop","username":"alice","is_bot":false,"delete_at":123}""")]
    public async Task Rejects_bots_and_deleted_accounts(string body)
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => JsonResponse(body)));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice"));

        Assert.Equal(MattermostUsernameLookupFailure.NotAnActivePerson, error.Failure);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"id":"different","username":"bob","is_bot":false,"delete_at":0}""")]
    [InlineData("""{"username":"alice","is_bot":false,"delete_at":0}""")]
    [InlineData("""{"id":"first,second","username":"alice","is_bot":false,"delete_at":0}""")]
    [InlineData("""{"id":"short","username":"alice","is_bot":false,"delete_at":0}""")]
    public async Task Rejects_malformed_or_authority_mismatched_users(string body)
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice"));

        Assert.Equal(MattermostUsernameLookupFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData("http://chat.example.test", "alice", MattermostUsernameLookupFailure.InvalidServerUrl)]
    [InlineData("https://chat.example.test", "", MattermostUsernameLookupFailure.InvalidUsername)]
    [InlineData("https://chat.example.test", "alice/bob", MattermostUsernameLookupFailure.InvalidUsername)]
    [InlineData("https://chat.example.test", "..", MattermostUsernameLookupFailure.InvalidUsername)]
    public async Task Rejects_invalid_url_or_username(string url, string username, MattermostUsernameLookupFailure expected)
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("The request must not be sent.")));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() => resolver.ResolveAsync(url, "token", username));

        Assert.Equal(expected, error.Failure);
    }

    [Fact]
    public async Task Rejects_response_that_could_inject_another_allowlist_id()
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => JsonResponse(
            """{"id":"0123456789abcdefghijklmnop,another-id","username":"alice","is_bot":false,"delete_at":0}""")));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice"));

        Assert.Equal(MattermostUsernameLookupFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public void Default_handler_disables_redirects()
    {
        using var handler = MattermostUsernameResolver.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Maps_network_and_timeout_failures()
    {
        using var networkResolver = new MattermostUsernameResolver(new DelegateHandler(_ => throw new HttpRequestException()), TimeSpan.FromSeconds(1));
        var network = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            networkResolver.ResolveAsync("https://chat.example.test", "token", "alice"));
        Assert.Equal(MattermostUsernameLookupFailure.Network, network.Failure);

        using var slowResolver = new MattermostUsernameResolver(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        }), TimeSpan.FromMilliseconds(20));
        var timeout = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            slowResolver.ResolveAsync("https://chat.example.test", "token", "alice"));
        Assert.Equal(MattermostUsernameLookupFailure.Timeout, timeout.Failure);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice", cancellation.Token));
    }

    [Fact]
    public async Task Rejects_invalid_authorization_header_without_echoing_token()
    {
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("The request must not be sent.")));

        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "bad\ntoken", "alice"));

        Assert.Equal(MattermostUsernameLookupFailure.InvalidToken, error.Failure);
        Assert.DoesNotContain("bad", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_an_oversized_otherwise_valid_user_response()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "0123456789abcdefghijklmnop", username = "alice",
            first_name = new string('a', 70 * 1024), is_bot = false, delete_at = 0
        });
        using var resolver = new MattermostUsernameResolver(new DelegateHandler(_ => JsonResponse(body)));
        var error = await Assert.ThrowsAsync<MattermostUsernameLookupException>(() =>
            resolver.ResolveAsync("https://chat.example.test", "token", "alice"));
        Assert.Equal(MattermostUsernameLookupFailure.InvalidResponse, error.Failure);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _respond(request, cancellationToken);
    }
}
