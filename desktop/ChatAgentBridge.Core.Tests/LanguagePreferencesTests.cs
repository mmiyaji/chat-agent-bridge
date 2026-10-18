using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core.Tests;

public sealed class LanguagePreferencesTests
{
    [Theory]
    [InlineData("system", "ja", "ja-JP")]
    [InlineData("system", "ja-JP", "ja-JP")]
    [InlineData("system", "ja-jp", "ja-JP")]
    [InlineData("system", "en-US", "en-US")]
    [InlineData("system", "en-JP", "en-US")]
    [InlineData("system", "ja-US", "ja-JP")]
    [InlineData("system", "fr-FR", "en-US")]
    [InlineData("system", "zh-Hans", "en-US")]
    [InlineData("system", "", "en-US")]
    [InlineData("system", null, "en-US")]
    [InlineData("ja", "en-US", "ja-JP")]
    [InlineData("en", "ja-JP", "en-US")]
    [InlineData("", "ja-JP", "ja-JP")]
    [InlineData("unknown", "ja-JP", "ja-JP")]
    public void SelectResolvesManualAndSystemPreferences(string? preference, string? systemLanguage, string expected)
        => Assert.Equal(expected, AppLanguage.Select(preference, systemLanguage).Name);

    [Theory]
    [InlineData("en-US", "Usage: not reported by the provider")]
    [InlineData("fr-FR", "Usage: not reported by the provider")]
    [InlineData("ja-JP", "使用量: 未取得（プロバイダーから報告されていません）")]
    public void EmbeddedResourcesUseJapaneseOrEnglishFallback(string culture, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, CoreText.Get("UsageMissing"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task MissingPreferenceDefaultsToSystemWithoutTouchingSettings()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(temp.Path);
        await settingsStore.SaveAsync(new BridgeSettings { WorkspaceRoot = "C:/workspace" });
        var settingsBefore = await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"));
        var store = new LanguagePreferencesStore(settingsStore);

        Assert.Equal(new LanguagePreferences("system"), await store.LoadAsync());
        Assert.False(File.Exists(Path.Combine(temp.Path, "language.json")));
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Language\":\"unsupported\"}")]
    public async Task CorruptOrUnknownPreferenceFallsBackAndPreservesFile(string contents)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "language.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new LanguagePreferencesStore(new SettingsStore(temp.Path));

        Assert.Equal(new LanguagePreferences("system"), await store.LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("ja")]
    [InlineData("en")]
    public async Task SaveAndLoadRoundTrip(string language)
    {
        using var temp = new TempDirectory();
        var store = new LanguagePreferencesStore(new SettingsStore(temp.Path));
        var expected = new LanguagePreferences(language);

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
        Assert.True(File.Exists(Path.Combine(temp.Path, "language.json")));
    }

    [Fact]
    public async Task SaveRejectsUnknownPreferenceWithoutChangingExistingFile()
    {
        using var temp = new TempDirectory();
        var store = new LanguagePreferencesStore(new SettingsStore(temp.Path));
        await store.SaveAsync(new LanguagePreferences("ja"));
        var before = await File.ReadAllTextAsync(Path.Combine(temp.Path, "language.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new LanguagePreferences("fr")));

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(temp.Path, "language.json")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-language-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
