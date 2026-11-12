using System.Text.Json;

namespace ChatAgentBridge.Core.Appearance;

/// <summary>Theme is system, light or dark; Accent is "system" (Windows accent) or an <see cref="AccentPalette"/> id.</summary>
public sealed record AppearancePreferences(string Theme = "system", string Accent = AccentPalette.System);

public sealed class AppearancePreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public AppearancePreferencesStore(SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        DirectoryPath = settingsStore.DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "appearance.json");
    }

    public string DirectoryPath { get; }

    public async Task<AppearancePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new AppearancePreferences();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<AppearancePreferences>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) return new AppearancePreferences();
            // An unknown theme or accent (older or edited file) falls back on its own without losing the other value.
            return new AppearancePreferences(IsSupported(stored.Theme) ? stored.Theme : "system",
                AccentPalette.IsSupported(stored.Accent) ? stored.Accent : AccentPalette.System);
        }
        catch (JsonException) { return new AppearancePreferences(); }
        catch (InvalidDataException) { return new AppearancePreferences(); }
    }

    public async Task SaveAsync(AppearancePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsSupported(preferences.Theme))
            throw new ArgumentException("Theme must be system, light, or dark.", nameof(preferences));
        if (!AccentPalette.IsSupported(preferences.Accent))
            throw new ArgumentException("Accent must be system or a supported accent color.", nameof(preferences));
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"appearance.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static bool IsSupported(string? value) => value is "system" or "light" or "dark";
}
