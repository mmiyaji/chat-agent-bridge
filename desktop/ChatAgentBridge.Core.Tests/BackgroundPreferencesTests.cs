namespace ChatAgentBridge.Core.Tests;

public sealed class BackgroundPreferencesTests
{
    [Fact]
    public async Task MissingAndMalformedPreferencesKeepNormalCloseBehavior()
    {
        using var temp = new TempDirectory();
        var store = new BackgroundPreferencesStore(new SettingsStore(temp.Path));
        Assert.Equal(WindowCloseBehavior.AskEveryTime, (await store.LoadAsync()).CloseBehavior);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "background.json"), "{bad");
        Assert.Equal(WindowCloseBehavior.AskEveryTime, (await store.LoadAsync()).CloseBehavior);
    }

    [Theory]
    [InlineData("{\"CloseToTray\":true}", WindowCloseBehavior.MoveToSystemTray)]
    [InlineData("{\"CloseToTray\":false}", WindowCloseBehavior.AskEveryTime)]
    [InlineData("{\"CloseBehavior\":\"ExitApp\"}", WindowCloseBehavior.ExitApp)]
    [InlineData("{\"CloseBehavior\":\"unknown\",\"CloseToTray\":true}", WindowCloseBehavior.AskEveryTime)]
    [InlineData("{\"CloseBehavior\":4}", WindowCloseBehavior.AskEveryTime)]
    [InlineData("null", WindowCloseBehavior.AskEveryTime)]
    [InlineData("[]", WindowCloseBehavior.AskEveryTime)]
    [InlineData("true", WindowCloseBehavior.AskEveryTime)]
    [InlineData("{\"CloseBehavior\":\"2\"}", WindowCloseBehavior.AskEveryTime)]
    [InlineData("{\"CloseBehavior\":\"1\"}", WindowCloseBehavior.AskEveryTime)]
    public async Task LoadsLegacyAndCanonicalValuesWithSafeFallback(string json, WindowCloseBehavior expected)
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "background.json"), json);

        var loaded = await new BackgroundPreferencesStore(new SettingsStore(temp.Path)).LoadAsync();

        Assert.Equal(expected, loaded.CloseBehavior);
    }

    [Theory]
    [InlineData(WindowCloseBehavior.AskEveryTime)]
    [InlineData(WindowCloseBehavior.MoveToSystemTray)]
    [InlineData(WindowCloseBehavior.ExitApp)]
    public async Task PreferenceSurvivesNewStoreWithoutChangingChatSettings(WindowCloseBehavior behavior)
    {
        using var temp = new TempDirectory();
        var settings = System.IO.Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(settings, "untouched credentials");
        await new BackgroundPreferencesStore(new SettingsStore(temp.Path)).SaveAsync(new(behavior));
        var loaded = await new BackgroundPreferencesStore(new SettingsStore(temp.Path)).LoadAsync();
        Assert.Equal(behavior, loaded.CloseBehavior);
        Assert.Equal("untouched credentials", await File.ReadAllTextAsync(settings));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        var saved = await File.ReadAllTextAsync(System.IO.Path.Combine(temp.Path, "background.json"));
        Assert.Contains("\"CloseBehavior\"", saved);
        Assert.DoesNotContain("\"CloseToTray\"", saved);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-background-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
