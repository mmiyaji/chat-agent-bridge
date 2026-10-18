using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Appearance;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppearancePreferencesTests
{
    [Fact]
    public async Task MissingPreferenceDefaultsToSystemWithoutTouchingSettings()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(temp.Path);
        await settingsStore.SaveAsync(new BridgeSettings { WorkspaceRoot = "C:/workspace" });
        var settingsBefore = await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"));
        var store = new AppearancePreferencesStore(settingsStore);

        Assert.Equal(new AppearancePreferences("system"), await store.LoadAsync());
        Assert.False(File.Exists(Path.Combine(temp.Path, "appearance.json")));
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Theme\":null}")]
    [InlineData("{\"Theme\":\"unsupported\"}")]
    public async Task CorruptNullOrUnknownPreferenceFallsBackAndPreservesFile(string contents)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "appearance.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new AppearancePreferencesStore(new SettingsStore(temp.Path));

        Assert.Equal(new AppearancePreferences("system"), await store.LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task SaveAndLoadRoundTrip(string theme)
    {
        using var temp = new TempDirectory();
        var store = new AppearancePreferencesStore(new SettingsStore(temp.Path));
        var expected = new AppearancePreferences(theme);

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
        Assert.True(File.Exists(Path.Combine(temp.Path, "appearance.json")));
    }

    [Fact]
    public async Task SaveRejectsUnknownPreferenceWithoutChangingExistingFile()
    {
        using var temp = new TempDirectory();
        var store = new AppearancePreferencesStore(new SettingsStore(temp.Path));
        await store.SaveAsync(new AppearancePreferences("dark"));
        var before = await File.ReadAllTextAsync(Path.Combine(temp.Path, "appearance.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppearancePreferences("auto")));

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(temp.Path, "appearance.json")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-appearance-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
