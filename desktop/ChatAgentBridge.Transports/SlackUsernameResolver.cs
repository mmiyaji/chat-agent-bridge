using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Transports;

public sealed record SlackUserIdentity(string Id, string Name, string RealName, string TeamId, string TeamName);

public enum SlackUsernameLookupFailure
{
    InvalidWorkspaceUrl, InvalidUsername, MissingCredentials, InvalidToken, WrongWorkspace,
    MissingScope, RateLimited, NotFound, Ambiguous, Network, Timeout, InvalidResponse
}

public sealed class SlackUsernameLookupException(SlackUsernameLookupFailure failure, string? requiredScope = null) : Exception
{
    public SlackUsernameLookupFailure Failure { get; } = failure;
    public string? RequiredScope { get; } = requiredScope;
}

/// <summary>Resolves an exact Slack member name or member ID in the selected workspace.</summary>
public sealed class SlackUsernameResolver : IDisposable
{
    private const int MaxPages = 20;
    private readonly HttpClient _http;
    private readonly TimeSpan _lookupTimeout;

    public SlackUsernameResolver() : this(CreateHandler(), TimeSpan.FromSeconds(20)) { }

    internal SlackUsernameResolver(HttpMessageHandler handler, TimeSpan? lookupTimeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _lookupTimeout = lookupTimeout ?? TimeSpan.FromSeconds(20);
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<SlackUserIdentity> ResolveAsync(string token, string usernameOrMemberId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw Error(SlackUsernameLookupFailure.MissingCredentials);
        var query = NormalizeQuery(usernameOrMemberId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_lookupTimeout);
        var auth = await CallAsync<AuthResponse>("auth.test", token, null, timeout.Token, cancellationToken).ConfigureAwait(false);
        var teamId = Required(auth.TeamId);
        var teamName = Required(auth.Team);

        SlackUser? user;
        if (IsMemberId(query))
        {
            var response = await CallAsync<UserResponse>("users.info", token, new Dictionary<string, string> { ["user"] = query }, timeout.Token, cancellationToken).ConfigureAwait(false);
            user = response.User;
        }
        else
        {
            user = await FindExactNameAsync(token, query, timeout.Token, cancellationToken).ConfigureAwait(false);
        }

        if (user is null) throw Error(SlackUsernameLookupFailure.NotFound);
        var identity = ToIdentity(user, teamId, teamName);
        if (!string.Equals(identity.TeamId, teamId, StringComparison.Ordinal))
            throw Error(SlackUsernameLookupFailure.InvalidResponse);
        return identity;
    }

    private async Task<SlackUser?> FindExactNameAsync(string token, string query, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        string? cursor = null;
        SlackUser? match = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var args = new Dictionary<string, string> { ["limit"] = "200" };
            if (!string.IsNullOrEmpty(cursor)) args["cursor"] = cursor;
            var response = await CallAsync<UsersResponse>("users.list", token, args, timeoutToken, callerToken).ConfigureAwait(false);
            if (response.Members is null) throw Error(SlackUsernameLookupFailure.InvalidResponse);
            foreach (var member in response.Members)
            {
                if (member is null || !MatchesName(member, query)) continue;
                if (match is not null && !string.Equals(match.Id, member.Id, StringComparison.Ordinal))
                    throw Error(SlackUsernameLookupFailure.Ambiguous);
                match = member;
            }
            cursor = response.ResponseMetadata?.NextCursor?.Trim();
            if (string.IsNullOrEmpty(cursor)) return match;
        }
        throw Error(SlackUsernameLookupFailure.InvalidResponse);
    }

    private async Task<T> CallAsync<T>(string method, string token, IReadOnlyDictionary<string, string>? args,
        CancellationToken timeoutToken, CancellationToken callerToken)
    {
        var builder = new UriBuilder("https://slack.com/api/" + method);
        if (args is not null && args.Count > 0)
            builder.Query = string.Join("&", args.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var request = method == "auth.test"
            ? new HttpRequestMessage(HttpMethod.Post, builder.Uri)
            : new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        try { request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim()); }
        catch (FormatException) { throw Error(SlackUsernameLookupFailure.InvalidToken); }
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw Error(SlackUsernameLookupFailure.Timeout); }
        catch (HttpRequestException) { throw Error(SlackUsernameLookupFailure.Network); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw Error(SlackUsernameLookupFailure.Network);
            if (response.StatusCode == (HttpStatusCode)429) throw Error(SlackUsernameLookupFailure.RateLimited);
            if (!response.IsSuccessStatusCode)
                throw Error(response.StatusCode == HttpStatusCode.Unauthorized ? SlackUsernameLookupFailure.InvalidToken : SlackUsernameLookupFailure.Network);
            JsonDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
                await using var body = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), timeoutToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (body.Length + read > 1024 * 1024) throw Error(SlackUsernameLookupFailure.InvalidResponse);
                    await body.WriteAsync(buffer.AsMemory(0, read), timeoutToken).ConfigureAwait(false);
                }
                body.Position = 0;
                document = await JsonDocument.ParseAsync(body, cancellationToken: timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw Error(SlackUsernameLookupFailure.Timeout); }
            catch (JsonException) { throw Error(SlackUsernameLookupFailure.InvalidResponse); }
            catch (IOException) { throw Error(SlackUsernameLookupFailure.Network); }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Error(SlackUsernameLookupFailure.InvalidResponse);
                if (!ok.GetBoolean())
                {
                    var apiError = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
                    if (apiError == "missing_scope")
                        throw new SlackUsernameLookupException(SlackUsernameLookupFailure.MissingScope, RequiredScopeFor(method));
                    if (apiError is "invalid_auth" or "not_authed" or "token_revoked" or "token_expired") throw Error(SlackUsernameLookupFailure.InvalidToken);
                    if (apiError is "user_not_found" or "users_not_found") throw Error(SlackUsernameLookupFailure.NotFound);
                    throw Error(SlackUsernameLookupFailure.Network);
                }
                try { return root.Deserialize<T>(TransportHttp.JsonOptions) ?? throw Error(SlackUsernameLookupFailure.InvalidResponse); }
                catch (JsonException) { throw Error(SlackUsernameLookupFailure.InvalidResponse); }
            }
        }
    }

    private static SlackUserIdentity ToIdentity(SlackUser user, string teamId, string teamName)
    {
        if (!IsMemberId(user.Id ?? string.Empty) || string.Equals(user.Id, "USLACKBOT", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(user.Name) || user.Deleted || user.IsBot || user.IsAppUser)
            throw Error(SlackUsernameLookupFailure.InvalidResponse);
        var userTeam = user.TeamId ?? teamId;
        var realName = user.RealName?.Trim();
        if (string.IsNullOrEmpty(realName)) realName = user.Profile?.RealName?.Trim();
        return new SlackUserIdentity(user.Id!, user.Name!.Trim(), realName ?? string.Empty, userTeam, teamName);
    }

    private static string NormalizeQuery(string? value)
    {
        var query = (value ?? string.Empty).Trim();
        var isHandle = query.StartsWith('@');
        if (isHandle) query = query[1..];
        if (query.Length is < 1 or > 128 || query.Any(char.IsControl) || query.Contains('@') ||
            query is "." or ".." || (isHandle
                ? query.Any(char.IsWhiteSpace) || query.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                : false))
            throw Error(SlackUsernameLookupFailure.InvalidUsername);
        return query;
    }

    private static bool MatchesName(SlackUser member, string query) =>
        string.Equals(member.Name?.Trim(), query, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(member.Profile?.DisplayName?.Trim(), query, StringComparison.OrdinalIgnoreCase);

    private static bool IsMemberId(string value) => value.Length is >= 9 and <= 32 && value[0] is 'U' or 'W' && value.All(char.IsAsciiLetterOrDigit);
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw Error(SlackUsernameLookupFailure.InvalidResponse) : value;
    private static string? RequiredScopeFor(string method) => method switch { "users.list" or "users.info" => "users:read", _ => null };
    private static SlackUsernameLookupException Error(SlackUsernameLookupFailure failure) => new(failure);
    public void Dispose() => _http.Dispose();

    private sealed record Metadata([property: JsonPropertyName("next_cursor")] string? NextCursor);
    private sealed record SlackUser([property: JsonPropertyName("id")] string? Id, [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("real_name")] string? RealName, [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("deleted")] bool Deleted, [property: JsonPropertyName("is_bot")] bool IsBot,
        [property: JsonPropertyName("is_app_user")] bool IsAppUser,
        [property: JsonPropertyName("profile")] SlackProfile? Profile);
    private sealed record SlackProfile([property: JsonPropertyName("real_name")] string? RealName,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    // Slack wraps method-specific fields at the top level. Keep the generic payload adapter explicit.
    private sealed record AuthResponse([property: JsonPropertyName("team_id")] string? TeamId, [property: JsonPropertyName("team")] string? Team, [property: JsonPropertyName("url")] string? Url);
    private sealed record UserResponse([property: JsonPropertyName("user")] SlackUser? User);
    private sealed record UsersResponse([property: JsonPropertyName("members")] List<SlackUser?>? Members, [property: JsonPropertyName("response_metadata")] Metadata? ResponseMetadata);
}
