using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Updates;

/// <summary>Preferences and the last portable release candidate for the current installation.</summary>
public sealed record PortableUpdatePreferences(
    int SchemaVersion,
    bool AutomaticChecksEnabled,
    bool IncludePrereleases,
    DateTimeOffset? LastAttemptUtc,
    PortableRelease? AvailableRelease)
{
    public const int CurrentSchemaVersion = 1;

    public static PortableUpdatePreferences CreateDefault(bool includePrereleases) =>
        new(CurrentSchemaVersion, true, includePrereleases, null, null);
}

/// <summary>Stores portable updater settings separately from the main application settings.</summary>
public sealed class PortableUpdatePreferencesStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "portable-update.json");

    public async Task<PortableUpdatePreferences?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        var info = new FileInfo(_path);
        if (info.Length > 256 * 1024) throw new InvalidDataException("Portable update preferences exceed the size limit.");

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var value = await JsonSerializer.DeserializeAsync<PortableUpdatePreferences>(stream, JsonOptions, cancellationToken);
        return value?.SchemaVersion == PortableUpdatePreferences.CurrentSchemaVersion ? value : null;
    }

    public async Task SaveAsync(PortableUpdatePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            await AtomicFile.ReplaceAsync(temp, _path, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Shared scheduling rule for six-hour portable update checks.</summary>
public static class PortableUpdateCheckPolicy
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(6);

    public static bool IsDue(DateTimeOffset? lastAttemptUtc, DateTimeOffset nowUtc) =>
        lastAttemptUtc is null || nowUtc - lastAttemptUtc.Value >= MinimumInterval;
}
