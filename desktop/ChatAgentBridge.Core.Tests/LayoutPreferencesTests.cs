using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Appearance;

namespace ChatAgentBridge.Core.Tests;

public sealed class LayoutPreferencesTests
{
    [Fact]
    public async Task MissingFileKeepsBuiltInLayout()
    {
        using var temp = new TempDirectory();
        var preferences = await new LayoutPreferencesStore(new SettingsStore(temp.Path)).LoadAsync();
        Assert.Equal(new LayoutPreferences(), preferences);
    }

    [Fact]
    public async Task SavedWidthsRoundTrip()
    {
        using var temp = new TempDirectory();
        var store = new LayoutPreferencesStore(new SettingsStore(temp.Path));
        await store.SaveAsync(new LayoutPreferences(SidebarWidth: 300, SettingsNavigationWidth: 240, InboxListWidth: 520));
        Assert.Equal(new LayoutPreferences(300, 240, 520), await store.LoadAsync());
    }

    [Fact]
    public void WidthsAreClampedAndNonFiniteValuesDropped()
    {
        var normalized = new LayoutPreferences(SidebarWidth: 10, SettingsNavigationWidth: 9999, InboxListWidth: double.NaN).Normalized();
        Assert.Equal(LayoutPreferences.MinSidebarWidth, normalized.SidebarWidth);
        Assert.Equal(LayoutPreferences.MaxSettingsNavigationWidth, normalized.SettingsNavigationWidth);
        Assert.Null(normalized.InboxListWidth);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SidebarWidth\":\"wide\"}")]
    [InlineData("null")]
    public async Task UnreadableFileFallsBackToDefaults(string content)
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "layout.json"), content);
        Assert.Equal(new LayoutPreferences(), await new LayoutPreferencesStore(new SettingsStore(temp.Path)).LoadAsync());
    }

    [Fact]
    public async Task OutOfRangeStoredWidthIsClampedOnLoad()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "layout.json"), "{\"SidebarWidth\":5000}");
        var preferences = await new LayoutPreferencesStore(new SettingsStore(temp.Path)).LoadAsync();
        Assert.Equal(LayoutPreferences.MaxSidebarWidth, preferences.SidebarWidth);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-layout-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
