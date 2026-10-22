namespace ChatAgentBridge.Core.History;

public sealed class AuditStore
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "received", "approved", "rejected", "started", "completed", "failed", "cancelled", "reply_sent",
        "reply_failed", "detached", "workspace_changed", "model_changed", "agent_changed",
        "attachment_approved", "attachment_sent", "attachment_failed", "follow_up_applied", "follow_up_uncertain"
    };
    private readonly string _filePath;

    public AuditStore(string? directory = null)
    {
        var dataDirectory = directory ?? new SettingsStore().DirectoryPath;
        _filePath = Path.Combine(Path.GetFullPath(dataDirectory), "audit.json");
    }

    public async Task AppendAsync(AuditEvent record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            var records = file.Records!.ToList();
            records.Add(record);
            records = records.OrderBy(item => item.Timestamp).TakeLast(HistoryFileStore.MaxRecords).ToList();
            await HistoryFileStore.WriteAtomicAsync(_filePath,
                new AuditDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = records }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(CancellationToken cancellationToken = default)
    {
        var gate = HistoryFileStore.LockFor(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            return file.Records!.OrderByDescending(item => item.Timestamp).ToArray();
        }
        finally { gate.Release(); }
    }

    // Callers hold the per-path lock and must not mutate the returned document; it is the cached parse of the file.
    private Task<AuditDocument> ReadFileAsync(CancellationToken ct) =>
        HistoryFileStore.ReadCachedAsync(_filePath, static () => new AuditDocument { SchemaVersion = HistoryFileStore.SchemaVersion, Records = [] }, static file =>
        {
            HistoryFileStore.CheckSchema(file.SchemaVersion);
            var records = HistoryFileStore.ValidateLoadedRecords(file.Records);
            foreach (var record in records) ValidateLoaded(record);
        }, ct);

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
        if (record is null) throw new InvalidDataException("History file contains a null audit event.");
        try { Validate(record); }
        catch (ArgumentException ex) { throw new InvalidDataException("History file contains an invalid audit event.", ex); }
    }

    private sealed class AuditDocument
    {
        public int? SchemaVersion { get; set; }
        public List<AuditEvent>? Records { get; set; }
    }
}
