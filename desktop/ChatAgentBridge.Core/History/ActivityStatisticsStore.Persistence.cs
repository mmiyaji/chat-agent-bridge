using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core.History;

public sealed partial class ActivityStatisticsStore
{
    internal const int MaxJournalEvents = 128;
    private const int MaxJournalBytes = 256 * 1024;
    private const int MaxDeltaBytes = 32 * 1024;
    private static readonly JsonSerializerOptions DeltaJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly ConcurrentDictionary<string, MergedStatistics> MergedDocuments = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly record struct StatisticsStamp(long Ticks, long Length);
    private sealed record MergedStatistics(StatisticsStamp Snapshot, StatisticsStamp Journal,
        StatisticsDocument Document, int Events, int CommittedJournalBytes);

    // Only the fields needed for day/model aggregates are durable here. Never store
    // prompts, results, workspace paths, channel IDs, user IDs or agent session IDs.
    private sealed record StatisticsDelta(int SchemaVersion, string? Generation, Guid Id,
        DateTimeOffset StartedAt, string Status, string? Model, AgentUsage? Usage)
    {
        public ExecutionRecord ToRecord() => new()
        {
            Id = Id, StartedAt = StartedAt, Status = Status, AgentModel = Model, Usage = Usage
        };
    }

    private static bool ApplyRecord(StatisticsDocument document, ExecutionRecord record)
    {
        if (!document.MigrationCompleted)
        {
            document.MigrationCompleted = true;
            document.TimeZoneId = TimeZoneInfo.Local.Id;
        }
        if (record.Status == "running")
        {
            if (document.FinalizedIds.Any(marker => marker.Id == record.Id)) return false;
            var active = document.Active.FirstOrDefault(marker => marker.Id == record.Id);
            if (active is not null)
            {
                active.StartedAt = record.StartedAt;
                active.Day = LocalDay(record.StartedAt);
                active.Model = ActivityModel(record);
            }
            else AddActive(document, record);
        }
        else
        {
            var active = document.Active.FirstOrDefault(marker => marker.Id == record.Id);
            if (active is null && document.FinalizedIds.Any(marker => marker.Id == record.Id)) return false;
            if (active is null && document.CutoffStartedAt is { } cutoff && record.StartedAt <= cutoff) return false;
            if (active is not null) document.Active.Remove(active);
            AddFinalized(document, record);
        }
        Prune(document);
        return true;
    }

    private static StatisticsStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(info.LastWriteTimeUtc.Ticks, info.Length) : new(-1, -1);
    }

    // Caller holds the existing per-statistics-path lock. This cache owns the working
    // document; a second generic snapshot cache would only double retained memory.
    private async Task<StatisticsDocument> ReadMergedDocumentAsync(CancellationToken ct)
    {
        var snapshotStamp = Stamp(_path);
        var journalStamp = Stamp(_journalPath);
        if (snapshotStamp.Length > 64L * 1024 * 1024 || journalStamp.Length > MaxJournalBytes)
            throw new InvalidDataException("Activity statistics file exceeds its size limit.");
        if (MergedDocuments.TryGetValue(_path, out var cached) &&
            cached.Snapshot == snapshotStamp && cached.Journal == journalStamp)
            return cached.Document;

        MergedDocuments.TryRemove(_path, out _);
        var document = await HistoryFileStore.ReadAsync(_path, EmptyDocument, ct).ConfigureAwait(false);
        ValidateDocument(document);
        document.JournalGeneration = CheckpointGeneration(document);
        var events = 0;
        var committedBytes = 0;
        if (journalStamp.Length > 0)
        {
            var bytes = await File.ReadAllBytesAsync(_journalPath, ct).ConfigureAwait(false);
            var begin = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                ct.ThrowIfCancellationRequested();
                var length = index - begin;
                if (length is <= 0 or > MaxDeltaBytes)
                    throw new InvalidDataException("Activity statistics journal has an invalid entry length.");
                StatisticsDelta delta;
                try
                {
                    delta = JsonSerializer.Deserialize<StatisticsDelta>(bytes.AsSpan(begin, length), DeltaJson)
                        ?? throw new InvalidDataException("Activity statistics journal has an empty entry.");
                }
                catch (JsonException ex) { throw new InvalidDataException("Activity statistics journal is invalid.", ex); }
                if (delta.SchemaVersion != 1)
                    throw new InvalidDataException("Activity statistics journal schema is unsupported.");
                if (delta.Generation == document.JournalGeneration)
                {
                    if (++events > MaxJournalEvents)
                        throw new InvalidDataException("Activity statistics journal exceeds its event limit.");
                    var record = delta.ToRecord();
                    try { Validate(record); }
                    catch (ArgumentException ex) { throw new InvalidDataException("Activity statistics journal contains an invalid record.", ex); }
                    ApplyRecord(document, record);
                }
                committedBytes = index + 1;
                begin = index + 1;
            }
            // No newline means the previous append never committed. Ignore that tail;
            // the next writer truncates it before appending a complete replacement.
            if (bytes.Length - committedBytes > MaxDeltaBytes)
                throw new InvalidDataException("Activity statistics journal has an oversized incomplete entry.");
        }
        ValidateDocument(document);
        MergedDocuments[_path] = new(snapshotStamp, journalStamp, document, events, committedBytes);
        return document;
    }

    private async Task AppendRecordAsync(StatisticsDocument document, ExecutionRecord record, CancellationToken ct)
    {
        var cached = MergedDocuments[_path];
        var delta = new StatisticsDelta(1, document.JournalGeneration, record.Id, record.StartedAt,
            record.Status, record.AgentModel, record.Usage);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(delta, DeltaJson);
        if (bytes.Length > MaxDeltaBytes) throw new InvalidDataException("Activity statistics delta exceeds its size limit.");
        if (cached.Events + 1 >= MaxJournalEvents || cached.CommittedJournalBytes + bytes.Length + 1 > MaxJournalBytes)
        {
            await WriteSnapshotAsync(document, ct).ConfigureAwait(false);
            return;
        }
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = (byte)'\n';

        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        await using (var stream = new FileStream(_journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            // Only complete newline-terminated entries are committed. This also repairs
            // an interrupted append without accepting or counting its partial content.
            if (stream.Length != cached.CommittedJournalBytes) stream.SetLength(cached.CommittedJournalBytes);
            stream.Position = cached.CommittedJournalBytes;
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        MergedDocuments[_path] = new(Stamp(_path), Stamp(_journalPath), document, cached.Events + 1,
            cached.CommittedJournalBytes + bytes.Length);
    }

    private async Task WriteSnapshotAsync(StatisticsDocument document, CancellationToken ct)
    {
        // Changing the generation makes old deltas harmless if the process stops after
        // replacing the snapshot but before removing the journal (including Clear).
        var generation = CheckpointGeneration(document);
        try
        {
            await HistoryFileStore.WriteAtomicAsync(_path, document, ct, indented: false).ConfigureAwait(false);
            HistoryFileStore.Invalidate(_path);
            document.JournalGeneration = generation;
            File.Delete(_journalPath);
            var journalStamp = Stamp(_journalPath);
            MergedDocuments[_path] = new(Stamp(_path), journalStamp, document, 0, 0);
        }
        catch { InvalidateDocuments(); throw; }
    }

    private static string CheckpointGeneration(StatisticsDocument document)
    {
        // Derive the journal generation from the canonical checkpoint, retaining the
        // existing snapshot schema. Changing its contents invalidates obsolete deltas.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, DeltaJson);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void InvalidateDocuments()
    {
        MergedDocuments.TryRemove(_path, out _);
        HistoryFileStore.Invalidate(_path);
    }
}
