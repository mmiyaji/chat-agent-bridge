using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Transports;

public sealed record MattermostThreadIdentity(string RootPostId, string ChannelId);

public enum MattermostThreadLookupFailure
{
    InvalidServerUrl,
    InvalidLink,
    MissingCredentials,
    InvalidToken,
    Unauthorized,
    Forbidden,
    NotFound,
    WrongChannel,
    DeletedPost,
    Network,
    Timeout,
    InvalidResponse
}

public sealed class MattermostThreadLookupException(MattermostThreadLookupFailure failure) : Exception
{
    public MattermostThreadLookupFailure Failure { get; } = failure;
}

/// <summary>Resolves a Mattermost post permalink to its existing root post in the configured channel.</summary>
public sealed class MattermostThreadResolver : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _lookupTimeout;

    public MattermostThreadResolver() : this(MattermostUsernameResolver.CreateHandler(), TimeSpan.FromSeconds(15)) { }

    internal MattermostThreadResolver(HttpMessageHandler handler, TimeSpan? lookupTimeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _lookupTimeout = lookupTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<MattermostThreadIdentity> ResolveAsync(
        string serverUrl, string token, string configuredChannelId, string permalink,
        CancellationToken cancellationToken = default)
    {
        Uri root;
        try { root = TransportHttp.MattermostBaseUri(serverUrl); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidServerUrl);
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.MissingCredentials);
        if (!IsPostId(configuredChannelId))
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.WrongChannel);

        var postId = ParsePostId(permalink, root);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_lookupTimeout);
        var post = ValidatePost(await GetPostAsync(root, token, postId, timeout.Token, cancellationToken).ConfigureAwait(false), postId, configuredChannelId);

        var rootId = string.IsNullOrEmpty(post.RootId) ? post.Id! : post.RootId;
        if (!IsPostId(rootId) || post.RootId == post.Id)
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidResponse);

        if (rootId != post.Id)
        {
            var rootPost = ValidatePost(await GetPostAsync(root, token, rootId, timeout.Token, cancellationToken).ConfigureAwait(false), rootId, configuredChannelId);
            if (!string.IsNullOrEmpty(rootPost.RootId))
                throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidResponse);
        }

        return new MattermostThreadIdentity(rootId, post.ChannelId!);
    }

    private async Task<MattermostPost?> GetPostAsync(Uri server, string token, string postId,
        CancellationToken timeoutToken, CancellationToken callerToken)
    {
        HttpRequestMessage request;
        try
        {
            request = TransportHttp.AuthenticatedRequest(HttpMethod.Get,
                new Uri(server, $"api/v4/posts/{postId}"), token.Trim());
        }
        catch (FormatException)
        {
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidToken);
        }

        HttpResponseMessage response;
        try
        {
            using (request)
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.Network);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw new MattermostThreadLookupException(MattermostThreadLookupFailure.Network);
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => MattermostThreadLookupFailure.Unauthorized,
                    HttpStatusCode.Forbidden => MattermostThreadLookupFailure.Forbidden,
                    HttpStatusCode.NotFound => MattermostThreadLookupFailure.NotFound,
                    _ => MattermostThreadLookupFailure.Network
                };
                throw new MattermostThreadLookupException(failure);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
                await using var body = new MemoryStream();
                var chunk = new byte[4096];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(), timeoutToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (body.Length + read > 64 * 1024)
                        throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidResponse);
                    await body.WriteAsync(chunk.AsMemory(0, read), timeoutToken).ConfigureAwait(false);
                }
                body.Position = 0;
                return await JsonSerializer.DeserializeAsync<MattermostPost>(body, TransportHttp.JsonOptions, timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                throw new MattermostThreadLookupException(MattermostThreadLookupFailure.Timeout);
            }
            catch (JsonException)
            {
                throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidResponse);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new MattermostThreadLookupException(MattermostThreadLookupFailure.Network);
            }
        }
    }

    private static MattermostPost ValidatePost(MattermostPost? post, string expectedId, string configuredChannelId)
    {
        if (post is null || !IsPostId(post.Id) || post.Id != expectedId || !IsPostId(post.ChannelId) ||
            post.RootId is not null && post.RootId.Length != 0 && !IsPostId(post.RootId))
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidResponse);
        if (post.ChannelId != configuredChannelId)
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.WrongChannel);
        if (post.DeleteAt != 0)
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.DeletedPost);
        return post;
    }

    private static string ParsePostId(string value, Uri configuredServer)
    {
        var input = (value ?? string.Empty).Trim();
        if (IsPostId(input)) return input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var link) ||
            input.Contains('?') || input.Contains('#') ||
            (link.Scheme != Uri.UriSchemeHttps && !(link.Scheme == Uri.UriSchemeHttp && TransportHttp.IsLoopback(link.Host))) ||
            !string.IsNullOrEmpty(link.UserInfo) || !string.IsNullOrEmpty(link.Query) || !string.IsNullOrEmpty(link.Fragment) ||
            !string.Equals(link.Scheme, configuredServer.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(link.Authority, configuredServer.Authority, StringComparison.OrdinalIgnoreCase))
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidLink);

        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || segments[1] != "pl" ||
            !(segments[0] == "_redirect" || segments[0].Length is > 0 and <= 128) || !IsPostId(segments[2]))
            throw new MattermostThreadLookupException(MattermostThreadLookupFailure.InvalidLink);
        return segments[2];
    }

    private static bool IsPostId(string? value) => value is { Length: 26 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    public void Dispose() => _http.Dispose();

    private sealed record MattermostPost(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("root_id")] string? RootId,
        [property: JsonPropertyName("delete_at")] long DeleteAt);
}
