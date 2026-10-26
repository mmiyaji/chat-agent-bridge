namespace ChatAgentBridge.Core.History;

public sealed class ExecutionHistoryStore
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "running", "completed", "failed", "cancelled", "interrupted" };
    private readonly string _filePath;
    private readonly ActivityStatisticsStore _statisticsStore;
    private readonly LogRetentionPreferencesStore _retentionStore;
    private readonly TimeProvider _timeProvider;

    public ExecutionHistoryStore(string? directory = null, TimeProvider? timeProvider = null)
    {
        var dataDirectory = directory ?? new SettingsStore().DirectoryPath;
        var fullDirectory = Path.GetFullPath(dataDirectory);
        _filePath = Path.Combine(fullDirectory, "history.json");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statisticsStore = new ActivityStatisticsStore(fullDirectory, _timeProvider);
        _retentionStore = new LogRetentionPreferencesStore(fullDirectory, _timeProvider);
    }

    public async Task RecordAsync(ExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            var records = file.Records!.ToList();
            await _statisticsStore.MigrateAsync(records, cancellationToken).ConfigureAwait(false);
            // Commit aggregates first; a retry after a history write failure is idempotent.
            await _statisticsStore.RecordAsync(record, cancellationToken).ConfigureAwait(false);
            records.RemoveAll(existing => existing.Id == record.Id);
            records.Add(record);
            var preferences = await _retentionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            records = Prune(records, preferences, _timeProvider.GetUtcNow(), record.Status == "running" ? record.Id : null);
            await HistoryFileStore.WriteAtomicAsync(_filePath, new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = records }, cancellationToken).ConfigureAwait(false);
            HistoryFileStore.SetLive(record);
        }
        finally { gate.Release(); }
    }

    /// <summary>Stops treating an execution as live without changing its persisted record.</summary>
    public void EndTracking(Guid executionId)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("Execution ID cannot be empty.", nameof(executionId));
        HistoryFileStore.EndTracking(executionId);
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            // Migrate all surviving source rows before applying history retention so
            // long lived aggregates retain the full pre-pruning history.
            await _statisticsStore.MigrateAsync(file.Records!, cancellationToken).ConfigureAwait(false);
            var preferences = await _retentionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var retained = Prune(file.Records!, preferences, _timeProvider.GetUtcNow());
            if (retained.Count != file.Records!.Count)
                await HistoryFileStore.WriteAtomicAsync(_filePath, new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = retained }, cancellationToken).ConfigureAwait(false);
            var liveIds = file.Records!.Where(record => record.Status == "running" && HistoryFileStore.IsLive(record.Id))
                .Select(record => record.Id).ToHashSet();
            await _statisticsStore.ReconcileStaleActivesAsync(liveIds, cancellationToken).ConfigureAwait(false);
            return retained.OrderByDescending(item => item.StartedAt)
                .Select(item => item.Status == "running" && !HistoryFileStore.IsLive(item.Id)
                    ? item with { Status = "interrupted" }
                    : item)
                .ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return;
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            await _statisticsStore.MigrateAsync(file.Records!, cancellationToken).ConfigureAwait(false);
            var preferences = await _retentionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var retained = Prune(file.Records!, preferences, _timeProvider.GetUtcNow());
            if (retained.Count != file.Records!.Count)
                await HistoryFileStore.WriteAtomicAsync(_filePath, new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = retained }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static List<ExecutionRecord> Prune(IEnumerable<ExecutionRecord> source, LogRetentionPreferences preferences,
        DateTimeOffset now, Guid? incomingLiveId = null)
    {
        var cutoff = now.ToUniversalTime().AddDays(-preferences.HistoryRetentionDays);
        bool IsLive(ExecutionRecord record) => record.Status == "running" &&
            (record.Id == incomingLiveId || HistoryFileStore.IsLive(record.Id));
        var records = source.Where(record => IsLive(record) || record.StartedAt.ToUniversalTime() >= cutoff)
            .OrderBy(item => item.StartedAt).ToList();
        var live = records.Where(IsLive).Take(HistoryFileStore.MaxRecords).ToList();
        var slotLimit = Math.Min(HistoryFileStore.MaxRecords, Math.Max(preferences.HistoryMaxRecords, live.Count));
        var other = records.Where(record => !IsLive(record)).TakeLast(Math.Max(0, slotLimit - live.Count));
        return live.Concat(other).OrderBy(item => item.StartedAt).ToList();
    }

    // Callers hold the per-path lock and must not mutate the returned document; it is the cached parse of the file.
    private Task<HistoryDocument> ReadFileAsync(CancellationToken ct) =>
        HistoryFileStore.ReadCachedAsync(_filePath, static () => new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = [] }, static file =>
        {
            HistoryFileStore.CheckSchema(file.SchemaVersion);
            var records = HistoryFileStore.ValidateLoadedRecords(file.Records);
            foreach (var record in records) ValidateLoaded(record);
        }, ct);

    private static void Validate(ExecutionRecord record)
    {
        if (record.Id == Guid.Empty) throw new ArgumentException("Execution ID cannot be empty.", nameof(record));
        if (record.StartedAt == default || record.FinishedAt < record.StartedAt) throw new ArgumentException("Execution timestamps are invalid.", nameof(record));
        HistoryFileStore.ValidateSource(record.Source);
        if (!AllowedStatuses.Contains(record.Status)) throw new ArgumentException("Execution status is not allowlisted.", nameof(record));
        HistoryFileStore.ValidateText(record.ChannelId, nameof(record.ChannelId), 256);
        HistoryFileStore.ValidateText(record.ThreadId, nameof(record.ThreadId), 256);
        HistoryFileStore.ValidateText(record.UserId, nameof(record.UserId), 256);
        HistoryFileStore.ValidateText(record.WorkspaceRoot, nameof(record.WorkspaceRoot), 2048);
        HistoryFileStore.ValidateText(record.SessionId, nameof(record.SessionId), 256);
        HistoryFileStore.ValidateText(record.AgentModel, nameof(record.AgentModel), 256);
        _ = AgentProfile.NormalizeReasoningEffort(record.AgentReasoningEffort);
        if (record.Usage is { } usage)
        {
            if (new[] { usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens }.Any(value => value < 0))
                throw new ArgumentException("Token counts cannot be negative.", nameof(record));
            if (usage.Scope is not ("turn" or "last-request" or "thread-total"))
                throw new ArgumentException("Unknown usage observation scope.", nameof(record));
            HistoryFileStore.ValidateText(usage.Source, nameof(usage.Source), 64);
            HistoryFileStore.ValidateText(usage.Model, nameof(usage.Model), 256);
        }
        if (record.Metrics is { } metrics && new[] { metrics.ProcessStartMilliseconds, metrics.InitializeMilliseconds, metrics.TotalMilliseconds, metrics.QueueMilliseconds }
            .Any(value => value is double milliseconds && (!double.IsFinite(milliseconds) || milliseconds < 0)))
            throw new ArgumentException("Execution durations must be finite and nonnegative.", nameof(record));
    }

    private static void ValidateLoaded(ExecutionRecord? record)
    {
        if (record is null) throw new InvalidDataException("History file contains a null execution record.");
        try { Validate(record); }
        catch (ArgumentException ex) { throw new InvalidDataException("History file contains an invalid execution record.", ex); }
    }

    private sealed class HistoryDocument
    {
        public int? SchemaVersion { get; set; }
        public List<ExecutionRecord>? Records { get; set; }
    }
}
