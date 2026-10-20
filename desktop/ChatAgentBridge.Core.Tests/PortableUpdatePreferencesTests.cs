using ChatAgentBridge.Core.Updates;

public sealed class PortableUpdatePreferencesTests
{
    [Fact]
    public async Task StoreRoundTripsIndependentPreferencesAttemptTimeAndCandidate()
    {
        var directory = TempDirectory();
        try
        {
            var candidate = Candidate();
            var preferences = new PortableUpdatePreferences(1, false, true,
                new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), candidate);
            var store = new PortableUpdatePreferencesStore(directory);

            await store.SaveAsync(preferences);

            Assert.Equal(preferences, await store.LoadAsync());
            Assert.True(File.Exists(Path.Combine(directory, "portable-update.json")));
        }
        finally { DeleteTree(directory); }
    }

    [Fact]
    public void CheckIsDueAtSixHoursAndAfterFailuresAreRecorded()
    {
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        Assert.True(PortableUpdateCheckPolicy.IsDue(null, now));
        Assert.False(PortableUpdateCheckPolicy.IsDue(now.AddHours(-5).AddMinutes(59), now));
        Assert.True(PortableUpdateCheckPolicy.IsDue(now.AddHours(-6), now));
    }

    [Fact]
    public void CachedCandidateMustBeNewerAndMatchPreviewPreferenceArchitectureAndFixedGitHubUrls()
    {
        var candidate = Candidate();
        Assert.True(PortableReleaseClient.IsValidCachedCandidate(candidate, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate, "0.1.0-preview.5", "x64", false));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate, "0.1.0-preview.6", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate, "0.1.0-preview.5", "arm64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { DownloadUri = new Uri("https://evil.example/file.zip") }, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { ReleasePage = new Uri("https://github.com/other/repo/releases/tag/v0.1.0-preview.6") }, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { DownloadUri = null! }, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { DownloadUri = new Uri("relative.zip", UriKind.Relative) }, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { ReleasePage = null! }, "0.1.0-preview.5", "x64", true));
        Assert.False(PortableReleaseClient.IsValidCachedCandidate(candidate with { ReleasePage = new Uri("relative", UriKind.Relative) }, "0.1.0-preview.5", "x64", true));
    }

    [Fact]
    public async Task StoreRejectsOversizedPreferencesFile()
    {
        var directory = TempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "portable-update.json"), new byte[256 * 1024 + 1]);
            var store = new PortableUpdatePreferencesStore(directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally { DeleteTree(directory); }
    }

    private static PortableRelease Candidate() => new(
        "0.1.0-preview.6",
        "v0.1.0-preview.6",
        "ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip",
        new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/download/v0.1.0-preview.6/ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip"),
        new string('a', 64),
        123,
        new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6"));

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-update-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
