using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core.History;

/// <summary>
/// Append-only, bounded audit segments. Retention removes whole segments, so a boundary segment may
/// include older events. Segments roll at most every 30 days (or the shorter retention period).
/// All operations share the legacy audit path's lock.
/// </summary>
public sealed class AuditStore
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "received", "approved", "rejected", "started", "completed", "failed", "cancelled", "reply_sent",
        "reply_failed", "detached", "workspace_changed", "model_changed", "agent_changed",
        "attachment_approved", "attachment_sent", "attachment_failed", "follow_up_applied", "follow_up_uncertain"
    };
    private const int MaxEntryBytes = 16 * 1024;
    private const int MaxSegmentBytes = 16 * 1024 * 1024;
    private static readonly byte[] Newline = [(byte)'\n'];
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly ConcurrentDictionary<string, State> States = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Regex SegmentName = new("^audit-segment-([0-9]{20})\\.jsonl\\z", RegexOptions.CultureInvariant);
    internal static readonly Regex OwnedFileName = new("^(?:audit-manifest\\.json|audit-segment-[0-9]{20}\\.jsonl|audit-(?:manifest|segment-[0-9]{20})\\.[0-9a-fA-F]{32}\\.tmp)\\z", RegexOptions.CultureInvariant);
    private readonly string _directory;
    private readonly string _filePath;
    private string ManifestPath => Path.Combine(_directory, "audit-manifest.json");

    public AuditStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(_directory, "audit.json");
    }

    public Task AppendAsync(AuditEvent record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AppendManyAsync([record], cancellationToken);
    }

    public async Task AppendManyAsync(IEnumerable<AuditEvent> recordsToAppend, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordsToAppend);
        var batch = recordsToAppend.Select(record =>
        {
            ArgumentNullException.ThrowIfNull(record);
            Validate(record);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            if (bytes.Length + 1 > MaxEntryBytes) throw new ArgumentException("Audit event exceeds the entry size limit.", nameof(recordsToAppend));
            return (record, bytes);
        }).ToArray();
        if (batch.Length == 0) return;
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafe(_directory);
            var policy = await new LogRetentionPreferencesStore(_directory).LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = await GetStateAsync(policy, false, cancellationToken).ConfigureAwait(false);
            var limit = policy.AuditSegmentSizeMb * 1024 * 1024;
            var batchIndex = 0;
            while (batchIndex < batch.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = state.Segments.LastOrDefault();
                if (active is null || active.Length + batch[batchIndex].bytes.Length + 1 > limit || ShouldAgeRoll(active, policy))
                {
                    active = await RotateAsync(state, cancellationToken).ConfigureAwait(false);
                    await PruneAsync(state, policy, cancellationToken).ConfigureAwait(false);
                }
                EnsureSafe(active.Path);
                await using (var stream = new FileStream(active.Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    // One durable flush per segment in this batch, with each completed line independently recoverable.
                    while (batchIndex < batch.Length && active.Length + batch[batchIndex].bytes.Length + 1 <= limit)
                    {
                        var (record, bytes) = batch[batchIndex++];
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
                        active.Length += bytes.Length + 1;
                        state.TotalBytes += bytes.Length + 1;
                        active.Latest = record.Timestamp > active.Latest ? record.Timestamp : active.Latest;
                        active.Earliest = record.Timestamp < active.Earliest ? record.Timestamp : active.Earliest;
                        active.Recent.Add(record);
                        state.Recent.Add(record);
                    }
                    stream.Flush(true);
                }
                active.Recent = Latest(active.Recent);
                state.Recent = Latest(state.Recent);
                active.Stamp = state.ActiveStamp = StampOf(active.Path);
                if (state.TotalBytes > (long)limit * policy.AuditMaxSegments)
                    await PruneAsync(state, policy, cancellationToken).ConfigureAwait(false);
            }
        }
        catch { States.TryRemove(_filePath, out _); throw; }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(CancellationToken cancellationToken = default)
    {
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafe(_directory);
            var policy = await new LogRetentionPreferencesStore(_directory).LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = await GetStateAsync(policy, false, cancellationToken, checkInventory: true).ConfigureAwait(false);
            return state.Recent.ToArray();
        }
        catch { States.TryRemove(_filePath, out _); throw; }
        finally { gate.Release(); }
    }

    /// <summary>Reloads the saved policy and removes closed segments outside its age/count bounds.</summary>
    public async Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafe(_directory);
            // Load before any recovery/migration/deletion: invalid preferences must never select defaults for pruning.
            var policy = await new LogRetentionPreferencesStore(_directory).LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = await GetStateAsync(policy, true, cancellationToken).ConfigureAwait(false);
            var active = state.Segments.LastOrDefault();
            if (active is not null && (ShouldAgeRoll(active, policy) || active.Length > policy.AuditSegmentSizeMb * 1024 * 1024))
                await RotateAsync(state, cancellationToken).ConfigureAwait(false);
            await PruneAsync(state, policy, cancellationToken).ConfigureAwait(false);
        }
        catch { States.TryRemove(_filePath, out _); throw; }
        finally { gate.Release(); }
    }

    private async Task<State> GetStateAsync(LogRetentionPreferences policy, bool refresh, CancellationToken ct, bool checkInventory = false)
    {
        EnsureSafe(ManifestPath);
        EnsureSafe(_filePath);
        if (!refresh && !File.Exists(_filePath) && States.TryGetValue(_filePath, out var hit) &&
            hit.ManifestStamp == StampOf(ManifestPath) &&
            (hit.Segments.Count == 0 || hit.ActiveStamp == StampOf(hit.Segments[^1].Path)) &&
            (!checkInventory || InventoryMatches(hit)))
            return hit;
        Directory.CreateDirectory(_directory);
        await InitializeAsync(policy, ct).ConfigureAwait(false);
        var paths = SegmentPaths();
        var state = new State { ManifestStamp = StampOf(ManifestPath) };
        var repairs = new List<(string Path, long Length)>();
        for (var index = 0; index < paths.Length; index++)
        {
            var (segment, records, completeLength) = await ReadSegmentAsync(paths[index], index == paths.Length - 1, ct).ConfigureAwait(false);
            state.Segments.Add(segment);
            segment.Recent = Latest(records);
            state.Recent.AddRange(records);
            // Memory stays bounded even for long retention.
            state.Recent = state.Recent.OrderByDescending(record => record.Timestamp).Take(HistoryFileStore.MaxRecords).ToList();
            if (completeLength != segment.Length) repairs.Add((segment.Path, completeLength));
            segment.Length = completeLength;
            state.TotalBytes += completeLength;
        }
        // Validate every committed line before truncating any crash tail, preserving evidence on corruption.
        foreach (var (path, length) in repairs)
        {
            EnsureSafe(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.SetLength(length);
            stream.Flush(true);
        }
        state.NextNumber = state.Segments.Count == 0 ? 1 : checked(state.Segments[^1].Number + 1);
        foreach (var segment in state.Segments) segment.Stamp = StampOf(segment.Path);
        state.ActiveStamp = state.Segments.Count == 0 ? default : StampOf(state.Segments[^1].Path);
        States[_filePath] = state;
        return state;
    }

    private async Task InitializeAsync(LogRetentionPreferences policy, CancellationToken ct)
    {
        if (File.Exists(ManifestPath))
        {
            var manifest = await ReadManifestAsync(ct).ConfigureAwait(false);
            if (File.Exists(_filePath))
            {
                if (new FileInfo(_filePath).Length > (long)HistoryFileStore.MaxRecords * MaxEntryBytes)
                    throw new InvalidDataException("Legacy audit file exceeds the supported size.");
                var source = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(source));
                if (manifest.LegacyHash != hash) throw new InvalidDataException("Legacy audit data changed after migration; it has been preserved.");
                // A crash after committing the marker but before removing the source is safe to resume.
                // The source is still the recovery authority: every imported byte must be intact.
                // Normal active-tail recovery must not truncate an incomplete imported event here.
                var expected = LegacyChunks(source);
                var paths = SegmentPaths();
                if (manifest.MigrationSegments != expected.Count || paths.Length != expected.Count)
                    throw new InvalidDataException("Migrated audit segment inventory differs from its source; data has been preserved.");
                for (var i = 0; i < paths.Length; i++)
                {
                    if (paths[i] != SegmentPath(i + 1) || new FileInfo(paths[i]).Length != expected[i].Length ||
                        !(await File.ReadAllBytesAsync(paths[i], ct).ConfigureAwait(false)).AsSpan().SequenceEqual(expected[i]))
                        throw new InvalidDataException("Migrated audit segment differs from its source; data has been preserved.");
                }
                EnsureSafe(_filePath);
                File.Delete(_filePath);
            }
            return;
        }
        var existing = SegmentPaths();
        if (!File.Exists(_filePath))
        {
            if (existing.Length != 0) throw new InvalidDataException("Audit segments have no committed manifest; they have been preserved.");
            await WriteAtomicAsync(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(new Manifest { SchemaVersion = 1 }, JsonOptions), ct).ConfigureAwait(false);
            return;
        }
        if (new FileInfo(_filePath).Length > (long)HistoryFileStore.MaxRecords * MaxEntryBytes)
            throw new InvalidDataException("Legacy audit file exceeds the supported size.");
        var legacyBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
        var chunks = LegacyChunks(legacyBytes);
        // Deterministic names and content let an interrupted import resume before its single commit marker.
        if (existing.Length > chunks.Count) throw new InvalidDataException("Unexpected audit segments during migration; data has been preserved.");
        for (var index = 0; index < chunks.Count; index++)
        {
            var path = SegmentPath(index + 1);
            EnsureSafe(path);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length != chunks[index].Length ||
                    !(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(chunks[index]))
                    throw new InvalidDataException("Interrupted audit migration differs from its source; data has been preserved.");
            }
            else await WriteAtomicAsync(path, chunks[index], ct).ConfigureAwait(false);
        }
        if (existing.Any(path => !Enumerable.Range(1, chunks.Count).Any(number => path == SegmentPath(number))))
            throw new InvalidDataException("Unexpected audit segment during migration.");
        await WriteAtomicAsync(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(new Manifest
        {
            SchemaVersion = 1, LegacyHash = Convert.ToHexString(SHA256.HashData(legacyBytes)), MigrationSegments = chunks.Count
        }, JsonOptions), ct).ConfigureAwait(false);
        EnsureSafe(_filePath);
        File.Delete(_filePath);
    }

    private static List<byte[]> LegacyChunks(byte[] legacyBytes)
    {
        AuditDocument legacy;
        try { legacy = JsonSerializer.Deserialize<AuditDocument>(legacyBytes, JsonOptions) ?? throw new InvalidDataException("History file is empty or invalid."); }
        catch (JsonException ex) { throw new InvalidDataException("History file contains invalid JSON or unsupported fields.", ex); }
        HistoryFileStore.CheckSchema(legacy.SchemaVersion);
        var records = HistoryFileStore.ValidateLoadedRecords(legacy.Records);
        foreach (var record in records) ValidateLoaded(record);
        var chunks = new List<byte[]>();
        using (var chunk = new MemoryStream())
        {
            foreach (var record in records)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                if (bytes.Length + 1 > MaxEntryBytes) throw new InvalidDataException("Audit event exceeds the entry size limit.");
                // Fixed migration chunking remains resumable even when preferences change after a crash.
                if (chunk.Length != 0 && chunk.Length + bytes.Length + 1 > 1024 * 1024)
                {
                    chunks.Add(chunk.ToArray()); chunk.SetLength(0);
                }
                chunk.Write(bytes); chunk.WriteByte((byte)'\n');
            }
            if (chunk.Length != 0) chunks.Add(chunk.ToArray());
        }
        return chunks;
    }

    private async Task<Manifest> ReadManifestAsync(CancellationToken ct)
    {
        if (new FileInfo(ManifestPath).Length > 4096) throw new InvalidDataException("Audit manifest exceeds the supported size.");
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(ManifestPath, ct).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Audit manifest is empty.");
            HistoryFileStore.CheckSchema(manifest.SchemaVersion);
            if (manifest.MigrationSegments is < 0 or > HistoryFileStore.MaxRecords ||
                (manifest.LegacyHash is null && manifest.MigrationSegments != 0) ||
                (manifest.LegacyHash is not null && !Regex.IsMatch(manifest.LegacyHash, "^[0-9A-F]{64}\\z", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Audit manifest is invalid.");
            return manifest;
        }
        catch (JsonException ex) { throw new InvalidDataException("Audit manifest contains invalid JSON.", ex); }
    }

    private async Task<(Segment Segment, List<AuditEvent> Records, long CompleteLength)> ReadSegmentAsync(string path, bool active, CancellationToken ct)
    {
        EnsureSafe(path);
        var info = new FileInfo(path);
        if (info.Length > MaxSegmentBytes) throw new InvalidDataException("Audit segment exceeds the size limit.");
        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var records = new List<AuditEvent>();
        var start = 0;
        var latest = DateTimeOffset.MinValue;
        var earliest = DateTimeOffset.MaxValue;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] != (byte)'\n') continue;
            if (index - start + 1 > MaxEntryBytes) throw new InvalidDataException("Audit event exceeds the entry size limit.");
            AuditEvent? record;
            try { record = JsonSerializer.Deserialize<AuditEvent>(data.AsSpan(start, index - start), JsonOptions); }
            catch (JsonException ex) { throw new InvalidDataException("Audit segment contains an invalid committed line.", ex); }
            ValidateLoaded(record);
            records.Add(record!);
            if (record!.Timestamp > latest) latest = record.Timestamp;
            if (record.Timestamp < earliest) earliest = record.Timestamp;
            start = index + 1;
        }
        if (start != data.Length && !active) throw new InvalidDataException("Closed audit segment has an incomplete line.");
        if (!long.TryParse(SegmentName.Match(Path.GetFileName(path)).Groups[1].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1 || number == long.MaxValue)
            throw new InvalidDataException("Audit segment number is invalid.");
        return (new Segment(number, path, data.Length, latest) { Earliest = earliest }, records, start);
    }

    private async Task PruneAsync(State state, LogRetentionPreferences policy, CancellationToken ct)
    {
        // A rotation may use cached metadata. Revalidate externally changed closed files before
        // making an age decision, so a newly added event can never be erased using a stale maximum.
        for (var index = 0; index < state.Segments.Count - 1; index++)
        {
            var previous = state.Segments[index];
            EnsureSafe(previous.Path);
            if (previous.Stamp == StampOf(previous.Path)) continue;
            var (updated, records, _) = await ReadSegmentAsync(previous.Path, false, ct).ConfigureAwait(false);
            updated.Recent = Latest(records);
            updated.Stamp = StampOf(updated.Path);
            state.TotalBytes += updated.Length - previous.Length;
            state.Segments[index] = updated;
            state.Recent = Latest(state.Segments.SelectMany(segment => segment.Recent));
        }
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.AuditRetentionDays);
        var active = state.Segments.LastOrDefault();
        var expired = state.Segments.Where(segment => segment != active && segment.Latest < cutoff).ToHashSet();
        var remainingBytes = state.TotalBytes - expired.Sum(segment => segment.Length);
        var capacity = (long)policy.AuditSegmentSizeMb * 1024 * 1024 * policy.AuditMaxSegments;
        foreach (var segment in state.Segments.Where(segment => segment != active && !expired.Contains(segment)))
        {
            if (state.Segments.Count - expired.Count <= policy.AuditMaxSegments && remainingBytes <= capacity) break;
            expired.Add(segment);
            remainingBytes -= segment.Length;
        }
        // Refuse links for all targets before deleting any target.
        foreach (var segment in expired) EnsureSafe(segment.Path);
        foreach (var segment in expired)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSafe(segment.Path);
            File.Delete(segment.Path);
            state.Segments.Remove(segment);
            state.TotalBytes -= segment.Length;
        }
        if (expired.Count != 0) state.Recent = Latest(state.Segments.SelectMany(segment => segment.Recent));
    }

    private async Task<Segment> RotateAsync(State state, CancellationToken ct)
    {
        var number = state.NextNumber;
        if (number == long.MaxValue) throw new InvalidDataException("Audit segment number limit was reached.");
        var path = SegmentPath(number);
        EnsureSafe(path);
        await using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            created.Flush(true);
        var segment = new Segment(number, path, 0, DateTimeOffset.MinValue) { Stamp = StampOf(path) };
        state.Segments.Add(segment);
        state.NextNumber = checked(number + 1);
        state.ActiveStamp = segment.Stamp;
        return segment;
    }

    private static bool ShouldAgeRoll(Segment segment, LogRetentionPreferences policy) =>
        segment.Length != 0 && segment.Earliest < DateTimeOffset.UtcNow.AddDays(-Math.Min(30, policy.AuditRetentionDays));

    private bool InventoryMatches(State state)
    {
        var paths = SegmentPaths();
        if (paths.Length != state.Segments.Count) return false;
        for (var index = 0; index < paths.Length; index++)
            if (!StringComparer.Ordinal.Equals(paths[index], state.Segments[index].Path) ||
                StampOf(paths[index]) != state.Segments[index].Stamp) return false;
        return true;
    }

    private static List<AuditEvent> Latest(IEnumerable<AuditEvent> records) =>
        records.OrderByDescending(record => record.Timestamp).Take(HistoryFileStore.MaxRecords).ToList();

    private string[] SegmentPaths()
    {
        EnsureSafe(_directory);
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "audit-segment-*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(path => SegmentName.IsMatch(Path.GetFileName(path))).Select(path => { EnsureSafe(path); return path; })
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private string SegmentPath(long number) => Path.Combine(_directory, $"audit-segment-{number:D20}.jsonl");

    private async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        EnsureSafe(path);
        var temp = Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp");
        EnsureSafe(temp);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            EnsureSafe(path);
            await AtomicFile.ReplaceAsync(temp, path, ct).ConfigureAwait(false);
        }
        finally { EnsureSafe(temp); if (File.Exists(temp)) File.Delete(temp); }
    }

    private void EnsureSafe(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(_directory, comparison) && !full.StartsWith(Path.TrimEndingDirectorySeparator(_directory) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Audit path escaped its directory.");
        for (FileSystemInfo? item = new FileInfo(full); item is not null; item = Directory.GetParent(item.FullName))
        {
            try
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked audit locations cannot be managed here.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static (long Ticks, long Length) StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (-1, -1);
    }

    private static void Validate(AuditEvent record)
    {
        if (record.Timestamp == default) throw new ArgumentException("Audit timestamp is required.", nameof(record));
        if (!AllowedKinds.Contains(record.Kind)) throw new ArgumentException("Audit kind is not allowlisted.", nameof(record));
        HistoryFileStore.ValidateSource(record.Source);
        HistoryFileStore.ValidateText(record.ChannelId, nameof(record.ChannelId), 256);
        HistoryFileStore.ValidateText(record.ThreadId, nameof(record.ThreadId), 256);
    }

    private static void ValidateLoaded(AuditEvent? record)
    {
        if (record is null) throw new InvalidDataException("History file contains a null record.");
        try { Validate(record); }
        catch (ArgumentException ex) { throw new InvalidDataException("History file contains an invalid audit event.", ex); }
    }

    private sealed class AuditDocument
    {
        public int? SchemaVersion { get; set; }
        public List<AuditEvent>? Records { get; set; }
    }
    private sealed class Manifest
    {
        public int? SchemaVersion { get; set; }
        public string? LegacyHash { get; set; }
        public int MigrationSegments { get; set; }
    }
    private sealed class Segment(long number, string path, long length, DateTimeOffset latest)
    {
        public long Number { get; } = number;
        public string Path { get; } = path;
        public long Length { get; set; } = length;
        public DateTimeOffset Latest { get; set; } = latest;
        public DateTimeOffset Earliest { get; set; } = DateTimeOffset.MaxValue;
        public (long Ticks, long Length) Stamp { get; set; }
        public List<AuditEvent> Recent = [];
    }
    private sealed class State
    {
        public (long Ticks, long Length) ManifestStamp;
        public (long Ticks, long Length) ActiveStamp;
        public long NextNumber = 1;
        public long TotalBytes;
        public List<Segment> Segments { get; } = [];
        public List<AuditEvent> Recent = [];
    }
}
