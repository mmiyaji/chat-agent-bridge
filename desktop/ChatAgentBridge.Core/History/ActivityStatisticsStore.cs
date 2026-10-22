using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core.Pricing;

namespace ChatAgentBridge.Core.History;

/// <summary>Durable, privacy-minimized day/model statistics that outlive the bounded execution list.</summary>
public sealed class ActivityStatisticsStore
{
    public const int MaxDays = 3660;
    public const int MaxModelsPerDay = 128;
    public const int MaxRecentFinalizedIds = 500;
    public const int MaxActiveExecutions = 500;
    public const int MaxUsageGroupsPerDay = MaxModelsPerDay * 12;
    public const string OtherModelsKey = "__other_models__";
    // Once a statistics file has recorded that migration completed, later checks skip the lock and the file read.
    private static readonly ConcurrentDictionary<string, byte> MigratedPaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;

    public ActivityStatisticsStore(string? directory = null)
    {
        var dataDirectory = directory ?? new SettingsStore().DirectoryPath;
        _path = Path.Combine(Path.GetFullPath(dataDirectory), "activity-statistics.json");
    }

    public async Task MigrateAsync(IReadOnlyList<ExecutionRecord> retainedHistory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedHistory);
        if (MigratedPaths.ContainsKey(_path) && File.Exists(_path)) return;
        var gate = HistoryFileStore.LockFor(_path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document.MigrationCompleted) { MigratedPaths[_path] = 0; return; }
            var migrated = EmptyDocument();
            foreach (var record in retainedHistory.OrderBy(item => item.StartedAt))
            {
                Validate(record);
                if (record.Status == "running") AddActive(migrated, record);
                else AddFinalized(migrated, record);
            }
            migrated.MigrationCompleted = true;
            migrated.MigratedFromAvailableRetainedHistory = retainedHistory.Count > 0;
            migrated.TimeZoneId = TimeZoneInfo.Local.Id;
            Prune(migrated);
            await WriteAsync(migrated, cancellationToken).ConfigureAwait(false);
            MigratedPaths[_path] = 0;
        }
        finally { gate.Release(); }
    }

    /// <summary>Records one execution update exactly once while retaining only bounded deduplication markers.</summary>
    public async Task RecordAsync(ExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var gate = HistoryFileStore.LockFor(_path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // The cached document is edited in place; drop it unless this call ends in a successful write.
        var committed = false;
        try
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (!document.MigrationCompleted)
            {
                // An explicit event stream can exist before history has been read. Keep it intact and mark
                // migration complete; future history reads must never import an older view over it.
                document.MigrationCompleted = true;
                document.TimeZoneId = TimeZoneInfo.Local.Id;
            }
            if (record.Status == "running")
            {
                if (document.FinalizedIds.Any(marker => marker.Id == record.Id)) return;
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
                if (active is null && document.FinalizedIds.Any(marker => marker.Id == record.Id)) return;
                if (active is null && document.CutoffStartedAt is { } cutoff && record.StartedAt <= cutoff) return;
                if (active is not null) document.Active.Remove(active);
                AddFinalized(document, record);
            }
            Prune(document);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            committed = true;
            MigratedPaths[_path] = 0;
        }
        finally
        {
            if (!committed) HistoryFileStore.Invalidate(_path);
            gate.Release();
        }
    }

    /// <summary>Finalizes stale active markers after restart; never imports a new event from history.</summary>
    public async Task ReconcileStaleActivesAsync(IReadOnlySet<Guid> liveExecutionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveExecutionIds);
        var gate = HistoryFileStore.LockFor(_path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var stale = document.Active.Where(marker => !liveExecutionIds.Contains(marker.Id) && !HistoryFileStore.IsLive(marker.Id)).ToArray();
            if (stale.Length == 0) { committed = true; return; }
            foreach (var marker in stale)
            {
                document.Active.Remove(marker);
                AddFinalized(document, new ExecutionRecord
                {
                    Id = marker.Id, StartedAt = marker.StartedAt, Status = "interrupted", AgentModel = marker.Model
                });
            }
            Prune(document);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (!committed) HistoryFileStore.Invalidate(_path);
            gate.Release();
        }
    }

    public async Task<ActivityStatisticsSnapshot> ReadAsync(DateOnly endDay, int dayCount, TimeZoneInfo zone,
        string scope = "turn", PricingSnapshot? pricing = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (dayCount is < 1 or > 366) throw new ArgumentOutOfRangeException(nameof(dayCount));
        if (scope is not ("turn" or "last-request")) throw new ArgumentException("Scope must be 'turn' or 'last-request'.", nameof(scope));
        // First read also upgrades an installation that has only rolling history. Run this before
        // taking the statistics lock because ListAsync performs the one-time migration itself.
        await new ExecutionHistoryStore(Path.GetDirectoryName(_path)!).ListAsync(cancellationToken).ConfigureAwait(false);
        var gate = HistoryFileStore.LockFor(_path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var start = endDay.AddDays(1 - dayCount);
            var days = Enumerable.Range(0, dayCount).Select(i => new StatisticsDay { Date = start.AddDays(i) }).ToArray();
            var byDate = days.ToDictionary(day => day.Date);
            var modelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var completed = 0; var failed = 0; var cancelled = 0; var running = 0;
            foreach (var source in doc.Days)
            {
                if (!byDate.TryGetValue(source.Date, out var target)) continue;
                target.Total = source.Total;
                target.Completed = source.Completed;
                target.Failed = source.Failed;
                target.Cancelled = source.Cancelled;
                completed += source.Completed; failed += source.Failed; cancelled += source.Cancelled;
                foreach (var model in source.Models)
                    AddCount(modelCounts, ModelKey(model.Model), model.Count);
                foreach (var pair in source.Usage)
                {
                    var normalized = NormalizeScope(pair.Source, pair.Scope);
                    if (normalized == scope)
                    {
                        var key = ModelKey(pair.Model);
                        if (!target.Usage.TryGetValue(key, out var bucket)) target.Usage[key] = bucket = new UsageAggregate();
                        bucket.ExecutionCount = checked(bucket.ExecutionCount + pair.Value.UsageReportedCount);
                        bucket.Merge(pair.Value);
                    }
                    else target.ExcludedScopeCount = checked(target.ExcludedScopeCount + pair.Value.UsageReportedCount);
                }
            }
            foreach (var active in doc.Active)
            {
                if (active.Day < start || active.Day > endDay) continue;
                byDate[active.Day].Total++;
                running++;
                AddCount(modelCounts, ModelKey(active.Model), 1);
            }

            var activityDays = days.Select(day => new ActivityDay(day.Date, day.Total)).ToArray();
            var activeDays = 0; var streak = 0; var longest = 0; var totalRuns = 0; ActivityDay? peak = null;
            var weekdayCounts = new int[7];
            foreach (var day in activityDays)
            {
                totalRuns += day.Count;
                weekdayCounts[((int)day.Date.DayOfWeek + 6) % 7] += day.Count;
                if (day.Count > 0)
                {
                    activeDays++; streak++; longest = Math.Max(longest, streak);
                    if (peak is null || day.Count >= peak.Count) peak = day;
                }
                else streak = 0;
            }
            var models = modelCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(5).Select(pair => new ModelActivity(NullModel(pair.Key), pair.Value)).ToArray();
            var activity = new ActivitySummary(start, endDay, totalRuns, activeDays, longest, peak,
                Array.AsReadOnly(activityDays), Array.AsReadOnly(Enumerable.Range(0, 7)
                    .Select(i => new WeekdayActivity((DayOfWeek)((i + 1) % 7), weekdayCounts[i])).ToArray()),
                Array.AsReadOnly(models), completed, failed, cancelled, running);
            var usage = BuildUsageSummary(days, start, endDay, scope, pricing);
            return new ActivityStatisticsSnapshot(activity, usage, doc.MigratedFromAvailableRetainedHistory,
                doc.TimeZoneId ?? TimeZoneInfo.Local.Id);
        }
        finally { gate.Release(); }
    }

    /// <summary>Clears only durable statistics. The migrated flag prevents retained history from repopulating them.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var gate = HistoryFileStore.LockFor(_path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = EmptyDocument();
            document.MigrationCompleted = true;
            document.TimeZoneId = TimeZoneInfo.Local.Id;
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            MigratedPaths[_path] = 0;
        }
        finally { gate.Release(); }
    }

    // Callers hold the per-path lock. The result is the cached parse: only RecordAsync and
    // ReconcileStaleActivesAsync edit it, and they invalidate it unless they finish with a write.
    private async Task<StatisticsDocument> ReadDocumentAsync(CancellationToken ct)
    {
        var fileInfo = new FileInfo(_path);
        if (fileInfo.Exists && fileInfo.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Activity statistics file exceeds its size limit.");
        return await HistoryFileStore.ReadCachedAsync(_path, EmptyDocument, ValidateDocument, ct).ConfigureAwait(false);
    }

    private static void ValidateDocument(StatisticsDocument doc)
    {
        if (doc.SchemaVersion != 1 || doc.Days is null || doc.Active is null || doc.FinalizedIds is null)
            throw new InvalidDataException("Activity statistics file has an unsupported schema or missing collections.");
        if (doc.Days.Count > MaxDays || doc.Active.Count > MaxActiveExecutions || doc.FinalizedIds.Count > MaxRecentFinalizedIds)
            throw new InvalidDataException("Activity statistics file exceeds supported bounds.");
        var dates = new HashSet<DateOnly>();
        foreach (var day in doc.Days)
        {
            if (day is null || day.Models is null || day.Usage is null || day.Total < 0 || day.Completed < 0 || day.Failed < 0 || day.Cancelled < 0 ||
                day.Models.Count > MaxModelsPerDay || day.Usage.Count > MaxUsageGroupsPerDay || !dates.Add(day.Date))
                throw new InvalidDataException("Activity statistics contain an invalid or duplicate day bucket.");
            var modelNames = new HashSet<string>(StringComparer.Ordinal);
            if (day.Models.Any(model => model is null || model.Count <= 0 || model.Model is { Length: > 256 } || !modelNames.Add(ModelKey(model.Model))))
                throw new InvalidDataException("Activity statistics contain an invalid model bucket.");
            var usageKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in day.Usage)
            {
                if (entry is null || entry.Value is null || entry.Source is null || entry.Scope is null || entry.Source.Length > 64 || entry.Scope.Length > 32 ||
                    entry.Model is { Length: > 256 } || !usageKeys.Add($"{entry.Model}\0{entry.Source}\0{entry.Scope}"))
                    throw new InvalidDataException("Activity statistics contain an invalid usage group.");
                ValidateUsage(entry.Value);
            }
        }
        var activeIds = new HashSet<Guid>();
        if (doc.Active.Any(marker => marker is null || marker.Id == Guid.Empty || marker.StartedAt == default || marker.Model is { Length: > 256 } || !activeIds.Add(marker.Id)))
            throw new InvalidDataException("Activity statistics contain an invalid active marker.");
        var finalizedIds = new HashSet<Guid>();
        if (doc.FinalizedIds.Any(marker => marker is null || marker.Id == Guid.Empty || marker.StartedAt == default || !finalizedIds.Add(marker.Id)))
            throw new InvalidDataException("Activity statistics contain an invalid finalized marker.");
    }

    private static void ValidateUsage(UsageAggregate usage)
    {
        if (usage.Input < 0 || usage.Output < 0 || usage.Cached < 0 || usage.CacheWrite < 0 || usage.PairedTotal < 0 ||
            usage.CacheNumerator < 0 || usage.CacheDenominator < 0 || usage.ExecutionCount < 0 || usage.TokenReportedCount < 0 ||
            usage.CacheReportedCount < 0 || usage.UsageReportedCount < 0 || usage.ExcludedScopeCount < 0 || usage.Costs is null || usage.Costs.Count > 1024)
            throw new InvalidDataException("Activity statistics contain invalid usage totals.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cost in usage.Costs)
            if (cost is null || cost.Key is null || cost.Source is null || cost.Scope is null || cost.Count <= 0 ||
                cost.Input < 0 || cost.Output < 0 || cost.Cached < 0 || cost.Write < 0 || !keys.Add(cost.Key))
                throw new InvalidDataException("Activity statistics contain an invalid cost feature.");
    }

    // Derived data that only this class reads, so skip indentation to shrink the file and the write.
    private Task WriteAsync(StatisticsDocument document, CancellationToken ct) =>
        HistoryFileStore.WriteAtomicAsync(_path, document, ct, indented: false);

    private static StatisticsDocument EmptyDocument() => new() { SchemaVersion = 1, Days = [], Active = [], FinalizedIds = [] };

    private static void AddActive(StatisticsDocument document, ExecutionRecord record)
    {
        if (document.Active.Any(marker => marker.Id == record.Id)) return;
        if (document.Active.Count >= MaxActiveExecutions)
            throw new IOException($"Activity statistics cannot track more than {MaxActiveExecutions} simultaneous executions. Clear or finish active executions and retry.");
        document.Active.Add(new ActiveMarker { Id = record.Id, StartedAt = record.StartedAt, Day = LocalDay(record.StartedAt), Model = ActivityModel(record) });
    }

    private static void AddFinalized(StatisticsDocument document, ExecutionRecord record)
    {
        var day = GetDay(document, LocalDay(record.StartedAt));
        day.Total = checked(day.Total + 1);
        switch (record.Status)
        {
            case "completed": day.Completed = checked(day.Completed + 1); break;
            case "failed": day.Failed = checked(day.Failed + 1); break;
            case "cancelled": day.Cancelled = checked(day.Cancelled + 1); break;
        }
        AddModel(day, BoundedModel(day, ActivityModel(record)), 1);
        if (record.Usage is { } usage)
        {
            var usageModel = BoundedModel(day, string.IsNullOrWhiteSpace(usage.Model) ? null : usage.Model);
            var normalized = NormalizeScope(usage.Source, usage.Scope);
            var aggregate = day.Usage.FirstOrDefault(item => item.Model == usageModel && item.Source == usage.Source && item.Scope == normalized);
            if (aggregate is null)
            {
                if (day.Usage.Count >= MaxUsageGroupsPerDay)
                    throw new IOException($"Activity statistics reached the {MaxUsageGroupsPerDay} usage-group limit for one day; retry after clearing activity statistics.");
                aggregate = new UsageEntry { Model = usageModel, Source = usage.Source, Scope = normalized };
                day.Usage.Add(aggregate);
            }
            aggregate.Value.Add(usage, usageModel);
        }
        document.FinalizedIds.Add(new IdMarker { Id = record.Id, StartedAt = record.StartedAt });
        while (document.FinalizedIds.Count > MaxRecentFinalizedIds)
        {
            var oldest = document.FinalizedIds.OrderBy(item => item.StartedAt).First();
            document.FinalizedIds.Remove(oldest);
            if (document.CutoffStartedAt is null || oldest.StartedAt > document.CutoffStartedAt)
                document.CutoffStartedAt = oldest.StartedAt;
        }
    }

    private static DayAggregate GetDay(StatisticsDocument document, DateOnly date)
    {
        var day = document.Days.FirstOrDefault(item => item.Date == date);
        if (day is not null) return day;
        day = new DayAggregate { Date = date, Models = [], Usage = [] };
        document.Days.Add(day);
        return day;
    }

    private static void AddModel(DayAggregate day, string? model, int count)
    {
        var entry = day.Models.FirstOrDefault(item => item.Model == model);
        if (entry is null && day.Models.Count >= MaxModelsPerDay)
        {
            model = OtherModelsKey;
            entry = day.Models.FirstOrDefault(item => item.Model == model);
            if (entry is null) { entry = new ModelCountEntry { Model = model }; day.Models.Add(entry); }
        }
        else if (entry is null) { entry = new ModelCountEntry { Model = model }; day.Models.Add(entry); }
        entry.Count = checked(entry.Count + count);
    }

    private static string? BoundedModel(DayAggregate day, string? model)
    {
        if (day.Models.Any(item => item.Model == model) || day.Models.Count < MaxModelsPerDay - 1) return model;
        return OtherModelsKey;
    }

    private static void Prune(StatisticsDocument document)
    {
        document.Days = document.Days.OrderByDescending(day => day.Date).Take(MaxDays).OrderBy(day => day.Date).ToList();
        document.TimeZoneId ??= TimeZoneInfo.Local.Id;
    }

    private static string? ActivityModel(ExecutionRecord record) =>
        string.IsNullOrWhiteSpace(record.Usage?.Model) ? EmptyToNull(record.AgentModel) : record.Usage!.Model;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateOnly LocalDay(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime);
    private static string NormalizeScope(string source, string scope) => source == "claude.stream-json" && scope == "thread-total" ? "turn" : scope;
    private static string ModelKey(string? model) => model ?? "\0";
    private static string? NullModel(string model) => model == "\0" ? null : model;
    private static void AddCount(Dictionary<string, int> counts, string model, int value)
    { counts.TryGetValue(model, out var current); counts[model] = checked(current + value); }

    private static ActivityUsageSummary BuildUsageSummary(StatisticsDay[] days, DateOnly start, DateOnly end,
        string scope, PricingSnapshot? pricing)
    {
        var usageDays = new List<ActivityUsageDay>(days.Length);
        var modelTotals = new Dictionary<string, UsageAggregate>(StringComparer.Ordinal);
        var overall = new UsageAggregate();
        foreach (var day in days)
        {
            var totals = new UsageAggregate { ExecutionCount = day.Total, ExcludedScopeCount = day.ExcludedScopeCount };
            foreach (var pair in day.Usage)
            {
                totals.Merge(pair.Value);
                if (!modelTotals.TryGetValue(pair.Key, out var model)) modelTotals[pair.Key] = model = new UsageAggregate();
                model.Merge(pair.Value);
            }
            // Total execution coverage includes records with no usage report and reports excluded by scope.
            totals.ExecutionCount = day.Total;
            overall.Merge(totals);
            usageDays.Add(new ActivityUsageDay(day.Date, totals.ToTotals(pricing)));
        }
        var models = modelTotals.Select(pair => new ActivityUsageModel(NullModel(pair.Key), pair.Value.ToTotals(pricing)))
            .OrderByDescending(item => item.Totals.TotalTokens ?? decimal.MinValue).ThenBy(item => item.Model, StringComparer.Ordinal).ToArray();
        return ActivityUsageSummary.FromAggregates(start, end, scope, Array.AsReadOnly(usageDays.ToArray()), Array.AsReadOnly(models), overall.ToTotals(pricing));
    }

    private static void Validate(ExecutionRecord record)
    {
        if (record.Id == Guid.Empty || record.StartedAt == default || record.FinishedAt < record.StartedAt ||
            record.Status is not ("running" or "completed" or "failed" or "cancelled" or "interrupted"))
            throw new ArgumentException("Activity statistics record is invalid.", nameof(record));
        if (record.Usage is { } usage && (new[] { usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens }.Any(value => value < 0) ||
            usage.Scope is null || usage.Source is null || usage.Scope is not ("turn" or "last-request" or "thread-total") || usage.Source.Length > 64 || usage.Source.Contains('\0') ||
            usage.Model is { Length: > 256 } || usage.Model?.Contains('\0') == true || record.AgentModel is { Length: > 256 } || record.AgentModel?.Contains('\0') == true))
            throw new ArgumentException("Activity statistics usage fields are invalid.", nameof(record));
    }

    private sealed class NullableOrdinalComparer : IComparer<string?>, IEqualityComparer<string?>
    {
        public static NullableOrdinalComparer Instance { get; } = new();
        public int Compare(string? x, string? y) => StringComparer.Ordinal.Compare(x, y);
        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);
        public int GetHashCode(string? obj) => obj is null ? 0 : StringComparer.Ordinal.GetHashCode(obj);
    }

    private sealed class StatisticsDay { public DateOnly Date; public int Total; public int Completed; public int Failed; public int Cancelled; public int ExcludedScopeCount; public Dictionary<string, UsageAggregate> Usage { get; } = new(StringComparer.Ordinal); }
    private sealed class UsageEntry { public string? Model { get; set; } public string Source { get; set; } = ""; public string Scope { get; set; } = ""; public UsageAggregate Value { get; set; } = new(); }
    private sealed class DayAggregate { public DateOnly Date { get; set; } public int Total { get; set; } public int Completed { get; set; } public int Failed { get; set; } public int Cancelled { get; set; } public List<ModelCountEntry> Models { get; set; } = []; public List<UsageEntry> Usage { get; set; } = []; }
    private sealed class ModelCountEntry { public string? Model { get; set; } public int Count { get; set; } }
    private sealed class ActiveMarker { public Guid Id { get; set; } public DateTimeOffset StartedAt { get; set; } public DateOnly Day { get; set; } public string? Model { get; set; } }
    private sealed class IdMarker { public Guid Id { get; set; } public DateTimeOffset StartedAt { get; set; } }
    private sealed class StatisticsDocument { public int SchemaVersion { get; set; } public bool MigrationCompleted { get; set; } public bool MigratedFromAvailableRetainedHistory { get; set; } public string? TimeZoneId { get; set; } public DateTimeOffset? CutoffStartedAt { get; set; } public List<DayAggregate> Days { get; set; } = []; public List<ActiveMarker> Active { get; set; } = []; public List<IdMarker> FinalizedIds { get; set; } = []; }

    private sealed class UsageAggregate
    {
        public decimal Input { get; set; } public decimal Output { get; set; } public decimal Cached { get; set; } public decimal CacheWrite { get; set; } public decimal PairedTotal { get; set; } public decimal CacheNumerator { get; set; } public decimal CacheDenominator { get; set; }
        public bool HasInput { get; set; } public bool HasOutput { get; set; } public bool HasCached { get; set; } public bool HasCacheWrite { get; set; } public bool HasPairedTotal { get; set; } public bool HasCachePair { get; set; }
        public int ExecutionCount { get; set; } public int TokenReportedCount { get; set; } public int CacheReportedCount { get; set; } public int UsageReportedCount { get; set; } public int ExcludedScopeCount { get; set; }
        public List<CostFeature> Costs { get; set; } = [];
        public void Add(AgentUsage usage, string? model)
        {
            UsageReportedCount++;
            var input = NormalizedInput(usage); var output = NonNegative(usage.OutputTokens);
            if (input is decimal i)
            {
                Input += i; HasInput = true;
                var cached = NonNegative(usage.CachedInputTokens); var write = NonNegative(usage.CacheWriteInputTokens);
                if (cached > i) cached = null; if (write > i) write = null;
                if (cached is decimal c) { Cached += c; HasCached = true; CacheNumerator += c; CacheDenominator += i; HasCachePair = true; CacheReportedCount++; }
                if (write is decimal w) { CacheWrite += w; HasCacheWrite = true; }
            }
            if (output is decimal o) { Output += o; HasOutput = true; }
            if (input is decimal pi && output is decimal po) { PairedTotal += pi + po; HasPairedTotal = true; TokenReportedCount++; }
            var feature = CostFeature.From(usage);
            feature.Model = model;
            feature.Key = model + "|" + feature.Key;
            var existing = Costs.FirstOrDefault(item => item.Key == feature.Key);
            if (existing is null)
            {
                if (Costs.Count >= 1024) throw new IOException("Activity statistics reached the per-model usage feature limit; retry after clearing activity statistics.");
                Costs.Add(feature);
            }
            else existing.Merge(feature);
        }
        public void Merge(UsageAggregate other)
        {
            ExecutionCount += other.ExecutionCount; TokenReportedCount += other.TokenReportedCount; CacheReportedCount += other.CacheReportedCount;
            UsageReportedCount += other.UsageReportedCount; ExcludedScopeCount += other.ExcludedScopeCount;
            Input += other.Input; Output += other.Output; Cached += other.Cached; CacheWrite += other.CacheWrite; PairedTotal += other.PairedTotal;
            CacheNumerator += other.CacheNumerator; CacheDenominator += other.CacheDenominator;
            HasInput |= other.HasInput; HasOutput |= other.HasOutput; HasCached |= other.HasCached; HasCacheWrite |= other.HasCacheWrite;
            HasPairedTotal |= other.HasPairedTotal; HasCachePair |= other.HasCachePair;
            foreach (var feature in other.Costs)
            {
                var existing = Costs.FirstOrDefault(item => item.Key == feature.Key);
                if (existing is null) Costs.Add(feature.Clone()); else existing.Merge(feature);
            }
        }
        public ActivityUsageTotals ToTotals(PricingSnapshot? pricing)
        {
            var cost = Cost(pricing);
            return new ActivityUsageTotals(HasInput ? Input : null, HasOutput ? Output : null, HasCached ? Cached : null,
                HasCacheWrite ? CacheWrite : null, HasPairedTotal ? PairedTotal : null,
                HasCachePair && CacheDenominator > 0 ? (double)(100m * CacheNumerator / CacheDenominator) : null,
                ExecutionCount, TokenReportedCount, CacheReportedCount, UsageReportedCount, ExcludedScopeCount) { Cost = cost };
        }
        private ActivityCostTotals Cost(PricingSnapshot? pricing)
        {
            pricing ??= AgentCostEstimator.EmbeddedPricingSnapshot;
            decimal min = 0m, max = 0m; var estimated = 0; var ranges = 0; var overflow = false;
            foreach (var feature in Costs)
            {
                if (!feature.Estimate(pricing, out var low, out var high, out var hasRange)) continue;
                estimated = checked(estimated + feature.Count);
                if (hasRange) ranges = checked(ranges + feature.Count);
                if (overflow) continue;
                try { min = checked(min + low); max = checked(max + high); }
                catch (OverflowException) { overflow = true; }
            }
            return new ActivityCostTotals(estimated > 0 && !overflow ? min : null, estimated > 0 && !overflow ? max : null, estimated, ranges);
        }
        private static decimal? NormalizedInput(AgentUsage usage)
        {
            if (usage.InputIncludesCache) return NonNegative(usage.InputTokens);
            var i = NonNegative(usage.InputTokens); var c = NonNegative(usage.CachedInputTokens); var w = NonNegative(usage.CacheWriteInputTokens);
            return i is decimal iv && c is decimal cv && w is decimal wv ? iv + cv + wv : null;
        }
        private static decimal? NonNegative(long? value) => value is >= 0 ? value.Value : null;
    }

    private sealed class CostFeature
    {
        public string Key { get; set; } = "";
        public string Source { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? Model { get; set; }
        public bool IncludesCache { get; set; }
        public bool HasInput { get; set; }
        public bool HasOutput { get; set; }
        public bool HasCached { get; set; }
        public bool HasWrite { get; set; }
        public bool InputPositive { get; set; }
        public bool OutputPositive { get; set; }
        public bool CachedPositive { get; set; }
        public bool WritePositive { get; set; }
        public bool Valid { get; set; }
        public int Count { get; set; }
        public decimal Input { get; set; }
        public decimal Output { get; set; }
        public decimal Cached { get; set; }
        public decimal Write { get; set; }

        public static CostFeature From(AgentUsage usage)
        {
            var i = usage.InputTokens is >= 0 ? (decimal?)usage.InputTokens.Value : null;
            var o = usage.OutputTokens is >= 0 ? (decimal?)usage.OutputTokens.Value : null;
            var c = usage.CachedInputTokens is >= 0 ? (decimal?)usage.CachedInputTokens.Value : null;
            var w = usage.CacheWriteInputTokens is >= 0 ? (decimal?)usage.CacheWriteInputTokens.Value : null;
            var hasI = i.HasValue; var hasO = o.HasValue; var hasC = c.HasValue; var hasW = w.HasValue;
            var hasNegative = new[] { usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens }.Any(value => value < 0);
            var valid = !hasNegative && hasI && hasO && (usage.InputIncludesCache || hasC && hasW);
            if (usage.InputIncludesCache && valid && (c ?? 0) + (w ?? 0) > i) valid = false;
            var feature = new CostFeature
            {
                Source = usage.Source, Scope = usage.Scope, Model = usage.Model, IncludesCache = usage.InputIncludesCache,
                HasInput = hasI, HasOutput = hasO, HasCached = hasC, HasWrite = hasW,
                InputPositive = i > 0, OutputPositive = o > 0, CachedPositive = c > 0, WritePositive = w > 0,
                Valid = valid, Count = 1,
                Input = i ?? 0, Output = o ?? 0, Cached = c ?? 0, Write = w ?? 0
            };
            feature.Key = string.Join('|', feature.Source, feature.Scope, feature.IncludesCache, hasI, hasO, hasC, hasW,
                feature.InputPositive, feature.OutputPositive, feature.CachedPositive, feature.WritePositive, valid);
            return feature;
        }

        public void Merge(CostFeature other)
        { Count += other.Count; Input += other.Input; Output += other.Output; Cached += other.Cached; Write += other.Write; }

        public CostFeature Clone() => (CostFeature)MemberwiseClone();

        public bool Estimate(PricingSnapshot pricing, out decimal min, out decimal max, out bool range)
        {
            min = max = 0m; range = false;
            if (!Valid || NormalizeScope(Source, Scope) is not ("turn" or "last-request") && !(Source == "claude.stream-json" && Scope == "thread-total")) return false;
            // Model is resolved by the caller's model bucket. It is supplied through the temporary property below.
            if (!pricing.TryResolve(Model, out var price)) return false;
            try
            {
                if (!IncludesCache)
                {
                    min = Charge(Input, price.Input.Min) + Charge(Cached, price.CachedInput.Min) + Charge(Write, price.CacheWrite.Min) + Charge(Output, price.Output.Min);
                    max = Charge(Input, price.Input.Max) + Charge(Cached, price.CachedInput.Max) + Charge(Write, price.CacheWrite.Max) + Charge(Output, price.Output.Max);
                    range = InputPositive && price.Input.Min != price.Input.Max || CachedPositive && price.CachedInput.Min != price.CachedInput.Max ||
                        WritePositive && price.CacheWrite.Min != price.CacheWrite.Max || OutputPositive && price.Output.Min != price.Output.Max;
                }
                else
                {
                    var remainder = Input - (HasCached ? Cached : 0m) - (HasWrite ? Write : 0m);
                    if (remainder < 0) return false;
                    var minRates = new List<decimal> { price.Input.Min }; var maxRates = new List<decimal> { price.Input.Max };
                    if (!HasCached) { minRates.Add(price.CachedInput.Min); maxRates.Add(price.CachedInput.Max); }
                    if (!HasWrite) { minRates.Add(price.CacheWrite.Min); maxRates.Add(price.CacheWrite.Max); }
                    min = Charge(Cached, price.CachedInput.Min) + Charge(Write, price.CacheWrite.Min) + Charge(remainder, minRates.Min()) + Charge(Output, price.Output.Min);
                    max = Charge(Cached, price.CachedInput.Max) + Charge(Write, price.CacheWrite.Max) + Charge(remainder, maxRates.Max()) + Charge(Output, price.Output.Max);
                    range = CachedPositive && price.CachedInput.Min != price.CachedInput.Max || WritePositive && price.CacheWrite.Min != price.CacheWrite.Max ||
                        OutputPositive && price.Output.Min != price.Output.Max || remainder > 0 && minRates.Min() != maxRates.Max();
                }
                return true;
            }
            catch (OverflowException) { min = max = 0; return false; }
        }

        private static decimal Charge(decimal value, decimal rate) => checked(value * rate / 1_000_000m);
    }
}

public sealed record ActivityStatisticsSnapshot(ActivitySummary Activity, ActivityUsageSummary Usage,
    bool MigratedFromAvailableRetainedHistory, string TimeZoneId);
