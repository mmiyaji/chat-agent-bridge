using System.Text.Json;

namespace ChatAgentBridge.Core;

public sealed record BackgroundPreferences(bool CloseToTray = false);

/// <summary>Local window behavior, independent of agent credentials and chat settings.</summary>
public sealed class BackgroundPreferencesStore(SettingsStore settingsStore)
{
    private readonly string _path = Path.Combine(settingsStore.DirectoryPath, "background.json");

    public async Task<BackgroundPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return new();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<BackgroundPreferences>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public async Task SaveAsync(BackgroundPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(settingsStore.DirectoryPath);
        var temp = Path.Combine(settingsStore.DirectoryPath, $"background.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(temp, _path, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
