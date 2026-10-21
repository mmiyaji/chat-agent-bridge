using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Updates;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.0-preview.5", "0.1.0-preview.6", -1)]
    [InlineData("0.1.0-preview.6", "0.1.0", -1)]
    [InlineData("v1.10.0", "1.9.9", 1)]
    [InlineData("0.1.0-preview.5", "0.1.0-preview.5", 0)]
    public void ComparesSupportedReleaseVersions(string left, string right, int expectedSign)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var a));
        Assert.True(ReleaseVersion.TryParse(right, out var b));
        Assert.Equal(expectedSign, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("01.2.3")]
    public void RejectsUnsupportedVersionSyntax(string version) => Assert.False(ReleaseVersion.TryParse(version, out _));
}

public sealed class PortableReleaseClientTests
{
    private const string AssetName = "ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("portable archive bytes");
    private static string Digest => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Payload));

    [Fact]
    public async Task SelectsNewestMatchingNonDraftReleaseAndRespectsPrereleaseOption()
    {
        var handler = new StubHandler(_ => JsonResponse(Feed(
            Release("v0.1.0-preview.6", AssetName, Digest),
            Release("v0.1.0-preview.5", "ChatAgentBridge.Portable-0.1.0-preview.5-win-x64.zip", Digest),
            Release("v0.1.0", "ChatAgentBridge.Portable-0.1.0-win-x64.zip", Digest, prerelease: false),
            Release("v0.2.0-preview.1", "ChatAgentBridge.Portable-0.2.0-preview.1-win-x64.zip", Digest, draft: true))));
        using var client = new PortableReleaseClient(new HttpClient(handler));
        Assert.Equal("0.1.0", (await client.FindUpdateAsync("0.1.0-preview.5", "x64", true, default))?.Version);
        Assert.Equal("0.1.0", (await client.FindUpdateAsync("0.1.0-preview.5", "x64", false, default))?.Version);
    }

    [Fact]
    public async Task IncludesPreviewOnlyWhenRequested()
    {
        var handler = new StubHandler(_ => JsonResponse(Feed(
            Release("v0.1.0-preview.6", AssetName, Digest),
            Release("v0.1.0-preview.5", "ChatAgentBridge.Portable-0.1.0-preview.5-win-x64.zip", Digest))));
        using var client = new PortableReleaseClient(new HttpClient(handler));
        Assert.Equal("0.1.0-preview.6", (await client.FindUpdateAsync("0.1.0-preview.5", "x64", true, default))?.Version);
        Assert.Null(await client.FindUpdateAsync("0.1.0-preview.5", "x64", false, default));
    }

    [Fact]
    public async Task StableReleaseWinsOverPreviewsAndDoesNotReturnCurrentOrOlderVersions()
    {
        var handler = new StubHandler(_ => JsonResponse(Feed(
            Release("v0.1.0-preview.99", "ChatAgentBridge.Portable-0.1.0-preview.99-win-x64.zip", Digest),
            Release("v0.1.0", "ChatAgentBridge.Portable-0.1.0-win-x64.zip", Digest, prerelease: false))));
        using var client = new PortableReleaseClient(new HttpClient(handler));
        Assert.Equal("0.1.0", (await client.FindUpdateAsync("0.1.0-preview.6", "x64", true, default))?.Version);
        Assert.Null(await client.FindUpdateAsync("0.1.0", "x64", true, default));
    }

    [Fact]
    public async Task SelectsOnlyMatchingArchitectureAndRejectsMissingDigestOrUntrustedOrigins()
    {
        var rel = Release("v0.1.0-preview.6", AssetName, null);
        rel["assets"] = new[]
        {
            Asset("ChatAgentBridge.Portable-0.1.0-preview.6-win-arm64.zip", Digest),
            Asset(AssetName, null),
            Asset(AssetName, Digest, "https://evil.example/payload.zip")
        };
        using var client = new PortableReleaseClient(new HttpClient(new StubHandler(_ => JsonResponse(Feed(rel)))));
        Assert.Null(await client.FindUpdateAsync("0.1.0-preview.5", "x64", true, default));
    }

    [Fact]
    public async Task RejectsUntrustedReleasePageAndInvalidAssetSize()
    {
        var badPage = Release("v0.1.0-preview.6", AssetName, Digest);
        badPage["html_url"] = "https://github.com/other/repo/releases/tag/v0.1.0-preview.6";
        var badSize = Release("v0.1.0-preview.7", "ChatAgentBridge.Portable-0.1.0-preview.7-win-x64.zip", Digest);
        ((Dictionary<string, object?>)((object[])badSize["assets"]!)[0]!)["size"] = 0;
        using var client = new PortableReleaseClient(new HttpClient(new StubHandler(_ => JsonResponse(Feed(badPage, badSize)))));
        Assert.Null(await client.FindUpdateAsync("0.1.0-preview.5", "x64", true, default));
    }

    [Fact]
    public async Task RejectsOversizedApiBody()
    {
        using var client = new PortableReleaseClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + new string(' ', 2 * 1024 * 1024) + "]") })));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.0", "x64", true, default));
    }

    [Fact]
    public async Task RequestsChannelSpecificPagesFeedsAndAcceptsStableReleasesFromPreviewFeed()
    {
        var stable = Release("v0.2.0", "ChatAgentBridge.Portable-0.2.0-win-x64.zip", Digest, prerelease: false);
        var preview = Release("v0.3.0-preview.1", "ChatAgentBridge.Portable-0.3.0-preview.1-win-x64.zip", Digest, prerelease: true);
        var handler = new StubHandler(request =>
        {
            var isPreview = request.RequestUri == PortableReleaseClient.PreviewFeedUri;
            return JsonResponse(PagesFeed(isPreview ? "preview" : "stable", isPreview ? [stable, preview] : [stable]));
        }, routePagesToLegacyApi: false);
        using var client = new PortableReleaseClient(new HttpClient(handler));

        var stableResult = await client.FindUpdateAsync("0.1.0", "x64", false, default);
        var previewResult = await client.FindUpdateAsync("0.1.0", "x64", true, default);

        Assert.Equal("0.2.0", stableResult?.Version);
        Assert.Equal("0.3.0-preview.1", previewResult?.Version);
        Assert.Equal(PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
        Assert.Equal(PortableReleaseClient.PreviewFeedUri, handler.Requests[1].RequestUri);
    }

    [Theory]
    [InlineData("wrong-channel", "stable")]
    [InlineData("missing-releases", "stable")]
    [InlineData("legacy-array", "stable")]
    public async Task RejectsInvalidPagesEnvelopeWithoutFallingBack(string invalidEnvelope, string channel)
    {
        var responseBody = invalidEnvelope switch
        {
            "wrong-channel" => PagesFeed("preview", [Release("v0.2.0", "ChatAgentBridge.Portable-0.2.0-win-x64.zip", Digest, prerelease: false)]),
            "missing-releases" => "{\"schemaVersion\":1,\"channel\":\"stable\"}",
            _ => Feed(Release("v0.2.0", "ChatAgentBridge.Portable-0.2.0-win-x64.zip", Digest, prerelease: false))
        };
        var handler = new StubHandler(_ => JsonResponse(responseBody), routePagesToLegacyApi: false);
        using var client = new PortableReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.0", "x64", channel == "preview", default));

        Assert.Single(handler.Requests);
        Assert.Equal(channel == "preview" ? PortableReleaseClient.PreviewFeedUri : PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
    }

    [Fact]
    public async Task FallsBackToGitHubApiOnlyWhenPagesReturnsNotFound()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "api.github.com"
            ? JsonResponse(Feed(Release("v0.2.0", "ChatAgentBridge.Portable-0.2.0-win-x64.zip", Digest, prerelease: false)))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new PortableReleaseClient(new HttpClient(handler));

        var result = await client.FindUpdateAsync("0.1.0", "x64", false, default);

        Assert.Equal("0.2.0", result?.Version);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
        Assert.Equal("https://api.github.com/repos/mmiyaji/chat-agent-bridge/releases?per_page=100", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PagesServerErrorsAreNotHiddenByGitHubFallback()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), routePagesToLegacyApi: false);
        using var client = new PortableReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindUpdateAsync("0.1.0", "x64", false, default));

        Assert.Single(handler.Requests);
        Assert.Equal(PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
    }

    [Fact]
    public async Task MalformedPagesJsonIsSurfacedWithoutLegacyFallback()
    {
        var handler = new StubHandler(_ => JsonResponse("{ malformed json"), routePagesToLegacyApi: false);
        using var client = new PortableReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<JsonException>(() => client.FindUpdateAsync("0.1.0", "x64", false, default));

        Assert.Single(handler.Requests);
        Assert.Equal(PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
    }

    [Fact]
    public async Task FailedLegacyFallbackIsSurfacedWithoutRetryingAnotherEndpoint()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new PortableReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindUpdateAsync("0.1.0", "x64", false, default));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(PortableReleaseClient.StableFeedUri, handler.Requests[0].RequestUri);
        Assert.Equal("api.github.com", handler.Requests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task DownloadsOnlyAfterTrustedRedirectAndVerifiesHashAndSize()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/download/file") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        using var client = new PortableReleaseClient(new HttpClient(handler));
        var path = TempPath();
        try
        {
            await client.DownloadAsync(Candidate(Digest[7..]), path, null, default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { DeleteFile(path); }
    }

    [Fact]
    public async Task RejectsBadRedirectAndHashAndLeavesNoDestinationOrPartialFile()
    {
        var badRedirect = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/file") } });
        using var client = new PortableReleaseClient(new HttpClient(badRedirect)); var path = TempPath();
        try { await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(Candidate(Digest[7..]), path, null, default)); AssertNoDownloadFiles(path); }
        finally { DeleteFile(path); }

        var wrongHash = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        using var second = new PortableReleaseClient(new HttpClient(wrongHash)); path = TempPath();
        try { await Assert.ThrowsAsync<InvalidDataException>(() => second.DownloadAsync(Candidate(new string('0', 64)), path, null, default)); AssertNoDownloadFiles(path); }
        finally { DeleteFile(path); }
    }

    [Fact]
    public async Task DownloadHonorsCancellationAndDeclaredSizeBound()
    {
        var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent(() => cts.Cancel()) });
        using var client = new PortableReleaseClient(new HttpClient(handler)); var path = TempPath();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Candidate(Digest[7..]), path, null, cts.Token)); AssertNoDownloadFiles(path); }
        finally { DeleteFile(path); }
    }

    [Fact]
    public async Task DownloadRejectsCandidateAboveOneGigabyteBeforeRequesting()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var client = new PortableReleaseClient(new HttpClient(handler)); var path = TempPath();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Candidate(Digest[7..]) with { Size = 1024L * 1024 * 1024 + 1 }, path, null, default));
            Assert.Empty(handler.Requests);
            AssertNoDownloadFiles(path);
        }
        finally { DeleteFile(path); }
    }

    private static PortableRelease Candidate(string digest) => new("0.1.0-preview.6", "v0.1.0-preview.6", AssetName, new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/download/v0.1.0-preview.6/" + AssetName), digest, Payload.Length, new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6"));
    private static Dictionary<string, object?> Release(string tag, string asset, string? digest, bool draft = false, bool? prerelease = null)
        => new() { ["draft"] = draft, ["tag_name"] = tag, ["prerelease"] = prerelease ?? tag.Contains("preview", StringComparison.Ordinal), ["html_url"] = "https://github.com/mmiyaji/chat-agent-bridge/releases/tag/" + tag, ["assets"] = new[] { Asset(asset, digest, tag: tag) } };
    private static Dictionary<string, object?> Asset(string name, string? digest, string? url = null, string tag = "v0.1.0-preview.6")
    {
        var asset = new Dictionary<string, object?> { ["name"] = name, ["size"] = Payload.Length, ["browser_download_url"] = url ?? "https://github.com/mmiyaji/chat-agent-bridge/releases/download/" + tag + "/" + name };
        if (digest is not null) asset["digest"] = digest;
        return asset;
    }
    private static string Feed(params Dictionary<string, object?>[] releases) => JsonSerializer.Serialize(releases);
    private static string PagesFeed(string channel, params Dictionary<string, object?>[] releases) => JsonSerializer.Serialize(new { schemaVersion = 1, channel, releases });
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "cab-update-test-" + Guid.NewGuid().ToString("N") + ".zip");
    private static void DeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }
    private static void AssertNoDownloadFiles(string path) { Assert.False(File.Exists(path)); Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".download-*")); }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response, bool routePagesToLegacyApi = true) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (routePagesToLegacyApi && request.RequestUri is { } uri &&
                (uri == PortableReleaseClient.StableFeedUri || uri == PortableReleaseClient.PreviewFeedUri))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(response(request));
        }
    }

    private sealed class SlowContent(Action cancel) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = Payload.Length; return true; }
        protected override async Task<Stream> CreateContentReadStreamAsync() { cancel(); await Task.Yield(); return new MemoryStream(Payload); }
    }
}

public sealed class PortableArchiveStagerTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/rooted")]
    [InlineData("folder\\file")]
    [InlineData("Data/settings.json")]
    [InlineData("CON.txt")]
    [InlineData("file.")]
    [InlineData("private-data/token.json")]
    public void RejectsUnsafePaths(string path) => Assert.Throws<InvalidDataException>(() => PortableArchiveStager.ValidateRelativePath(path));

    [Fact]
    public async Task ExtractsValidArchiveAndReturnsHashForEveryFile()
    {
        var archive = BuildArchive(); var temp = TempDir();
        try
        {
            var staged = await PortableArchiveStager.ExtractAsync(archive, Path.Combine(temp, "staged"), Candidate("x64"), default);
            Assert.Contains("ChatAgentBridge.Desktop.exe", staged.Files.Keys);
            Assert.Contains("SHA256SUMS.txt", staged.Files.Keys);
            Assert.True(File.Exists(Path.Combine(staged.DirectoryPath, "ChatAgentBridge.Desktop.exe")));
        }
        finally { DeleteTree(temp); DeleteArchive(archive); }
    }

    [Theory]
    [InlineData("ChatAgentBridge.Portable/../escape.txt", false)]
    [InlineData("ChatAgentBridge.Portable/Data/settings.json", false)]
    [InlineData("ChatAgentBridge.Portable/file.txt", true)]
    public async Task RejectsZipSlipProtectedDataAndUnixSymlink(string entryName, bool symlink)
    {
        var archive = BuildArchive(entryName, symlink); var temp = TempDir(); var destination = Path.Combine(temp, "staged");
        try { await Assert.ThrowsAsync<InvalidDataException>(() => PortableArchiveStager.ExtractAsync(archive, destination, Candidate("x64"), default)); Assert.False(Directory.Exists(destination)); }
        finally { DeleteTree(temp); DeleteArchive(archive); }
    }

    [Fact]
    public async Task RejectsCaseCollisionsAndWrongPeArchitecture()
    {
        var archive = BuildArchive(extraCollision: true); var temp = TempDir();
        try { await Assert.ThrowsAsync<InvalidDataException>(() => PortableArchiveStager.ExtractAsync(archive, Path.Combine(temp, "collide"), Candidate("x64"), default)); }
        finally { DeleteTree(temp); DeleteArchive(archive); }

        archive = BuildArchive(peArchitecture: "arm64"); temp = TempDir();
        try { await Assert.ThrowsAsync<InvalidDataException>(() => PortableArchiveStager.ExtractAsync(archive, Path.Combine(temp, "wrong-pe"), Candidate("x64"), default)); }
        finally { DeleteTree(temp); DeleteArchive(archive); }
    }

    [Fact]
    public async Task ExistingDestinationIsNeverModified()
    {
        var archive = BuildArchive(); var temp = TempDir(); var destination = Path.Combine(temp, "existing"); Directory.CreateDirectory(destination); await File.WriteAllTextAsync(Path.Combine(destination, "sentinel"), "keep");
        try { await Assert.ThrowsAsync<IOException>(() => PortableArchiveStager.ExtractAsync(archive, destination, Candidate("x64"), default)); Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel"))); }
        finally { DeleteTree(temp); DeleteArchive(archive); }
    }

    private static PortableRelease Candidate(string arch)
    {
        var suffix = arch == "arm64" ? "win-arm64" : "win-x64";
        var name = "ChatAgentBridge.Portable-0.1.0-preview.6-" + suffix + ".zip";
        return new("0.1.0-preview.6", "v0.1.0-preview.6", name, new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/download/v0.1.0-preview.6/" + name), new string('a', 64), 1, new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6"));
    }

    private static string BuildArchive(string? maliciousEntry = null, bool symlink = false, bool extraCollision = false, string peArchitecture = "x64")
    {
        var root = "ChatAgentBridge.Portable/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["ChatAgentBridge.Desktop.exe"] = MakePe(peArchitecture), ["ChatAgentBridge.Desktop.dll"] = [1, 2], ["ChatAgentBridge.Desktop.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{}"), ["ChatAgentBridge.Updater.exe"] = MakePe("x64"),
            ["portable-release.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"releaseVersion\":\"0.1.0-preview.6\",\"architecture\":\"x64\"}")
        };
        files["portable-files.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 1, files = files.Keys.Append("portable-files.json").Append("SHA256SUMS.txt").ToArray() }));
        files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\n", files.Select(x => $"{Convert.ToHexStringLower(SHA256.HashData(x.Value))} *{x.Key}")) + "\n");
        var path = Path.Combine(Path.GetTempPath(), "cab-update-archive-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in files)
        {
            var entry = zip.CreateEntry(root + item.Key); using var stream = entry.Open(); stream.Write(item.Value);
        }
        if (extraCollision) { var e = zip.CreateEntry(root + "CHATAGENTBRIDGE.DESKTOP.DLL"); using var s = e.Open(); s.WriteByte(3); }
        if (maliciousEntry is not null)
        {
            var entry = zip.CreateEntry(maliciousEntry); if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var stream = entry.Open(); stream.WriteByte(0);
        }
        return path;
    }
    private static byte[] MakePe(string arch)
    {
        var bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(64).CopyTo(bytes, 0x3c); BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 64); BitConverter.GetBytes((ushort)(arch == "arm64" ? 0xAA64 : 0x8664)).CopyTo(bytes, 68); return bytes;
    }
    private static string TempDir() { var path = Path.Combine(Path.GetTempPath(), "cab-update-stager-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void DeleteArchive(string path) { if (File.Exists(path)) File.Delete(path); }
    private static void DeleteTree(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
