#if CAB_PORTABLE_UPDATE_TEST
using System.Net;
using System.Text.Json;
using ChatAgentBridge.Core.Updates;

namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>Offline release transport used only by explicitly enabled portable update test builds.</summary>
internal static class PortableUpdateTestFeed
{
    private const string FeedUrl = "https://api.github.com/repos/mmiyaji/chat-agent-bridge/releases?per_page=100";
    private const string StablePagesFeedUrl = "https://mmiyaji.github.io/chat-agent-bridge/updates/stable.json";
    private const string PreviewPagesFeedUrl = "https://mmiyaji.github.io/chat-agent-bridge/updates/preview.json";
    private const string AssetPrefix = "https://github.com/mmiyaji/chat-agent-bridge/releases/download/";
    private const int MaxApiBytes = 2 * 1024 * 1024;
    private const long MaxAssetBytes = 1024L * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static HttpClient CreateClient()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("CAB_PORTABLE_UPDATE_TEST_DIRECTORY");
        if (string.IsNullOrWhiteSpace(configuredDirectory) || !Path.IsPathFullyQualified(configuredDirectory))
            throw new InvalidOperationException("Portable update test build requires CAB_PORTABLE_UPDATE_TEST_DIRECTORY to name an absolute fixture directory.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDirectory));
        var feedPath = ResolveFixtureFile(root, "releases.json");
        var assetsPath = ResolveFixtureFile(root, "assets");
        if (!File.Exists(feedPath))
            throw new FileNotFoundException($"Portable update test fixture is missing '{feedPath}'.", feedPath);
        if (!Directory.Exists(assetsPath))
            throw new DirectoryNotFoundException($"Portable update test fixture is missing '{assetsPath}'.");
        ValidateFixture(root, feedPath, assetsPath);

        return new HttpClient(new FixtureHandler(feedPath, assetsPath))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static void ValidateFixture(string root, string feedPath, string assetsPath)
    {
        RejectReparsePoint(root);
        RejectReparsePoint(feedPath);
        RejectReparsePoint(assetsPath);

        var length = new FileInfo(feedPath).Length;
        if (length > MaxApiBytes)
            throw new InvalidDataException($"Portable update test fixture '{feedPath}' exceeds the 2 MB release-feed limit.");

        using var json = JsonDocument.Parse(File.ReadAllBytes(feedPath));
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Portable update test fixture '{feedPath}' must contain the GitHub releases JSON array.");

        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                var name = nameElement.GetString();
                if (name is null || !name.StartsWith("ChatAgentBridge.Portable-", StringComparison.Ordinal)) continue;
                if (!IsSafeAssetName(name))
                    throw new InvalidDataException($"Portable update test fixture contains an invalid asset name '{name}'.");
                var assetPath = ResolveFixtureFile(assetsPath, name!);
                if (!File.Exists(assetPath))
                    throw new FileNotFoundException($"Portable update test fixture is missing referenced asset '{assetPath}'.", assetPath);
                RejectReparsePoint(assetPath);
                if (new FileInfo(assetPath).Length > MaxAssetBytes)
                    throw new InvalidDataException($"Portable update test fixture asset '{assetPath}' exceeds the 1 GB asset limit.");
            }
        }
    }

    private static string ResolveFixtureFile(string directory, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
            throw new InvalidDataException("Portable update test fixture path escaped its configured directory.");
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(path);
    }

    private static bool IsSafeAssetName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Equals(Path.GetFileName(name), StringComparison.Ordinal) &&
        name.EndsWith(".zip", StringComparison.Ordinal) &&
        !name.Contains('/') && !name.Contains('\\') &&
        !name.Contains(':') && !name.Contains("..", StringComparison.Ordinal);

    private sealed class FixtureHandler(string feedPath, string assetsPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateResponse(request));
        }

        private HttpResponseMessage CreateResponse(HttpRequestMessage request)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri || !IsPlainHttpsUri(uri))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (uri.AbsoluteUri.Equals(StablePagesFeedUrl, StringComparison.Ordinal) ||
                uri.AbsoluteUri.Equals(PreviewPagesFeedUrl, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (uri.AbsoluteUri.Equals(FeedUrl, StringComparison.Ordinal))
                return OpenFileResponse(feedPath, MaxApiBytes);

            if (uri.Query.Length != 0 || !uri.AbsoluteUri.StartsWith(AssetPrefix, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var suffix = uri.AbsoluteUri[AssetPrefix.Length..];
            var separator = suffix.IndexOf('/');
            if (separator <= 0 || separator != suffix.LastIndexOf('/'))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            string tag;
            string name;
            try
            {
                tag = Uri.UnescapeDataString(suffix[..separator]);
                name = Uri.UnescapeDataString(suffix[(separator + 1)..]);
            }
            catch (UriFormatException) { return new HttpResponseMessage(HttpStatusCode.NotFound); }

            if (!IsSafeTag(tag) || !IsSafeAssetName(name) ||
                !Uri.EscapeDataString(tag).Equals(suffix[..separator], StringComparison.Ordinal) ||
                !Uri.EscapeDataString(name).Equals(suffix[(separator + 1)..], StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var path = ResolveFixtureFile(assetsPath, name);
            if (!File.Exists(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            RejectReparsePoint(path);
            return OpenFileResponse(path, MaxAssetBytes);
        }

        private static bool IsPlainHttpsUri(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Fragment.Length == 0;

        private static bool IsSafeTag(string tag) =>
            tag.Length is > 0 and <= 128 && tag is not "." and not ".." &&
            !tag.Contains("..", StringComparison.Ordinal) &&
            tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');

        private static HttpResponseMessage OpenFileResponse(string path, long maxBytes)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
            {
                stream.Dispose();
                throw new InvalidDataException($"Portable update test fixture file '{path}' exceeds its size limit.");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentLength = stream.Length;
            return response;
        }
    }
}
#endif
