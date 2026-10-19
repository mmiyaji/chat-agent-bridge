using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Transports;

public sealed record SlackThreadIdentity(string RootTimestamp, string ChannelId, string? ChannelName, string TeamId, string TeamName);

public enum SlackThreadLookupFailure
{
    InvalidLink, MissingCredentials, InvalidToken, WrongWorkspace, MissingScope, RateLimited,
    NotFound, WrongChannel, DeletedMessage, Network, Timeout, InvalidResponse
}

public sealed class SlackThreadLookupException(SlackThreadLookupFailure failure, string? requiredScope = null) : Exception
{
    public SlackThreadLookupFailure Failure { get; } = failure;
    public string? RequiredScope { get; } = requiredScope;
}

/// <summary>Resolves an existing Slack message permalink to its thread root using the authenticated workspace.</summary>
public sealed class SlackThreadResolver : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _lookupTimeout;

    public SlackThreadResolver() : this(SlackUsernameResolver.CreateHandler(), TimeSpan.FromSeconds(20)) { }
    internal SlackThreadResolver(HttpMessageHandler handler, TimeSpan? lookupTimeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _lookupTimeout = lookupTimeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<SlackThreadIdentity> ResolveAsync(string token, string permalink, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw Fail(SlackThreadLookupFailure.MissingCredentials);
        var link = ParseLink(permalink);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_lookupTimeout);
        var auth = await CallAsync<AuthResponse>("auth.test", token, null, timeout.Token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(auth.TeamId) || string.IsNullOrWhiteSpace(auth.Team) ||
            !Uri.TryCreate(auth.Url, UriKind.Absolute, out var authUrl))
            throw Fail(SlackThreadLookupFailure.InvalidResponse);
        var teamId = auth.TeamId!;
        var teamName = auth.Team!;
        if (!string.Equals(authUrl.Host, link.Host, StringComparison.OrdinalIgnoreCase))
            throw Fail(SlackThreadLookupFailure.WrongWorkspace);

        var replies = await CallAsync<RepliesResponse>("conversations.replies", token,
            new Dictionary<string, string> { ["channel"] = link.ChannelId, ["ts"] = link.MessageTimestamp, ["limit"] = "1" },
            timeout.Token, cancellationToken).ConfigureAwait(false);
        if (replies.Messages is not { Count: > 0 } messages)
            throw Fail(SlackThreadLookupFailure.NotFound);
        var message = messages[0] ?? throw Fail(SlackThreadLookupFailure.InvalidResponse);
        if (!IsTimestamp(message.Ts) || !string.Equals(message.Ts, link.MessageTimestamp, StringComparison.Ordinal))
            throw Fail(SlackThreadLookupFailure.InvalidResponse);
        if (!string.IsNullOrEmpty(message.Subtype) && message.Subtype == "tombstone")
            throw Fail(SlackThreadLookupFailure.DeletedMessage);
        var messageTs = message.Ts!;
        var rootTs = message.ThreadTs ?? messageTs;
        if (!IsTimestamp(rootTs) || (link.ThreadTimestamp is not null && !string.Equals(link.ThreadTimestamp, rootTs, StringComparison.Ordinal)))
            throw Fail(SlackThreadLookupFailure.InvalidResponse);
        if (rootTs != message.Ts)
        {
            var rootResponse = await CallAsync<RepliesResponse>("conversations.replies", token,
                new Dictionary<string, string> { ["channel"] = link.ChannelId, ["ts"] = rootTs, ["limit"] = "1" },
                timeout.Token, cancellationToken).ConfigureAwait(false);
            var rootMessage = rootResponse.Messages is { Count: > 0 } rootMessages ? rootMessages[0] : null;
            if (rootMessage is null || rootMessage.Ts != rootTs ||
                (!string.IsNullOrEmpty(rootMessage.ThreadTs) && rootMessage.ThreadTs != rootTs))
                throw Fail(SlackThreadLookupFailure.InvalidResponse);
            if (rootMessage.Subtype == "tombstone") throw Fail(SlackThreadLookupFailure.DeletedMessage);
        }

        // A one-item page is sufficient to establish the canonical root timestamp.
        // Channel names are optional because the read scopes differ by channel type.
        string? channelName = null;
        try
        {
            var channel = await CallAsync<ConversationResponse>("conversations.info", token,
                new Dictionary<string, string> { ["channel"] = link.ChannelId }, timeout.Token, cancellationToken).ConfigureAwait(false);
            channelName = string.IsNullOrWhiteSpace(channel.Channel?.Name) ? null : channel.Channel.Name;
        }
        catch (SlackThreadLookupException ex) when (ex.Failure is SlackThreadLookupFailure.MissingScope or
            SlackThreadLookupFailure.RateLimited or SlackThreadLookupFailure.Network or SlackThreadLookupFailure.Timeout or
            SlackThreadLookupFailure.NotFound)
        {
            // Channel metadata is optional after the message root has been verified.
        }
        return new SlackThreadIdentity(rootTs, link.ChannelId, channelName, teamId, teamName);
    }

    private async Task<T> CallAsync<T>(string method, string token, IReadOnlyDictionary<string, string>? args,
        CancellationToken timeoutToken, CancellationToken callerToken)
    {
        var uri = new UriBuilder("https://slack.com/api/" + method);
        if (args is not null) uri.Query = string.Join("&", args.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        using var request = new HttpRequestMessage(method == "auth.test" ? HttpMethod.Post : HttpMethod.Get, uri.Uri);
        try { request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim()); }
        catch (FormatException) { throw Fail(SlackThreadLookupFailure.InvalidToken); }
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw Fail(SlackThreadLookupFailure.Timeout); }
        catch (HttpRequestException) { throw Fail(SlackThreadLookupFailure.Network); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw Fail(SlackThreadLookupFailure.Network);
            if (response.StatusCode == (HttpStatusCode)429) throw Fail(SlackThreadLookupFailure.RateLimited);
            if (!response.IsSuccessStatusCode)
                throw Fail(response.StatusCode == HttpStatusCode.Unauthorized ? SlackThreadLookupFailure.InvalidToken : SlackThreadLookupFailure.Network);
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
                    if (body.Length + read > 1024 * 1024) throw Fail(SlackThreadLookupFailure.InvalidResponse);
                    await body.WriteAsync(buffer.AsMemory(0, read), timeoutToken).ConfigureAwait(false);
                }
                body.Position = 0;
                document = await JsonDocument.ParseAsync(body, cancellationToken: timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw Fail(SlackThreadLookupFailure.Timeout); }
            catch (JsonException) { throw Fail(SlackThreadLookupFailure.InvalidResponse); }
            catch (IOException) { throw Fail(SlackThreadLookupFailure.Network); }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Fail(SlackThreadLookupFailure.InvalidResponse);
                if (!ok.GetBoolean())
                {
                    var error = root.TryGetProperty("error", out var value) ? value.GetString() : null;
                    if (error == "missing_scope") throw new SlackThreadLookupException(SlackThreadLookupFailure.MissingScope, RequiredScope(method, root));
                    if (error is "invalid_auth" or "not_authed" or "token_revoked" or "token_expired") throw Fail(SlackThreadLookupFailure.InvalidToken);
                    if (error is "message_not_found" or "channel_not_found" or "thread_not_found") throw Fail(SlackThreadLookupFailure.NotFound);
                    if (error is "is_archived" or "message_deleted") throw Fail(SlackThreadLookupFailure.DeletedMessage);
                    throw Fail(SlackThreadLookupFailure.Network);
                }
                try { return root.Deserialize<T>(TransportHttp.JsonOptions) ?? throw Fail(SlackThreadLookupFailure.InvalidResponse); }
                catch (JsonException) { throw Fail(SlackThreadLookupFailure.InvalidResponse); }
            }
        }
    }

    private static string RequiredScope(string method, JsonElement response)
    {
        var allowed = method switch
        {
            "conversations.info" => new[] { "channels:read", "groups:read", "im:read", "mpim:read" },
            "conversations.replies" => new[] { "channels:history", "groups:history", "im:history", "mpim:history" },
            _ => Array.Empty<string>()
        };
        if (response.TryGetProperty("needed", out var needed) && needed.ValueKind == JsonValueKind.String)
        {
            var scopes = needed.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (scopes.Length > 0 && scopes.All(scope => allowed.Contains(scope, StringComparer.Ordinal)))
                return string.Join(", ", scopes.Distinct(StringComparer.Ordinal));
        }
        return string.Join(", ", allowed);
    }

    private static ParsedLink ParseLink(string? value)
    {
        var input = (value ?? string.Empty).Trim();
        if (input.Length > 2048 || !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Split('.').Length != 3)
            throw Fail(SlackThreadLookupFailure.InvalidLink);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || segments[0] != "archives" || !IsChannelId(segments[1]) ||
            !segments[2].StartsWith('p') || !TryParsePermalinkTimestamp(segments[2][1..], out var messageTs))
            throw Fail(SlackThreadLookupFailure.InvalidLink);
        string? threadTs = null, cid = null;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var values = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(values[0]);
                var val = values.Length == 2 ? Uri.UnescapeDataString(values[1]) : string.Empty;
                if (key == "thread_ts" && threadTs is null && IsTimestamp(val)) threadTs = val;
                else if (key == "cid" && cid is null && IsChannelId(val)) cid = val;
                else throw Fail(SlackThreadLookupFailure.InvalidLink);
            }
        }
        if (cid is not null && cid != segments[1]) throw Fail(SlackThreadLookupFailure.InvalidLink);
        if (threadTs is not null && threadTs == messageTs) { /* canonical root permalink */ }
        return new ParsedLink(uri.Host, segments[1], messageTs, threadTs);
    }

    private static bool TryParsePermalinkTimestamp(string value, out string timestamp)
    {
        timestamp = string.Empty;
        if (value.Length is < 16 or > 17 || !value.All(char.IsAsciiDigit)) return false;
        var seconds = value[..^6];
        var micros = value[^6..];
        timestamp = seconds + "." + micros;
        return IsTimestamp(timestamp);
    }

    private static bool IsTimestamp(string? value)
    {
        if (value is null) return false;
        var parts = value.Split('.');
        return parts.Length == 2 && parts[0].Length is >= 9 and <= 11 && parts[1].Length == 6 &&
            parts[0].All(char.IsAsciiDigit) && parts[1].All(char.IsAsciiDigit) &&
            long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsChannelId(string? value) => value is { Length: >= 9 and <= 32 } && value[0] is 'C' or 'G' or 'D' && value.All(char.IsAsciiLetterOrDigit);
    private static SlackThreadLookupException Fail(SlackThreadLookupFailure failure) => new(failure);
    public void Dispose() => _http.Dispose();

    private sealed record ParsedLink(string Host, string ChannelId, string MessageTimestamp, string? ThreadTimestamp);
    private sealed record AuthResponse([property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("team")] string? Team, [property: JsonPropertyName("url")] string? Url);
    private sealed record RepliesResponse([property: JsonPropertyName("messages")] List<SlackMessage?>? Messages);
    private sealed record SlackMessage([property: JsonPropertyName("ts")] string? Ts,
        [property: JsonPropertyName("thread_ts")] string? ThreadTs, [property: JsonPropertyName("subtype")] string? Subtype);
    private sealed record ConversationResponse([property: JsonPropertyName("channel")] SlackChannel? Channel);
    private sealed record SlackChannel([property: JsonPropertyName("name")] string? Name);
}
