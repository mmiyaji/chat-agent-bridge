using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core.History;

internal static class HistoryFileStore
{
    internal const int SchemaVersion = 1;
    internal const int MaxRecords = 500;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(PathComparer);
    private static readonly ConcurrentDictionary<Guid, byte> LiveExecutions = new();
    private static readonly ConcurrentDictionary<string, CachedDocument> DocumentCache = new(PathComparer);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly record struct FileStamp(long LastWriteTicks, long Length);

    private sealed record CachedDocument(FileStamp Stamp, object Document);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static SemaphoreSlim LockFor(string path) => FileLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    internal static bool IsLive(Guid id) => LiveExecutions.ContainsKey(id);

    internal static void EndTracking(Guid id) => LiveExecutions.TryRemove(id, out _);

    internal static void SetLive(ExecutionRecord record)
    {
        if (record.Status == "running") LiveExecutions[record.Id] = 0;
        else LiveExecutions.TryRemove(record.Id, out _);
    }

    internal static async Task<TFile> ReadAsync<TFile>(string path, Func<TFile> empty, CancellationToken ct)
    {
        if (!File.Exists(path)) return empty();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TFile>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("History file is empty or invalid.");
        }
        catch (JsonException ex) { throw new InvalidDataException("History file contains invalid JSON or unsupported fields.", ex); }
    }

    /// <summary>
    /// Reads and validates a document once, then serves it from memory until the file's last-write time or length changes.
    /// Call only while holding <see cref="LockFor"/> for the path. The returned instance is shared with the cache:
    /// treat it as read-only unless the caller rewrites it with <see cref="WriteAtomicAsync{TFile}"/> or calls <see cref="Invalidate"/> on failure.
    /// </summary>
    internal static async Task<TFile> ReadCachedAsync<TFile>(string path, Func<TFile> empty, Action<TFile> validate, CancellationToken ct)
        where TFile : class
    {
        var key = Path.GetFullPath(path);
        var stamp = StampOf(key);
        if (DocumentCache.TryGetValue(key, out var cached) && cached.Stamp == stamp && cached.Document is TFile hit) return hit;
        DocumentCache.TryRemove(key, out _);
        var document = await ReadAsync(key, empty, ct).ConfigureAwait(false);
        validate(document);
        // The stamp was taken before the read, so a concurrent external write makes the next call read again.
        DocumentCache[key] = new CachedDocument(stamp, document);
        return document;
    }

    internal static void Invalidate(string path) => DocumentCache.TryRemove(Path.GetFullPath(path), out _);

    private static FileStamp StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.LastWriteTimeUtc.Ticks, info.Length) : new FileStamp(-1, -1);
    }

    internal static async Task WriteAtomicAsync<TFile>(string path, TFile value, CancellationToken ct, bool indented = true)
        where TFile : class
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"history.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, indented ? JsonOptions : CompactJsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, path, ct).ConfigureAwait(false);
            // The caller must not mutate value afterwards; it becomes the cached view of the new file.
            var key = Path.GetFullPath(path);
            DocumentCache[key] = new CachedDocument(StampOf(key), value);
        }
        catch
        {
            Invalidate(path);
            throw;
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    internal static void CheckSchema(int? schemaVersion)
    {
        if (schemaVersion is null)
            throw new InvalidDataException("History file is missing its schema version.");
        if (schemaVersion.Value != SchemaVersion)
            throw new InvalidDataException($"History schema version {schemaVersion.Value} is unsupported; expected {SchemaVersion}.");
    }

    internal static void ValidateText(string? value, string field, int maxLength, bool allowEmpty = true)
    {
        if (value is null)
        {
            if (allowEmpty) return;
            throw new ArgumentException($"{field} is required.", field);
        }
        if (value.Length > maxLength || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.IndexOf('\0') >= 0)
            throw new ArgumentException($"{field} is invalid or exceeds {maxLength} characters.", field);
    }

    internal static void ValidateSource(string source)
    {
        if (source is not ("local" or "slack" or "mattermost" or "discord"))
            throw new ArgumentException("Source must be local, slack, mattermost, or discord.", nameof(source));
    }

    internal static IReadOnlyList<T> ValidateLoadedRecords<T>(IReadOnlyList<T>? records) where T : class
    {
        if (records is null || records.Count > MaxRecords)
            throw new InvalidDataException($"History file must contain at most {MaxRecords} records.");
        if (records.Any(static record => record is null))
            throw new InvalidDataException("History file contains a null record.");
        return records;
    }
}
