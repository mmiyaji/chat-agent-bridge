using System.Text.Json;

namespace ChatAgentBridge.Core.Localization;

public sealed record LanguagePreferences(string Language = "system");

public sealed class LanguagePreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public LanguagePreferencesStore(SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        DirectoryPath = settingsStore.DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "language.json");
    }

    public string DirectoryPath { get; }

    public async Task<LanguagePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new LanguagePreferences();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<LanguagePreferences>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return IsSupported(stored?.Language) ? stored! : new LanguagePreferences();
        }
        catch (JsonException) { return new LanguagePreferences(); }
        catch (InvalidDataException) { return new LanguagePreferences(); }
    }

    public async Task SaveAsync(LanguagePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsSupported(preferences.Language))
            throw new ArgumentException("Language preference must be system, ja, or en.", nameof(preferences));
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"language.{Guid.NewGuid():N}.tmp");
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

    private static bool IsSupported(string? value) => value is "system" or "ja" or "en";
}
