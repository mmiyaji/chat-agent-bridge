using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

/// <summary>Atomically persists validated retention preferences in the application data directory.</summary>
public sealed class LogRetentionPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _directory;
    private readonly string _filePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public LogRetentionPreferencesStore(string? directory = null, TimeProvider? timeProvider = null)
    {
        _directory = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(_directory, "log-retention.json");
    }

    public string DirectoryPath => _directory;

    public Task<LogRetentionPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    private async Task<LogRetentionPreferences> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = Gates.GetOrAdd(_filePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LogRetentionPreferences preferences;
            FileStream stream;
            try
            {
                stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (Cache.TryGetValue(_filePath, out var previous) && previous.Stamp.Ticks >= 0)
                    throw new IOException("Previously loaded log retention preferences are no longer available.");
                if (!IsDataPathAvailable())
                    throw new IOException("The application data path is unavailable; log retention could not be determined.", ex);
                preferences = new LogRetentionPreferences();
                Cache[_filePath] = new(new(-1, -1), preferences);
                return preferences;
            }
            await using (stream.ConfigureAwait(false))
            {
                var openedStamp = Stamp();
                if (Cache.TryGetValue(_filePath, out var cached) && cached.Stamp == openedStamp) return cached.Preferences;
                if (stream.Length > 16 * 1024) throw new InvalidDataException("Log retention preferences exceed the supported size.");
                try
                {
                    var document = await JsonSerializer.DeserializeAsync<StoredDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (document?.Preferences is null || document.SchemaVersion != CurrentSchemaVersion)
                        throw new InvalidDataException("Log retention preferences are empty, invalid, or unsupported.");
                    preferences = document.Preferences;
                    preferences.Validate();
                }
                catch (JsonException ex) { throw new InvalidDataException("Log retention preferences contain invalid or unsupported JSON.", ex); }
                catch (ArgumentException ex) { throw new InvalidDataException("Log retention preferences contain invalid values.", ex); }
                var completedStamp = Stamp();
                if (completedStamp != openedStamp) throw new IOException("Log retention preferences changed while they were being read.");
                Cache[_filePath] = new(completedStamp, preferences);
            }
            return preferences;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(LogRetentionPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        var gate = Gates.GetOrAdd(_filePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredDocument { SchemaVersion = CurrentSchemaVersion, Preferences = preferences }, JsonOptions);
            var tempPath = Path.Combine(_directory, $"log-retention.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
            Cache[_filePath] = new(Stamp(), preferences);
        }
        finally { gate.Release(); }
    }

    private FileStamp Stamp()
    {
        var info = new FileInfo(_filePath);
        info.Refresh();
        if (!info.Exists) throw new IOException("Retention settings metadata is unavailable.");
        return new(info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private bool IsDataPathAvailable()
    {
        var current = _directory;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                return (attributes & FileAttributes.Directory) != 0;
            }
            catch (DirectoryNotFoundException) { current = Directory.GetParent(current)?.FullName; }
            catch (FileNotFoundException) { current = Directory.GetParent(current)?.FullName; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
        return false;
    }
    private readonly record struct FileStamp(long Ticks, long Length);
    private sealed record CacheEntry(FileStamp Stamp, LogRetentionPreferences Preferences);
    private sealed class StoredDocument
    {
        public int? SchemaVersion { get; set; }
        public LogRetentionPreferences? Preferences { get; set; }
    }
}
