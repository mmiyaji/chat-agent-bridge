using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class FeatureTipsStoreTests
{
    [Fact]
    public async Task TipIsUnseenUntilMarked()
    {
        using var temp = new TempDirectory();
        var store = new FeatureTipsStore(new SettingsStore(temp.Path));

        Assert.False(await store.HasSeenAsync("chat-connect"));
        await store.MarkSeenAsync("chat-connect");

        Assert.True(await new FeatureTipsStore(new SettingsStore(temp.Path)).HasSeenAsync("chat-connect"));
        Assert.False(await store.HasSeenAsync("other-tip"));
    }

    [Fact]
    public async Task MarkingTwiceKeepsOneEntry()
    {
        using var temp = new TempDirectory();
        var store = new FeatureTipsStore(new SettingsStore(temp.Path));

        await store.MarkSeenAsync("chat-connect");
        await store.MarkSeenAsync("chat-connect");

        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "tips.json"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "chat-connect"));
    }

    [Fact]
    public async Task DamagedFileShowsTipsAgainAndIsRepaired()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "tips.json"), "{ not json");
        var store = new FeatureTipsStore(new SettingsStore(temp.Path));

        Assert.False(await store.HasSeenAsync("chat-connect"));
        await store.MarkSeenAsync("chat-connect");
        Assert.True(await store.HasSeenAsync("chat-connect"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-tips-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
