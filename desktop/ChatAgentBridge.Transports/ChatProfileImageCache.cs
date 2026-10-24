using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

/// <summary>Fetches and caches bounded profile images for known chat participants.</summary>
public sealed class ChatProfileImageCache : IDisposable
{
    private const int MaxImageBytes = 512 * 1024;
    private const int MaxImages = 100;
    private const long MaxCacheBytes = 50L * 1024 * 1024;
    private const int MaxApiResponseBytes = 128 * 1024;
    private const int MaxRequestsPerResolution = 2;
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim RequestSlots = new(64, 64);
    private static readonly SemaphoreSlim[] KeyGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] DirectoryGates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly string _cacheDirectory;
    private readonly object _httpSync = new();
    private HttpMessageHandler? _injectedHandler;
    private readonly bool _ownsInjectedHandler;
    private readonly bool _useInjectedHandler;
    private HttpClient? _defaultHttp;
    private HttpClient? _mattermostDirectHttp;
    private int _createdHttpClientCount;
    private bool _disposed;

    public ChatProfileImageCache(string? directory = null)
    {
        _cacheDirectory = Path.Combine(Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath), "avatar-cache");
    }

    internal ChatProfileImageCache(HttpMessageHandler handler, string? dataDirectory = null)
        : this(handler, dataDirectory ?? Path.Combine(Path.GetTempPath(), "ChatAgentBridge.ProfileImageCache"), ownsHandler: true) { }

    private ChatProfileImageCache(HttpMessageHandler handler, string dataDirectory, bool ownsHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _cacheDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "avatar-cache");
        _injectedHandler = handler;
        _ownsInjectedHandler = ownsHandler;
        _useInjectedHandler = true;
    }

    internal int CreatedHttpClientCount => Volatile.Read(ref _createdHttpClientCount);

    public async Task<string?> ResolveAsync(BridgeSettings settings, SessionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildRequestContext(settings, key, out var context)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolutionTimeout);
        var token = timeout.Token;
        await EnsureCacheDirectoryAsync(token).ConfigureAwait(false);
        var cacheKey = MakeCacheKey(context);
        var gate = KeyGates[(int)(Convert.ToUInt32(cacheKey[..8], 16) % (uint)KeyGates.Length)];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = await TryGetCachedAsync(cacheKey, token).ConfigureAwait(false);
            if (cached is not null || IsFreshFailure(cacheKey)) return cached;

            using var requestBudget = new RequestBudget(MaxRequestsPerResolution);
            try
            {
                var bytes = context.Provider switch
                {
                    "mattermost" => await FetchMattermostAsync(context, requestBudget, token).ConfigureAwait(false),
                    "slack" => await FetchSlackAsync(context, requestBudget, token).ConfigureAwait(false),
                    "discord" => await FetchDiscordAsync(context, requestBudget, token).ConfigureAwait(false),
                    _ => null
                };
                if (bytes is null || !TryValidateImage(bytes, out var extension))
                {
                    await WriteFailureAsync(cacheKey, token).ConfigureAwait(false);
                    await PruneCacheAsync(token).ConfigureAwait(false);
                    return null;
                }
                var path = await WriteImageAsync(cacheKey, extension!, bytes, token).ConfigureAwait(false);
                await PruneCacheAsync(token).ConfigureAwait(false);
                return path;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TryWriteFailureAfterTimeoutAsync(cacheKey).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                await WriteFailureBestEffortAsync(cacheKey).ConfigureAwait(false);
                return null;
            }
        }
        finally { gate.Release(); }
    }

    private static bool TryBuildRequestContext(BridgeSettings settings, SessionKey key, out RequestContext context)
    {
        context = default!;
        if (string.IsNullOrWhiteSpace(key.UserId)) return false;
        var provider = key.Provider?.Trim().ToLowerInvariant();
        var userId = key.UserId.Trim();
        string origin;
        string token;
        Uri? mattermostBase = null;
        switch (provider)
        {
            case "mattermost":
                try
                {
                    mattermostBase = TransportHttp.MattermostBaseUri(settings.MattermostServerUrl);
                    var expected = ChatSessionScope.Create("mattermost", key.ChannelId, key.ThreadId, userId, settings.MattermostServerUrl);
                    if (!string.Equals(expected.Origin, key.Origin, StringComparison.Ordinal)) return false;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) { return false; }
                if (!IsMattermostUserId(userId)) return false;
                origin = key.Origin!;
                token = settings.MattermostToken.Trim();
                break;
            case "slack":
                if (!string.Equals(key.Origin, "slack", StringComparison.Ordinal) || !IsSlackUserId(userId)) return false;
                origin = "slack";
                token = settings.SlackBotToken.Trim();
                break;
            case "discord":
                if (!string.Equals(key.Origin, "discord", StringComparison.Ordinal) || !IsDiscordId(userId)) return false;
                origin = "discord";
                token = settings.DiscordBotToken.Trim();
                break;
            default: return false;
        }
        if (token.Length == 0 || token.Any(char.IsControl)) return false;
        context = new RequestContext(provider!, origin, userId, token, mattermostBase, settings.MattermostBypassProxy);
        return true;
    }

    private static string MakeCacheKey(RequestContext context)
    {
        var credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Token))).ToLowerInvariant();
        var input = string.Concat(context.Provider, "\0", context.Origin, "\0", context.UserId, "\0", credentialHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private async Task<byte[]?> FetchMattermostAsync(RequestContext context, RequestBudget budget, CancellationToken token)
    {
        if (context.MattermostBase is null || !budget.TryTake()) return null;
        var requestUri = new Uri(context.MattermostBase, $"api/v4/users/{Uri.EscapeDataString(context.UserId)}/image");
        if (!SameOrigin(requestUri, context.MattermostBase)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        return await FetchImageResponseAsync(request, GetHttpClient(context.MattermostBypassProxy), token).ConfigureAwait(false);
    }

    private async Task<byte[]?> FetchSlackAsync(RequestContext context, RequestBudget budget, CancellationToken token)
    {
        if (!budget.TryTake()) return null;
        var apiUri = new Uri("https://slack.com/api/users.info?user=" + Uri.EscapeDataString(context.UserId));
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        using var response = await SendAsync(GetHttpClient(), request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || IsRedirect(response.StatusCode)) return null;
        var json = await ReadBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("user", out var user) || !IdMatches(user, "id", context.UserId) ||
            !user.TryGetProperty("profile", out var profile)) return null;

        foreach (var property in new[] { "image_72", "image_48", "image_192" })
        {
            if (!profile.TryGetProperty(property, out var imageValue) || imageValue.ValueKind != JsonValueKind.String ||
                !TrySlackImageUri(imageValue.GetString(), out var imageUri)) continue;
            if (!budget.TryTake()) break;
            using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUri);
            var bytes = await FetchImageResponseAsync(imageRequest, GetHttpClient(), token).ConfigureAwait(false);
            if (bytes is not null && TryValidateImage(bytes, out _)) return bytes;
        }
        return null;
    }

    private async Task<byte[]?> FetchDiscordAsync(RequestContext context, RequestBudget budget, CancellationToken token)
    {
        if (!budget.TryTake()) return null;
        var apiUri = new Uri($"https://discord.com/api/v10/users/{Uri.EscapeDataString(context.UserId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", context.Token);
        using var response = await SendAsync(GetHttpClient(), request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || IsRedirect(response.StatusCode)) return null;
        var json = await ReadBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var user = doc.RootElement;
        if (!IdMatches(user, "id", context.UserId) || !user.TryGetProperty("avatar", out var avatar) || avatar.ValueKind != JsonValueKind.String)
            return null;
        var hash = avatar.GetString();
        if (!IsDiscordAvatarHash(hash)) return null;
        var imageUri = new Uri($"https://cdn.discordapp.com/avatars/{context.UserId}/{hash}.png?size=64");
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUri);
        return await FetchImageResponseAsync(imageRequest, GetHttpClient(), token).ConfigureAwait(false);
    }

    private async Task<byte[]?> FetchImageResponseAsync(HttpRequestMessage request, HttpClient client, CancellationToken token)
    {
        using var response = await SendAsync(client, request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || IsRedirect(response.StatusCode)) return null;
        return await ReadBoundedAsync(response.Content, MaxImageBytes, token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        await RequestSlots.WaitAsync(token).ConfigureAwait(false);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        finally { RequestSlots.Release(); }
    }

    private HttpClient GetHttpClient(bool mattermostBypassProxy = false)
    {
        lock (_httpSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_useInjectedHandler)
            {
                _defaultHttp ??= CreateClient(_injectedHandler!, ownsHandler: _ownsInjectedHandler);
                _injectedHandler = null;
                return _defaultHttp;
            }
            if (mattermostBypassProxy)
                return _mattermostDirectHttp ??= CreateClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = ResolutionTimeout }, true);
            return _defaultHttp ??= CreateClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true, ConnectTimeout = ResolutionTimeout }, true);
        }
    }

    private HttpClient CreateClient(HttpMessageHandler handler, bool ownsHandler)
    {
        Interlocked.Increment(ref _createdHttpClientCount);
        return new HttpClient(handler, ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task<string?> TryGetCachedAsync(string cacheKey, CancellationToken token)
    {
        EnsureSafeDirectory();
        foreach (var extension in new[] { ".png", ".jpg" })
        {
            var path = Path.Combine(_cacheDirectory, cacheKey + extension);
            if (!IsRegularCacheFile(path)) continue;
            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc >= SuccessTtl) continue;
            if (info.Length is <= 0 or > MaxImageBytes) { TryDelete(path); continue; }
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (TryValidateImage(bytes, out var validated) && extension == validated) return path;
            TryDelete(path);
        }
        return null;
    }

    private bool IsFreshFailure(string cacheKey)
    {
        EnsureSafeDirectory();
        var path = Path.Combine(_cacheDirectory, cacheKey + ".fail");
        return IsRegularCacheFile(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < FailureTtl;
    }

    private async Task<string?> WriteImageAsync(string cacheKey, string extension, byte[] bytes, CancellationToken token)
    {
        EnsureSafeDirectory();
        var target = Path.Combine(_cacheDirectory, cacheKey + extension);
        var temp = Path.Combine(_cacheDirectory, cacheKey + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            EnsureSafeDirectory();
            if (File.Exists(target) && !IsRegularCacheFile(target)) return null;
            EnsureSafeDirectory();
            File.Move(temp, target, overwrite: true);
            TryDelete(Path.Combine(_cacheDirectory, cacheKey + (extension == ".png" ? ".jpg" : ".png")));
            TryDelete(Path.Combine(_cacheDirectory, cacheKey + ".fail"));
            return target;
        }
        finally { TryDelete(temp); }
    }

    private async Task WriteFailureAsync(string cacheKey, CancellationToken token)
    {
        EnsureSafeDirectory();
        var path = Path.Combine(_cacheDirectory, cacheKey + ".fail");
        var temp = Path.Combine(_cacheDirectory, cacheKey + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
                await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnsureSafeDirectory();
            if (File.Exists(path) && !IsRegularCacheFile(path)) return;
            EnsureSafeDirectory();
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    private async Task WriteFailureBestEffortAsync(string cacheKey)
    {
        try
        {
            await WriteFailureAsync(cacheKey, CancellationToken.None).ConfigureAwait(false);
            await PruneCacheAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private async Task TryWriteFailureAfterTimeoutAsync(string cacheKey) => await WriteFailureBestEffortAsync(cacheKey).ConfigureAwait(false);

    private async Task PruneCacheAsync(CancellationToken token)
    {
        var directoryHash = SHA256.HashData(Encoding.UTF8.GetBytes(_cacheDirectory.ToUpperInvariant()));
        var gate = DirectoryGates[(int)(BitConverter.ToUInt32(directoryHash, 0) % (uint)DirectoryGates.Length)];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectory();
            var files = Directory.EnumerateFiles(_cacheDirectory)
                .Where(path => IsRecognizedCacheFile(Path.GetFileName(path)) && IsRegularCacheFile(path))
                .Select(path => new FileInfo(path)).OrderByDescending(info => info.LastWriteTimeUtc).ToList();
            var images = files.Where(info => info.Extension is ".png" or ".jpg").ToList();
            long total = images.Sum(info => info.Length);
            foreach (var file in files.Where(info => info.Extension == ".fail").Skip(100)) TryDelete(file.FullName);
            for (var i = 0; i < images.Count; i++)
            {
                if (i < MaxImages && total <= MaxCacheBytes) continue;
                total -= images[i].Length;
                TryDelete(images[i].FullName);
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureCacheDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNoReparsePointAncestors();
        Directory.CreateDirectory(_cacheDirectory);
        EnsureSafeDirectory();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void EnsureNoReparsePointAncestors()
    {
        var path = Path.GetFullPath(_cacheDirectory);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Avatar cache path has no root.");
        var current = root;
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (!info.Exists) break;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Avatar cache path includes a redirected directory.");
        }
    }

    private void EnsureSafeDirectory()
    {
        var path = Path.GetFullPath(_cacheDirectory);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Avatar cache path has no root.");
        var current = root;
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Avatar cache path includes a missing or redirected directory.");
        }
    }

    private bool IsRegularCacheFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(_cacheDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool IsRecognizedCacheFile(string filename) => filename.Length is 68 or 69 &&
        filename[..64].All(Uri.IsHexDigit) && filename[64..] is ".png" or ".jpg" or ".fail";

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes) throw new InvalidOperationException("Response exceeded the configured size limit.");
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length > maxBytes - read) throw new InvalidOperationException("Response exceeded the configured size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static bool TryValidateImage(byte[] bytes, out string? extension)
    {
        extension = null;
        if (bytes.Length is < 24 or > MaxImageBytes) return false;
        ReadOnlySpan<byte> span = bytes;
        if (span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            span[12..16].SequenceEqual("IHDR"u8))
        {
            var width = ReadBigEndianInt32(span[16..20]);
            var height = ReadBigEndianInt32(span[20..24]);
            if (width is > 0 and <= 2048 && height is > 0 and <= 2048) { extension = ".png"; return true; }
            return false;
        }
        if (span[0] != 0xff || span[1] != 0xd8) return false;
        var offset = 2;
        while (offset + 4 <= span.Length)
        {
            if (span[offset] != 0xff) { offset++; continue; }
            while (offset < span.Length && span[offset] == 0xff) offset++;
            if (offset >= span.Length) break;
            var marker = span[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > span.Length) break;
            var segmentLength = (span[offset] << 8) | span[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > span.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 7) return false;
                var height = (span[offset + 3] << 8) | span[offset + 4];
                var width = (span[offset + 5] << 8) | span[offset + 6];
                if (width is > 0 and <= 2048 && height is > 0 and <= 2048) { extension = ".jpg"; return true; }
                return false;
            }
            offset += segmentLength;
        }
        return false;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static bool TrySlackImageUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo) || parsed.Host is not ("avatars.slack-edge.com" or "secure.gravatar.com")) return false;
        uri = parsed;
        return true;
    }

    private static bool SameOrigin(Uri candidate, Uri configured) => candidate.Scheme == configured.Scheme &&
        string.Equals(candidate.IdnHost, configured.IdnHost, StringComparison.OrdinalIgnoreCase) && candidate.Port == configured.Port;
    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;
    private static bool IdMatches(JsonElement element, string property, string expected) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    private static bool IsMattermostUserId(string id) => id.Length == 26 && id.All(char.IsAsciiLetterOrDigit);
    private static bool IsSlackUserId(string id) => id.Length is >= 9 and <= 32 && id[0] is 'U' or 'W' && id.All(char.IsAsciiLetterOrDigit);
    private static bool IsDiscordId(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var parsed) && parsed != 0;
    private static bool IsDiscordAvatarHash(string? value)
    {
        if (value is null) return false;
        var hash = value.StartsWith("a_", StringComparison.Ordinal) ? value[2..] : value;
        return hash.Length is >= 16 and <= 128 && hash.All(Uri.IsHexDigit);
    }
    private static void TryDelete(string path) { try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    public void Dispose()
    {
        lock (_httpSync)
        {
            if (_disposed) return;
            _disposed = true;
            _defaultHttp?.Dispose();
            _mattermostDirectHttp?.Dispose();
            if (_defaultHttp is null && _ownsInjectedHandler) _injectedHandler?.Dispose();
            _injectedHandler = null;
        }
    }

    private sealed class RequestBudget(int limit) : IDisposable
    {
        private int _remaining = limit;
        public bool TryTake() => Interlocked.Decrement(ref _remaining) >= 0;
        public void Dispose() { }
    }

    private sealed record RequestContext(string Provider, string Origin, string UserId, string Token, Uri? MattermostBase, bool MattermostBypassProxy);
}
