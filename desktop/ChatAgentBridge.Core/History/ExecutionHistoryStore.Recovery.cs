using System.Security;

namespace ChatAgentBridge.Core.History;

public sealed partial class ExecutionHistoryStore
{
    // Write-ahead terminal metadata only: the ExecutionRecord contract contains no
    // prompt, CLI output, exception message, stack trace or transport credential.
    internal const string RecoveryDirectoryName = "history-recovery";
    private string RecoveryDirectory => Path.Combine(Path.GetDirectoryName(_filePath)!, RecoveryDirectoryName);
    private string PendingPath(Guid id) => Path.Combine(RecoveryDirectory, $"{id:N}.json");
    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
    internal static bool IsOwnedRecoveryFile(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _) ||
        name.Length == 44 && name.StartsWith("history.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name[8..^4], "N", out _);

    private static void RejectLinkedRecoveryPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked history recovery paths are not supported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private async Task TryStageAsync(ExecutionRecord record)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            record = record with { FinishedAt = record.FinishedAt ?? (record.StartedAt > now ? record.StartedAt : now) };
            var path = PendingPath(record.Id);
            RejectLinkedRecoveryPath(RecoveryDirectory);
            RejectLinkedRecoveryPath(path);
            if (!File.Exists(path) && Directory.Exists(RecoveryDirectory) &&
                Directory.EnumerateFiles(RecoveryDirectory, "*.json").Take(HistoryFileStore.MaxRecords).Count() >= HistoryFileStore.MaxRecords)
                return; // Never evict an unresolved result to make space for another.
            await HistoryFileStore.WriteAtomicAsync(path, new PendingResult { SchemaVersion = 1, Record = record }, CancellationToken.None).ConfigureAwait(false);
            HistoryFileStore.Invalidate(path);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // A failed fallback must not prevent a healthy primary write or mask
            // its original exception when both destinations are unavailable.
        }
    }

    private void TryDeletePending(Guid id)
    {
        try { RejectLinkedRecoveryPath(RecoveryDirectory); RejectLinkedRecoveryPath(PendingPath(id)); File.Delete(PendingPath(id)); }
        catch (Exception exception) when (IsStorageFailure(exception)) { }
    }

    // Caller holds the history gate. Recovery is idempotent and never replaces a
    // newer, authoritative terminal result with an older pending one.
    private async Task<HistoryDocument> RecoverPendingAsync(CancellationToken ct, Guid? skipId = null)
    {
        var file = await ReadFileAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(RecoveryDirectory)) return file;
        RejectLinkedRecoveryPath(RecoveryDirectory);
        var paths = Directory.EnumerateFiles(RecoveryDirectory, "*.json").Take(HistoryFileStore.MaxRecords + 1).ToArray();
        if (paths.Length > HistoryFileStore.MaxRecords) throw new InvalidDataException("Too many pending history results.");
        var records = file.Records!.ToList();
        var applied = new List<ExecutionRecord>();
        var consumed = new List<Guid>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                throw new InvalidDataException("Invalid pending history filename.");
            if (id == skipId) continue;
            RejectLinkedRecoveryPath(path);
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Pending history result exceeds its size limit.");
            var pending = await HistoryFileStore.ReadAsync(path, static () => new PendingResult(), ct).ConfigureAwait(false);
            if (pending.SchemaVersion != 1 || pending.Record is null || pending.Record.Id != id ||
                pending.Record.Status == "running" || pending.Record.FinishedAt is null)
                throw new InvalidDataException("Invalid pending terminal history result.");
            ValidateLoaded(pending.Record);
            var existing = records.FindIndex(record => record.Id == id);
            var terminal = pending.Record;
            if (existing < 0 || records[existing].Status == "running")
            {
                if (existing >= 0) records[existing] = terminal;
                else records.Add(terminal);
                applied.Add(terminal);
            }
            else if (records[existing].HistorySaveDiagnostic is null && terminal.HistorySaveDiagnostic is not null &&
                records[existing].Status == terminal.Status && records[existing].FinishedAt == terminal.FinishedAt)
            {
                terminal = records[existing] with { HistorySaveDiagnostic = terminal.HistorySaveDiagnostic };
                records[existing] = terminal;
                applied.Add(terminal);
            }
            consumed.Add(id);
        }
        if (consumed.Count == 0) return file;
        var recovered = new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = records };
        try
        {
            if (applied.Count > 0)
            {
                await _statisticsStore.MigrateAsync(file.Records!, ct).ConfigureAwait(false);
                foreach (var terminal in applied) await _statisticsStore.RecordAsync(terminal, ct).ConfigureAwait(false);
                var preferences = await _retentionStore.LoadAsync(ct).ConfigureAwait(false);
                recovered.Records = Prune(records, preferences, _timeProvider.GetUtcNow());
                await HistoryFileStore.WriteAtomicAsync(_filePath, recovered, ct).ConfigureAwait(false);
            }
            foreach (var id in consumed)
            {
                HistoryFileStore.EndTracking(id);
                TryDeletePending(id);
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Show the known result even while repair is blocked; keep its durable
            // recovery file for a later read, execution, or application restart.
        }
        return recovered;
    }

    private sealed class PendingResult
    {
        public int SchemaVersion { get; set; }
        public ExecutionRecord? Record { get; set; }
    }
}
