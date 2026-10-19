using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Transports;

public sealed record MattermostUserIdentity(string Id, string Username, string DisplayName);

public enum MattermostUsernameLookupFailure
{
    InvalidServerUrl,
    InvalidUsername,
    MissingCredentials,
    InvalidToken,
    Unauthorized,
    Forbidden,
    NotFound,
    Network,
    Timeout,
    InvalidResponse,
    NotAnActivePerson
}

public sealed class MattermostUsernameLookupException(MattermostUsernameLookupFailure failure) : Exception
{
    public MattermostUsernameLookupFailure Failure { get; } = failure;
}

/// <summary>Resolves one exact Mattermost username to its internal user ID.</summary>
public sealed class MattermostUsernameResolver : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _lookupTimeout;

    public MattermostUsernameResolver()
        : this(CreateHandler(), TimeSpan.FromSeconds(15))
    {
    }

    internal MattermostUsernameResolver(HttpMessageHandler handler, TimeSpan? lookupTimeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        _lookupTimeout = lookupTimeout ?? TimeSpan.FromSeconds(15);
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<MattermostUserIdentity> ResolveAsync(
        string serverUrl, string token, string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.MissingCredentials);

        var exactUsername = NormalizeUsername(username);
        Uri root;
        try { root = TransportHttp.MattermostBaseUri(serverUrl); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidServerUrl);
        }

        var requestUri = new Uri(root, $"api/v4/users/username/{Uri.EscapeDataString(exactUsername)}");
        HttpRequestMessage request;
        try { request = TransportHttp.AuthenticatedRequest(HttpMethod.Get, requestUri, token.Trim()); }
        catch (FormatException)
        {
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidToken);
        }
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_lookupTimeout);
            return await SendAsync(request, exactUsername, timeout.Token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MattermostUserIdentity> SendAsync(HttpRequestMessage request, string exactUsername,
        CancellationToken timeoutToken, CancellationToken callerToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.Network);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.Network);
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => MattermostUsernameLookupFailure.Unauthorized,
                    HttpStatusCode.Forbidden => MattermostUsernameLookupFailure.Forbidden,
                    HttpStatusCode.NotFound => MattermostUsernameLookupFailure.NotFound,
                    _ => MattermostUsernameLookupFailure.Network
                };
                throw new MattermostUsernameLookupException(failure);
            }

            MattermostUser? user;
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
                        throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidResponse);
                    await body.WriteAsync(chunk.AsMemory(0, read), timeoutToken).ConfigureAwait(false);
                }
                body.Position = 0;
                user = await JsonSerializer.DeserializeAsync<MattermostUser>(body, TransportHttp.JsonOptions, timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.Timeout);
            }
            catch (JsonException)
            {
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidResponse);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.Network);
            }

            if (user is null || string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.Username) ||
                !string.Equals(user.Username, exactUsername, StringComparison.OrdinalIgnoreCase))
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidResponse);
            if (!IsValidUserId(user.Id))
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidResponse);
            if (user.IsBot || user.DeleteAt != 0)
                throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.NotAnActivePerson);

            var displayName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = user.Nickname?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = user.Username;
            return new MattermostUserIdentity(user.Id, user.Username, displayName);
        }
    }

    private static bool IsValidUserId(string id) => id.Length == 26 &&
        id.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static string NormalizeUsername(string username)
    {
        var value = (username ?? string.Empty).Trim();
        if (value.StartsWith('@')) value = value[1..];
        if (value.Length is < 1 or > 64 || value is "." or ".." || value.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new MattermostUsernameLookupException(MattermostUsernameLookupFailure.InvalidUsername);
        return value;
    }

    public void Dispose() => _http.Dispose();

    private sealed record MattermostUser(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("nickname")] string? Nickname,
        [property: JsonPropertyName("is_bot")] bool IsBot,
        [property: JsonPropertyName("delete_at")] long DeleteAt);
}
