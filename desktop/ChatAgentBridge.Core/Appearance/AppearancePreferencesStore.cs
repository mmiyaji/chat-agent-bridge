using System.Text.Json;

namespace ChatAgentBridge.Core.Appearance;

public sealed record AppearancePreferences(string Theme = "system");

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
            return IsSupported(stored?.Theme) ? stored! : new AppearancePreferences();
        }
        catch (JsonException) { return new AppearancePreferences(); }
        catch (InvalidDataException) { return new AppearancePreferences(); }
    }

    public async Task SaveAsync(AppearancePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsSupported(preferences.Theme))
            throw new ArgumentException("Theme must be system, light, or dark.", nameof(preferences));
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
