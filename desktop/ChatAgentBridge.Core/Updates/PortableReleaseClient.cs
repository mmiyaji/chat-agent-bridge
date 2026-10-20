using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatAgentBridge.Core.Updates;

public sealed record PortableRelease(string Version, string Tag, string AssetName, Uri DownloadUri, string Sha256, long Size, Uri ReleasePage);

/// <summary>Reads the fixed public GitHub release feed and downloads verified portable ZIPs.</summary>
public sealed class PortableReleaseClient : IDisposable
{
    private static readonly Uri ApiUri = new("https://api.github.com/repos/mmiyaji/chat-agent-bridge/releases?per_page=100");
    private static readonly Uri ReleasesRoot = new("https://github.com/mmiyaji/chat-agent-bridge/releases/");
    private const int MaxApiBytes = 2 * 1024 * 1024;
    private const long MaxAssetBytes = 1024L * 1024 * 1024;
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PortableReleaseClient(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatAgentBridge-PortableUpdater/1.0");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _ownsClient = true;
        }
        else _http = httpClient;
    }

    public async Task<PortableRelease?> FindUpdateAsync(string currentVersion, string architecture, bool includePrereleases, CancellationToken cancellationToken)
    {
        EnsureNoCredentialHeaders();
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) throw new ArgumentException("Current version is not a supported release version.", nameof(currentVersion));
        var suffix = architecture.ToLowerInvariant() switch { "x64" or "amd64" => "win-x64", "arm64" or "aarch64" => "win-arm64", _ => throw new ArgumentException("Architecture must be x64 or arm64.", nameof(architecture)) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxApiBytes) throw new InvalidDataException("GitHub release response exceeds 2 MB.");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var json = await ReadBoundedAsync(body, MaxApiBytes, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub release response is not an array.");
        PortableRelease? best = null;
        ReleaseVersion bestVersion = default;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) continue;
            var tag = tagElement.GetString()!;
            if (!ReleaseVersion.TryParse(tag, out var parsed) || parsed.CompareTo(current) <= 0 || (!includePrereleases && parsed.IsPreview)) continue;
            if (!release.TryGetProperty("prerelease", out var pre) || pre.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            if (pre.GetBoolean() != parsed.IsPreview) continue;
            if (!TryFixedReleasePage(release, tag, out var releasePage)) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var wantedName = $"ChatAgentBridge.Portable-{tag.TrimStart('v')}-{suffix}.zip";
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != wantedName) continue;
                if (!asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out var assetSize) || assetSize <= 0 || assetSize > MaxAssetBytes) continue;
                if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String || !TryDigest(digest.GetString(), out var sha)) continue;
                if (!asset.TryGetProperty("browser_download_url", out var url) || !IsExactAssetUrl(url.GetString(), tag, wantedName, out var download)) continue;
                if (best is null || parsed.CompareTo(bestVersion) > 0)
                {
                    best = new PortableRelease(parsed.ToString(), tag, wantedName, download, sha, assetSize, releasePage);
                    bestVersion = parsed;
                }
            }
        }
        return best;
    }

    public async Task DownloadAsync(PortableRelease candidate, string destinationZipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        EnsureNoCredentialHeaders();
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Size <= 0 || candidate.Size > MaxAssetBytes || !TryDigest("sha256:" + candidate.Sha256, out var expected) || !IsExactAssetUrl(candidate.DownloadUri.AbsoluteUri, candidate.Tag, candidate.AssetName, out _))
            throw new InvalidDataException("Release asset metadata is invalid.");
        var destination = Path.GetFullPath(destinationZipPath);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var partial = Path.Combine(parent, "." + Path.GetFileName(destination) + ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            Uri uri = candidate.DownloadUri;
            HttpResponseMessage? response = null;
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode)) break;
                var location = response.Headers.Location;
                response.Dispose(); response = null;
                if (redirects >= 4 || location is null) throw new HttpRequestException("Asset redirect limit exceeded.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsAllowedDownloadUri(uri)) throw new HttpRequestException("Asset redirect target is not trusted.");
            }
            using (response)
            {
                if (!response!.IsSuccessStatusCode) throw new HttpRequestException($"Asset download failed with HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength is long length && length != candidate.Size) throw new InvalidDataException("Downloaded asset size does not match release metadata.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                long total = 0;
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > candidate.Size || total > MaxAssetBytes) throw new InvalidDataException("Downloaded asset exceeds its declared size.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        progress?.Report((double)total / candidate.Size);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (total != candidate.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected))) throw new InvalidDataException("Downloaded asset size or SHA-256 does not match release metadata.");
            }
            File.Move(partial, destination, overwrite: true);
            progress?.Report(1);
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }

    private static bool TryFixedReleasePage(JsonElement release, string tag, out Uri page)
    {
        page = new Uri(ReleasesRoot, "tag/" + Uri.EscapeDataString(tag));
        return release.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && parsed.IsDefaultPort && parsed.UserInfo.Length == 0 && parsed.Query.Length == 0 && parsed.Fragment.Length == 0 &&
            parsed.AbsolutePath.Equals(page.AbsolutePath, StringComparison.Ordinal);
    }

    private static bool IsExactAssetUrl(string? text, string tag, string asset, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0) return false;
        var expected = $"/mmiyaji/chat-agent-bridge/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";
        if (!parsed.AbsolutePath.Equals(expected, StringComparison.Ordinal)) return false;
        uri = parsed; return true;
    }

    private static bool IsAllowedDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && DownloadHosts.Contains(uri.Host);
    private void EnsureNoCredentialHeaders()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Proxy-Authorization") || _http.DefaultRequestHeaders.Contains("Cookie") || _http.DefaultRequestHeaders.Contains("X-GitHub-Token"))
            throw new InvalidOperationException("Portable release requests cannot include authentication or cookie headers.");
    }
    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;
    private static bool TryDigest(string? value, out string digest)
    {
        digest = string.Empty;
        if (value is null || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || value.Length != 71) return false;
        var hex = value[7..];
        if (!hex.All(Uri.IsHexDigit)) return false;
        digest = hex.ToLowerInvariant(); return true;
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int max, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true) { var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > max) throw new InvalidDataException("GitHub release response exceeds 2 MB."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
