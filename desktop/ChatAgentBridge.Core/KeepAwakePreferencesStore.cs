using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

public sealed class KeepAwakePreferencesStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _path = Path.Combine(Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath), "keep-awake.json");

    public async Task<KeepAwakePreferences> LoadAsync(CancellationToken token = default)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > 16 * 1024) throw new InvalidDataException("Keep-awake preferences exceed the supported size.");
            var doc = await JsonSerializer.DeserializeAsync<Document>(stream, Options, token).ConfigureAwait(false);
            if (doc?.SchemaVersion != 1 || doc.Preferences is null) throw new InvalidDataException("Keep-awake preferences are invalid.");
            return doc.Preferences.Validate();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException ex) { throw new InvalidDataException("Keep-awake preferences contain invalid JSON.", ex); }
    }

    public async Task SaveAsync(KeepAwakePreferences preferences, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences = preferences.Validate();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"keep-awake.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document { SchemaVersion = 1, Preferences = preferences }, Options, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await AtomicFile.ReplaceAsync(temp, _path, token).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed class Document { public int? SchemaVersion { get; set; } public KeepAwakePreferences? Preferences { get; set; } }
}
