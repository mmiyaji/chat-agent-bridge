using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: SlackHistoryNamesProbe <normal-data-directory> <user-id> <channel-id> <isolated-cache-directory>");
    return 2;
}

var settingsDirectory = Path.GetFullPath(args[0]);
var userId = args[1];
var channelId = args[2];
var cacheDirectory = Path.GetFullPath(args[3]);
var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts"));
if (!IsWithin(artifactRoot, cacheDirectory)) throw new InvalidOperationException("Cache directory must be inside desktop/artifacts.");
if (userId.Length is < 9 or > 32 || userId[0] is not ('U' or 'W') || !userId.All(char.IsAsciiLetterOrDigit) ||
    channelId.Length is < 9 or > 32 || channelId[0] is not ('C' or 'G' or 'D') || !channelId.All(char.IsAsciiLetterOrDigit))
    throw new ArgumentException("Expected Slack user and channel IDs.");

var settings = await new SettingsStore(settingsDirectory).LoadAsync();
if (string.IsNullOrWhiteSpace(settings.SlackBotToken)) throw new InvalidOperationException("The configured Slack bot token is unavailable.");
var key = new SessionKey("slack", channelId, "history-names-probe", userId, "slack");
var coldHandler = new AllowlistedSlackHandler();
using (var directory = new ChatIdentityDirectory(coldHandler, cacheDirectory))
    await directory.ResolveAsync(settings, [key]);

var firstCalls = coldHandler.Snapshot();
ValidateCalls(firstCalls, ["POST auth.test", "GET users.info", "GET conversations.info"]);
var firstCache = await ReadCache(cacheDirectory);
var times = GetTimes(firstCache, userId, channelId);
ValidateIdentityNames(firstCache, userId, channelId);

var restartHandler = new AllowlistedSlackHandler();
using (var directory = new ChatIdentityDirectory(restartHandler, cacheDirectory))
    await directory.ResolveAsync(settings, [key]);
var restartCalls = restartHandler.Snapshot();
ValidateCalls(restartCalls, []);
var restartCache = await ReadCache(cacheDirectory);
var restartTimes = GetTimes(restartCache, userId, channelId);
if (times.user != restartTimes.user || times.channel != restartTimes.channel)
    throw new InvalidOperationException("Fresh cache timestamps changed during restart reuse.");

var cachePath = Path.Combine(cacheDirectory, "chat-identities.json");
var json = JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!;
var userEntry = json["entries"]!.AsArray().Single(entry =>
    entry?["key"]?["provider"]?.GetValue<string>() == "slack" &&
    entry["key"]?["origin"]?.GetValue<string>() == "slack" &&
    entry["key"]?["kind"]?.GetValue<string>() == "user" &&
    entry["key"]?["id"]?.GetValue<string>() == userId);
userEntry!["checkedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
var expiredAt = userEntry["checkedAtUtc"]!.GetValue<string>();
await File.WriteAllTextAsync(cachePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

var expiryHandler = new AllowlistedSlackHandler();
using (var directory = new ChatIdentityDirectory(expiryHandler, cacheDirectory))
    await directory.ResolveAsync(settings, [key]);
var expiryCalls = expiryHandler.Snapshot();
ValidateCalls(expiryCalls, ["POST auth.test", "GET users.info"]);
var refreshed = await ReadCache(cacheDirectory);
var refreshedTimes = GetTimes(refreshed, userId, channelId);
if (refreshedTimes.user == expiredAt || refreshedTimes.channel != times.channel)
    throw new InvalidOperationException("User expiry did not refresh only the user cache entry.");

var labels = firstCache.Entries.Where(entry => entry.Key.Provider == "slack" && entry.Key.Origin == "slack")
    .Select(entry => new { entry.Key.Kind, entry.SiteName, entry.ChannelName, entry.UserName, entry.IsDirectMessage, entry.Success, entry.CheckedAtUtc }).ToArray();
var result = new
{
    passed = true,
    calls = new { cold = firstCalls, restart = restartCalls, expiredUser = expiryCalls },
    cacheTimes = new
    {
        initial = new { times.user, times.channel },
        restart = new { restartTimes.user, restartTimes.channel },
        afterUserExpiry = new { refreshedTimes.user, refreshedTimes.channel }
    },
    labels,
    generatedAtUtc = DateTimeOffset.UtcNow
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static bool IsWithin(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static async Task<CacheDocument> ReadCache(string directory)
{
    await using var stream = File.OpenRead(Path.Combine(directory, "chat-identities.json"));
    return await JsonSerializer.DeserializeAsync<CacheDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Identity cache is missing or invalid.");
}

static (string user, string channel) GetTimes(CacheDocument cache, string userId, string channelId)
{
    var user = cache.Entries.Single(entry => entry.Key.Provider == "slack" && entry.Key.Origin == "slack" && entry.Key.Kind == "user" && entry.Key.Id == userId);
    var channel = cache.Entries.Single(entry => entry.Key.Provider == "slack" && entry.Key.Origin == "slack" && entry.Key.Kind == "channel" && entry.Key.Id == channelId);
    return (user.CheckedAtUtc, channel.CheckedAtUtc);
}

static void ValidateIdentityNames(CacheDocument cache, string userId, string channelId)
{
    var user = cache.Entries.Single(entry => entry.Key.Provider == "slack" && entry.Key.Origin == "slack" && entry.Key.Kind == "user" && entry.Key.Id == userId);
    var channel = cache.Entries.Single(entry => entry.Key.Provider == "slack" && entry.Key.Origin == "slack" && entry.Key.Kind == "channel" && entry.Key.Id == channelId);
    if (!user.Success || string.IsNullOrWhiteSpace(user.UserName) || !channel.Success || string.IsNullOrWhiteSpace(channel.ChannelName) ||
        string.IsNullOrWhiteSpace(channel.SiteName)) throw new InvalidOperationException("Slack returned incomplete identity labels.");
}

static void ValidateCalls(IReadOnlyList<CallRecord> actual, IReadOnlyList<string> expected)
{
    var summary = actual.Select(call => $"{call.Method} {call.Endpoint}").ToArray();
    if (!summary.SequenceEqual(expected)) throw new InvalidOperationException("Slack identity calls did not match the allowed lookup sequence.");
    if (actual.Any(call => call.Status != 200)) throw new InvalidOperationException("A Slack identity lookup did not return HTTP 200.");
}

sealed class AllowlistedSlackHandler : HttpMessageHandler
{
    private static readonly HashSet<string> Allowed = ["auth.test", "users.info", "conversations.info"];
    private readonly List<CallRecord> _calls = [];
    private readonly HttpMessageInvoker _forwarder = new(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);
    public IReadOnlyList<CallRecord> Snapshot() => _calls.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Slack request had no URI.");
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "slack.com" || uri.AbsolutePath is not ("/api/auth.test" or "/api/users.info" or "/api/conversations.info"))
            throw new HttpRequestException("The live probe blocked a non-identity Slack endpoint.");
        var endpoint = uri.AbsolutePath[5..];
        if (!Allowed.Contains(endpoint) || (endpoint == "auth.test" ? request.Method != HttpMethod.Post : request.Method != HttpMethod.Get))
            throw new HttpRequestException("The live probe blocked an unapproved Slack method.");
        var response = await _forwarder.SendAsync(request, cancellationToken);
        _calls.Add(new CallRecord(request.Method.Method, endpoint, (int)response.StatusCode));
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _forwarder.Dispose();
        base.Dispose(disposing);
    }
}

sealed record CallRecord(string Method, string Endpoint, int Status);
sealed record CacheDocument(int Version, List<CacheEntry> Entries);
sealed record CacheEntry(CacheKey Key, string? SiteName, string? ChannelName, string? UserName, bool IsDirectMessage, bool Success, string CheckedAtUtc);
sealed record CacheKey(string Provider, string Origin, string Kind, string Id);
