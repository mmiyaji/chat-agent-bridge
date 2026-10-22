namespace ChatAgentBridge.Core.History;

public sealed class ExecutionHistoryStore
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "running", "completed", "failed", "cancelled", "interrupted" };
    private readonly string _filePath;
    private readonly ActivityStatisticsStore _statisticsStore;

    public ExecutionHistoryStore(string? directory = null)
    {
        var dataDirectory = directory ?? new SettingsStore().DirectoryPath;
        var fullDirectory = Path.GetFullPath(dataDirectory);
        _filePath = Path.Combine(fullDirectory, "history.json");
        _statisticsStore = new ActivityStatisticsStore(fullDirectory);
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
            records = records.OrderBy(item => item.StartedAt).TakeLast(HistoryFileStore.MaxRecords).ToList();
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
            await _statisticsStore.MigrateAsync(file.Records!, cancellationToken).ConfigureAwait(false);
            var liveIds = file.Records!.Where(record => record.Status == "running" && HistoryFileStore.IsLive(record.Id))
                .Select(record => record.Id).ToHashSet();
            await _statisticsStore.ReconcileStaleActivesAsync(liveIds, cancellationToken).ConfigureAwait(false);
            return file.Records!.OrderByDescending(item => item.StartedAt)
                .Select(item => item.Status == "running" && !HistoryFileStore.IsLive(item.Id)
                    ? item with { Status = "interrupted" }
                    : item)
                .ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<HistoryDocument> ReadFileAsync(CancellationToken ct)
    {
        var file = await HistoryFileStore.ReadAsync(_filePath, static () => new HistoryDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = [] }, ct).ConfigureAwait(false);
        HistoryFileStore.CheckSchema(file.SchemaVersion);
        var records = HistoryFileStore.ValidateLoadedRecords(file.Records);
        foreach (var record in records) ValidateLoaded(record);
        return file;
    }

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
