using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed record ChatIdentityLabels(string? SiteName = null, string? ChannelName = null, string? UserName = null, bool IsDirectMessage = false);

/// <summary>Resolves and caches safe display labels for chat history identities.</summary>
public sealed class ChatIdentityDirectory : IDisposable
{
    private const int MaxEntries = 2000;
    private const int MaxRequests = 40;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly HttpClient? _mattermostProxyHttp;
    private readonly HttpClient? _mattermostDirectHttp;
    private readonly SemaphoreSlim _gate;
    private readonly SemaphoreSlim _resolveGate = new(1, 1);

    public ChatIdentityDirectory(string? directory = null)
    {
        _cachePath = Path.Combine(Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath), "chat-identities.json");
        _http = new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _mattermostProxyHttp = new HttpClient(CreateHandler(bypassProxy: false), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _mattermostDirectHttp = new HttpClient(CreateHandler(bypassProxy: true), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _gate = CacheGates.GetOrAdd(_cachePath, static _ => new SemaphoreSlim(1, 1));
    }

    internal ChatIdentityDirectory(HttpMessageHandler handler, string? directory = null)
        : this(handler, directory ?? Path.Combine(Path.GetTempPath(), "ChatAgentBridge.IdentityDirectory"), ownsHandler: true) { }

    private ChatIdentityDirectory(HttpMessageHandler handler, string directory, bool ownsHandler)
    {
        _cachePath = Path.Combine(Path.GetFullPath(directory), "chat-identities.json");
        _http = new HttpClient(handler, ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _gate = CacheGates.GetOrAdd(_cachePath, static _ => new SemaphoreSlim(1, 1));
    }

    internal static SocketsHttpHandler CreateHandler(bool bypassProxy = false) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = !bypassProxy,
        Proxy = null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<IReadOnlyDictionary<SessionKey, ChatIdentityLabels>> ReadCachedAsync(
        IEnumerable<SessionKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var requested = keys.Distinct().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await ReadStoreAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<SessionKey, ChatIdentityLabels>();
        foreach (var key in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[key] = Merge(entries, key);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<SessionKey, ChatIdentityLabels>> ResolveAsync(
        BridgeSettings settings, IEnumerable<SessionKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        var requested = keys.Distinct().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OverallTimeout);
            var budget = new RequestBudget();

            try
            {
                var mmKeys = new List<(SessionKey Key, string Origin)>();
                var slackKeys = new List<SessionKey>();
                foreach (var key in requested)
                {
                    if (key.Provider.Equals("mattermost", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(settings.MattermostToken) && TryMattermostOrigin(settings.MattermostServerUrl, out var uri, out var origin) &&
                            string.Equals(origin, key.Origin, StringComparison.Ordinal)) mmKeys.Add((key, uri!.AbsoluteUri));
                    }
                    else if (key.Provider.Equals("slack", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.SlackBotToken))
                        slackKeys.Add(key);
                }

                foreach (var group in mmKeys.GroupBy(item => item.Origin, StringComparer.Ordinal))
                {
                    if (timeout.IsCancellationRequested || budget.Limited) break;
                    var origin = group.First().Key.Origin!;
                    if (IsCoolingDown(store, "mattermost", origin)) continue;
                    var baseUri = new Uri(group.Key);
                    var staleSite = GetFreshOrStale(store, "mattermost", origin, "site", "site");
                    var siteName = staleSite?.SiteName ?? HostLabel(baseUri);
                    await ResolveMattermostAsync(settings, group.Select(item => item.Key).ToArray(), baseUri, origin, siteName, store, budget, timeout.Token).ConfigureAwait(false);
                }

                if (slackKeys.Count > 0 && !timeout.IsCancellationRequested && !budget.Limited)
                {
                    if (!IsCoolingDown(store, "slack", "slack"))
                        await ResolveSlackAsync(settings, slackKeys.ToArray(), store, budget, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }

            cancellationToken.ThrowIfCancellationRequested();
            return requested.ToDictionary(key => key, key => Merge(store, key));
        }
        finally { _resolveGate.Release(); }
    }

    private async Task ResolveMattermostAsync(BridgeSettings settings, SessionKey[] keys, Uri baseUri, string origin,
        string siteName, Dictionary<CacheKey, CacheEntry> store, RequestBudget budget, CancellationToken cancellationToken)
    {
        var token = settings.MattermostToken.Trim();
        foreach (var userId in keys.Select(key => key.UserId).Where(IsMattermostId).Distinct(StringComparer.Ordinal))
        {
            var cacheKey = new CacheKey("mattermost", origin, "user", userId!);
            if (!NeedsRefresh(store, cacheKey)) continue;
            await FetchAndStoreAsync(store, cacheKey, siteName, null, false, async () =>
            {
                using var document = await GetJsonAsync(new Uri(baseUri, $"api/v4/users/{Uri.EscapeDataString(userId!)}"), token, settings.MattermostBypassProxy, budget, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (!IdMatches(root, "id", userId!)) return null;
                var username = SafeLabel(root, "username");
                if (username is null) return null;
                var name = SafeLabel(root, "nickname") ?? JoinName(root) ?? username;
                return "@" + username + (name.Equals(username, StringComparison.OrdinalIgnoreCase) ? "" : $" ({name})");
            }, cancellationToken).ConfigureAwait(false);
            if (budget.Limited) return;
        }

        foreach (var channelId in keys.Select(key => key.ChannelId).Where(IsMattermostId).Distinct(StringComparer.Ordinal))
        {
            var cacheKey = new CacheKey("mattermost", origin, "channel", channelId);
            if (!NeedsRefresh(store, cacheKey)) continue;
            var channel = await FetchChannelAsync(store, cacheKey, baseUri, token, settings.MattermostBypassProxy, siteName, budget, cancellationToken).ConfigureAwait(false);
            if (budget.Limited) return;
        }
    }

    private async Task<ChannelData?> FetchChannelAsync(Dictionary<CacheKey, CacheEntry> store, CacheKey key, Uri baseUri,
        string token, bool bypassProxy, string siteName, RequestBudget budget, CancellationToken cancellationToken)
    {
        ChannelData? data = null;
        await FetchAndStoreAsync(store, key, siteName, null, false, async () =>
        {
            using var doc = await GetJsonAsync(new Uri(baseUri, $"api/v4/channels/{Uri.EscapeDataString(key.Id)}"), token, bypassProxy, budget, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!IdMatches(root, "id", key.Id)) return null;
            var type = SafeLabel(root, "type");
            var direct = type is "D" or "G";
            var name = direct ? null : SafeLabel(root, "display_name") ?? SafeLabel(root, "name");
            var teamId = SafeLabel(root, "team_id");
            data = new ChannelData(name, direct, teamId);
            return name ?? "Direct message";
        }, cancellationToken).ConfigureAwait(false);
        if (data is not null)
        {
            store[key] = Entry(key, siteName, data.IsDirectMessage ? "Direct message" : data.Name, data.IsDirectMessage, true);
            await PersistAsync(store, cancellationToken).ConfigureAwait(false);
        }
        return data;
    }

    private async Task ResolveSlackAsync(BridgeSettings settings, SessionKey[] keys, Dictionary<CacheKey, CacheEntry> store,
        RequestBudget budget, CancellationToken cancellationToken)
    {
        var token = settings.SlackBotToken.Trim();
        var userIds = keys.Select(key => key.UserId).Where(IsSlackUserId).Distinct(StringComparer.Ordinal).ToArray();
        var channelIds = keys.Select(key => key.ChannelId).Where(IsSlackChannelId).Distinct(StringComparer.Ordinal).ToArray();
        if (!userIds.Any(id => NeedsRefresh(store, new CacheKey("slack", "slack", "user", id!))) &&
            !channelIds.Any(id => NeedsRefresh(store, new CacheKey("slack", "slack", "channel", id!)))) return;
        string? siteName = null;
        try
        {
            using var auth = await SlackJsonAsync("auth.test", null, token, budget, cancellationToken).ConfigureAwait(false);
            var root = auth.RootElement;
            if (SafeLabel(root, "team_id") is null) return;
            siteName = SafeLabel(root, "team");
        }
        catch (ApiRateLimitedException) { await RecordCooldownAsync("slack", "slack", store, cancellationToken).ConfigureAwait(false); return; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { await RecordCooldownAsync("slack", "slack", store, cancellationToken).ConfigureAwait(false); return; }
        if (budget.Limited) return;

        foreach (var userId in userIds)
        {
            var cacheKey = new CacheKey("slack", "slack", "user", userId!);
            if (!NeedsRefresh(store, cacheKey)) continue;
            await FetchAndStoreAsync(store, cacheKey, siteName, null, false, async () =>
            {
                using var doc = await SlackJsonAsync("users.info", new Dictionary<string, string> { ["user"] = userId! }, token, budget, cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;
                if (!root.TryGetProperty("user", out var user) || !IdMatches(user, "id", userId!)) return null;
                var username = SafeLabel(user, "name");
                if (username is null) return null;
                var display = user.TryGetProperty("profile", out var profile) ? SafeLabel(profile, "display_name") : null;
                if (display is null || display.Equals(username, StringComparison.OrdinalIgnoreCase)) display = SafeLabel(user, "real_name") ?? display;
                return "@" + username + (display is null || display.Equals(username, StringComparison.OrdinalIgnoreCase) ? "" : $" ({display})");
            }, cancellationToken).ConfigureAwait(false);
            if (budget.Limited) return;
        }
        foreach (var channelId in channelIds)
        {
            var cacheKey = new CacheKey("slack", "slack", "channel", channelId);
            if (!NeedsRefresh(store, cacheKey)) continue;
            await FetchAndStoreAsync(store, cacheKey, siteName, null, false, async () =>
            {
                using var doc = await SlackJsonAsync("conversations.info", new Dictionary<string, string> { ["channel"] = channelId }, token, budget, cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;
                if (!root.TryGetProperty("channel", out var channel) || !IdMatches(channel, "id", channelId)) return null;
                var direct = GetBool(channel, "is_im") || GetBool(channel, "is_mpim") || channelId.StartsWith('D');
                var name = direct ? "Direct message" : SafeLabel(channel, "name");
                store[cacheKey] = Entry(cacheKey, siteName, name, direct, true);
                return name;
            }, cancellationToken).ConfigureAwait(false);
            if (store.TryGetValue(cacheKey, out var fetched) && fetched.Success)
            {
                // Re-read the response's direct/group marker because the common cache path only receives labels.
                store[cacheKey] = fetched with { IsDirectMessage = channelId.StartsWith('D') || fetched.ChannelName == "Direct message" };
                await PersistAsync(store, cancellationToken).ConfigureAwait(false);
            }
            if (budget.Limited) return;
        }
    }

    private async Task FetchAndStoreAsync(Dictionary<CacheKey, CacheEntry> store, CacheKey key, string? siteName,
        string? channelName, bool isDirectMessage, Func<Task<string?>> fetch, CancellationToken cancellationToken, ChannelData? channelData = null)
    {
        if (!budgetGuard(cancellationToken)) return;
        string? value = null;
        var success = false;
        try { value = await fetch().ConfigureAwait(false); success = value is not null; }
        catch (ApiRateLimitedException) { await RecordCooldownAsync(key.Provider, key.Origin, store, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException) { }
        store.TryGetValue(key, out var previous);
        var siteForEntity = key.Provider == "slack"
            ? success ? siteName ?? previous?.SiteName : previous?.SiteName
            : siteName ?? previous?.SiteName;
        store[key] = key.Kind switch
        {
            "site" => Entry(key, success ? value : previous?.SiteName ?? siteName, null, false, success),
            "user" => Entry(key, siteForEntity, null, false, success, success ? value : previous?.UserName),
            "channel" => Entry(key, siteForEntity, success ? channelData?.Name ?? value : previous?.ChannelName,
                success ? channelData?.IsDirectMessage == true : previous?.IsDirectMessage == true, success),
            _ => Entry(key, success ? value : previous?.SiteName ?? siteName, null, false, success)
        };
        await PersistAsync(store, cancellationToken).ConfigureAwait(false);
    }

    private static bool budgetGuard(CancellationToken token) { token.ThrowIfCancellationRequested(); return true; }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, bool bypassProxy, RequestBudget budget, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var client = bypassProxy ? _mattermostDirectHttp ?? _http : _mattermostProxyHttp ?? _http;
        return await SendJsonAsync(request, client, budget, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SlackJsonAsync(string method, IReadOnlyDictionary<string, string>? query,
        string token, RequestBudget budget, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder("https://slack.com/api/" + method);
        if (query is not null) builder.Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(method == "auth.test" ? HttpMethod.Post : HttpMethod.Get, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var doc = await SendJsonAsync(request, _http, budget, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            var rateLimited = doc.RootElement.TryGetProperty("error", out var error) && error.GetString() == "ratelimited";
            doc.Dispose();
            if (rateLimited) { budget.Limited = true; throw new ApiRateLimitedException(); }
            throw new InvalidOperationException("Identity lookup was unavailable.");
        }
        return doc;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, HttpClient client, RequestBudget budget, CancellationToken cancellationToken)
    {
        if (!budget.TryTake()) throw new InvalidOperationException("Identity lookup request limit reached.");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 429) { budget.Limited = true; throw new ApiRateLimitedException(); }
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new HttpRequestException("Redirect rejected for identity lookup.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Identity lookup failed.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > 256 * 1024) throw new JsonException("Identity response too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<CacheKey, CacheEntry>> ReadStoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadStoreLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(Dictionary<CacheKey, CacheEntry> entries, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = await ReadStoreLockedAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pair in entries)
                if (!latest.TryGetValue(pair.Key, out var current) || pair.Value.CheckedAtUtc >= current.CheckedAtUtc)
                    latest[pair.Key] = pair.Value;
            var dir = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(dir);
            var selected = latest.Values.OrderByDescending(entry => entry.CheckedAtUtc).Take(MaxEntries).ToArray();
            var temp = Path.Combine(dir, $"chat-identities.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    await JsonSerializer.SerializeAsync(stream, new CacheDocument(1, selected), JsonOptions, cancellationToken).ConfigureAwait(false);
                File.Move(temp, _cachePath, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

    private static bool IsValidEntry(CacheEntry entry) => entry.Key is { Provider: "slack" or "mattermost", Kind: "site" or "team" or "channel" or "user" or "cooldown", Origin: not null, Id: not null } &&
        entry.CheckedAtUtc != default && entry.CheckedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5) &&
        IsSafeCachedLabel(entry.SiteName) && IsSafeCachedLabel(entry.ChannelName) && IsSafeCachedLabel(entry.UserName);

    private static bool IsSafeCachedLabel(string? value) => value is null || value.Length <= 128 && !value.Any(char.IsControl);

    private static ChatIdentityLabels Merge(Dictionary<CacheKey, CacheEntry> store, SessionKey key)
    {
        var provider = key.Provider.ToLowerInvariant();
        var origin = provider == "mattermost" ? key.Origin : "slack";
        if (origin is null) return new();
        var site = provider == "mattermost" ? GetFreshOrStale(store, provider, origin, "site", "site")?.SiteName : null;
        var channel = GetFreshOrStale(store, provider, origin, "channel", key.ChannelId);
        var user = key.UserId is null ? null : GetFreshOrStale(store, provider, origin, "user", key.UserId);
        return new ChatIdentityLabels(channel?.SiteName ?? site, channel?.IsDirectMessage == true ? "Direct message" : channel?.ChannelName,
            user?.UserName, channel?.IsDirectMessage == true);
    }

    private static CacheEntry? GetFreshOrStale(Dictionary<CacheKey, CacheEntry> store, string provider, string origin, string kind, string id) =>
        store.TryGetValue(new CacheKey(provider, origin, kind, id), out var entry) ? entry : null;

    private static bool NeedsRefresh(Dictionary<CacheKey, CacheEntry> store, CacheKey key)
    {
        if (!store.TryGetValue(key, out var entry)) return true;
        var ttl = entry.Success ? SuccessTtl : FailureTtl;
        return DateTimeOffset.UtcNow - entry.CheckedAtUtc >= ttl;
    }

    private static bool IsCoolingDown(Dictionary<CacheKey, CacheEntry> store, string provider, string origin) =>
        !NeedsRefresh(store, new CacheKey(provider, origin, "cooldown", "api"));

    private async Task RecordCooldownAsync(string provider, string origin, Dictionary<CacheKey, CacheEntry> store, CancellationToken cancellationToken)
    {
        var key = new CacheKey(provider, origin, "cooldown", "api");
        store[key] = Entry(key, null, null, false, false);
        await PersistAsync(store, cancellationToken).ConfigureAwait(false);
    }

    private static CacheEntry Entry(CacheKey key, string? site, string? channel, bool direct, bool success, string? user = null) =>
        new(key, Sanitize(site), Sanitize(channel), Sanitize(user), direct, success, DateTimeOffset.UtcNow);

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Where(ch => !char.IsControl(ch)).Take(128).ToArray()).Trim();
        return safe.Length == 0 ? null : safe;
    }
    private static string? SafeLabel(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? Sanitize(value.GetString()) : null;
    private static bool GetBool(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool IdMatches(JsonElement element, string property, string expected) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    private static string? JoinName(JsonElement user) => string.Join(' ', new[] { SafeLabel(user, "first_name"), SafeLabel(user, "last_name") }.Where(value => value is not null)) is { Length: > 0 } value ? value : null;
    private static bool IsMattermostId(string? value) => value is { Length: 26 } && value.All(char.IsAsciiLetterOrDigit);
    private static bool IsSlackUserId(string? value) => value is { Length: >= 9 and <= 32 } && value[0] is 'U' or 'W' && value.All(char.IsAsciiLetterOrDigit);
    private static bool IsSlackChannelId(string? value) => value is { Length: >= 9 and <= 32 } && value[0] is 'C' or 'G' or 'D' && value.All(char.IsAsciiLetterOrDigit);
    private static string HostLabel(Uri uri) => uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";

    private static bool TryMattermostOrigin(string? serverUrl, out Uri? uri, out string? origin)
    {
        uri = null; origin = null;
        try
        {
            uri = TransportHttp.MattermostBaseUri(serverUrl ?? "");
            origin = ChatSessionScope.Create("mattermost", "x", "x", "x", serverUrl).Origin;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return false; }
    }

    private async Task<Dictionary<CacheKey, CacheEntry>> ReadStoreLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath)) return [];
        try
        {
            var info = new FileInfo(_cachePath);
            if (info.Length > 1024 * 1024) return [];
            await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var doc = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (doc?.Version != 1 || doc.Entries is null) return [];
            return doc.Entries.Where(entry => entry is not null && IsValidEntry(entry)).TakeLast(MaxEntries).ToDictionary(entry => entry.Key!, entry => entry);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return []; }
    }

    public void Dispose() { _http.Dispose(); _mattermostProxyHttp?.Dispose(); _mattermostDirectHttp?.Dispose(); _resolveGate.Dispose(); }

    private sealed class RequestBudget { private int _count; public bool Limited { get; set; } public bool TryTake() { if (Limited) return false; if (++_count <= MaxRequests) return true; Limited = true; return false; } }
    private sealed class ApiRateLimitedException : Exception;
    private sealed record ChannelData(string? Name, bool IsDirectMessage, string? TeamId);
    private sealed record CacheDocument(int Version, CacheEntry[]? Entries);
    // The cache key and labels are the complete allowlist; never serialize request/response payloads.
    private sealed record CacheKey(string Provider, string Origin, string Kind, string Id);
    private sealed record CacheEntry(CacheKey? Key, string? SiteName, string? ChannelName, string? UserName, bool IsDirectMessage, bool Success, DateTimeOffset CheckedAtUtc);
}
