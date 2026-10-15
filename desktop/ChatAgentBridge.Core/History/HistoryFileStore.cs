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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

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

    internal static async Task WriteAtomicAsync<TFile>(string path, TFile value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"history.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
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
        if (source is not ("local" or "slack" or "mattermost"))
            throw new ArgumentException("Source must be local, slack, or mattermost.", nameof(source));
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
