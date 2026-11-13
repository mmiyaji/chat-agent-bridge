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

    [Fact]
    public async Task AccentRoundTripsAndUnknownAccentKeepsTheTheme()
    {
        using var temp = new TempDirectory();
        var store = new AppearancePreferencesStore(new SettingsStore(temp.Path));
        await store.SaveAsync(new AppearancePreferences("dark", "konjyo"));
        Assert.Equal(new AppearancePreferences("dark", "konjyo"), await store.LoadAsync());

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "appearance.json"), "{\"Theme\":\"light\",\"Accent\":\"neon\"}");
        Assert.Equal(new AppearancePreferences("light", AccentPalette.System), await store.LoadAsync());
        // Files written before accents existed have no Accent property.
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "appearance.json"), "{\"Theme\":\"dark\"}");
        Assert.Equal(new AppearancePreferences("dark", AccentPalette.System), await store.LoadAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppearancePreferences("dark", "neon")));
    }

    [Fact]
    public void PaletteHasUniqueIdsAndFiveBasicColors()
    {
        Assert.Equal(30, AccentPalette.Colors.Count);
        Assert.Equal(AccentPalette.Colors.Count, AccentPalette.Colors.Select(color => color.Id).Distinct().Count());
        Assert.Equal(["konjyo", "moegi", "yamabuki", "kurenai", "sakura"],
            AccentPalette.Colors.Where(color => color.IsBasic).Select(color => color.Id));
    }

    [Fact]
    public void EveryAccentKeepsAccentFillTextReadableInBothThemes()
    {
        foreach (var color in AccentPalette.Colors)
        {
            // Light theme fills use Dark1 under white text; dark theme fills use Light2 under black text.
            var light = AccentPalette.CreateRamp(color, darkTheme: false);
            var dark = AccentPalette.CreateRamp(color, darkTheme: true);
            Assert.True(AccentPalette.Contrast(light.Dark1, 0xFFFFFF) >= 4.5, $"{color.Id} light {light.Dark1:X6}");
            Assert.True(AccentPalette.Contrast(dark.Light2, 0x000000) >= 4.5, $"{color.Id} dark {dark.Light2:X6}");
            // The ramp runs from darkest to lightest.
            uint[] shades = [light.Dark3, light.Dark2, light.Dark1, light.Accent, light.Light1, light.Light2, light.Light3];
            Assert.Equal(shades.OrderBy(Luma), shades);
        }

        static double Luma(uint rgb) => AccentPalette.Contrast(rgb, 0x000000);
    }

    [Fact]
    public void ReadableHiMemoColorsAreUsedUnchanged()
    {
        var konjyo = AccentPalette.Find("konjyo")!;
        Assert.Equal(0x113285u, AccentPalette.CreateRamp(konjyo, darkTheme: false).Dark1);
        Assert.Equal(0x9CB1FFu, AccentPalette.CreateRamp(konjyo, darkTheme: true).Light2);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-appearance-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
