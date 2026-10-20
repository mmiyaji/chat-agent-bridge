namespace ChatAgentBridge.Core.Tests;

public sealed class BackgroundPreferencesTests
{
    [Fact]
    public async Task MissingAndMalformedPreferencesKeepNormalCloseBehavior()
    {
        using var temp = new TempDirectory();
        var store = new BackgroundPreferencesStore(new SettingsStore(temp.Path));
        Assert.False((await store.LoadAsync()).CloseToTray);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "background.json"), "{bad");
        Assert.False((await store.LoadAsync()).CloseToTray);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferenceSurvivesNewStoreWithoutChangingChatSettings(bool enabled)
    {
        using var temp = new TempDirectory();
        var settings = System.IO.Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(settings, "untouched credentials");
        await new BackgroundPreferencesStore(new SettingsStore(temp.Path)).SaveAsync(new(enabled));
        var loaded = await new BackgroundPreferencesStore(new SettingsStore(temp.Path)).LoadAsync();
        Assert.Equal(enabled, loaded.CloseToTray);
        Assert.Equal("untouched credentials", await File.ReadAllTextAsync(settings));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-background-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
