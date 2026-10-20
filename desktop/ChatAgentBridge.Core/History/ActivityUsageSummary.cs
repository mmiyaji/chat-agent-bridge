namespace ChatAgentBridge.Core.History;

/// <summary>Immutable, timezone-aware token usage aggregated from execution history.</summary>
public sealed record ActivityUsageSummary
{
    private ActivityUsageSummary(DateOnly startDay, DateOnly endDay, string scope,
        IReadOnlyList<ActivityUsageDay> days, IReadOnlyList<ActivityUsageModel> models,
        ActivityUsageTotals totals)
    {
        StartDay = startDay;
        EndDay = endDay;
        Scope = scope;
        Days = days;
        Models = models;
        Totals = totals;
    }

    public DateOnly StartDay { get; }
    public DateOnly EndDay { get; }
    public string Scope { get; }
    public IReadOnlyList<ActivityUsageDay> Days { get; }
    public IReadOnlyList<ActivityUsageModel> Models { get; }
    public ActivityUsageTotals Totals { get; }

    public static ActivityUsageSummary Build(IEnumerable<ExecutionRecord> records, DateOnly endDay,
        int dayCount, TimeZoneInfo zone, string scope = "turn")
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(zone);
        if (dayCount <= 0) throw new ArgumentOutOfRangeException(nameof(dayCount), "Day count must be positive.");
        if (scope is not ("turn" or "last-request")) throw new ArgumentException("Scope must be 'turn' or 'last-request'.", nameof(scope));

        var startDay = endDay.AddDays(1 - dayCount);
        var dayBuckets = Enumerable.Range(0, dayCount)
            .Select(i => new DayBucket(startDay.AddDays(i))).ToArray();
        var modelBuckets = new Dictionary<string, UsageBucket>(StringComparer.Ordinal);
        var total = new UsageBucket();
        var seen = new HashSet<Guid>();

        foreach (var record in records)
        {
            if (!seen.Add(record.Id)) continue;
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.StartedAt, zone).DateTime);
            if (localDate < startDay || localDate > endDay) continue;

            var day = dayBuckets[localDate.DayNumber - startDay.DayNumber];
            day.Bucket.ExecutionCount++;
            total.ExecutionCount++;

            var usage = record.Usage;
            if (usage is null) continue;
            var effectiveScope = NormalizeScope(usage);
            if (effectiveScope != scope)
            {
                day.Bucket.ExcludedScopeCount++;
                total.ExcludedScopeCount++;
                continue;
            }

            var modelKey = string.IsNullOrWhiteSpace(usage.Model) ? null : usage.Model;
            if (!modelBuckets.TryGetValue(modelKey ?? "\0", out var modelBucket))
            {
                modelBucket = new UsageBucket();
                modelBuckets.Add(modelKey ?? "\0", modelBucket);
            }

            day.Bucket.Add(usage);
            modelBucket.ExecutionCount++;
            modelBucket.Add(usage);
            total.Add(usage);
        }

        var days = Array.AsReadOnly(dayBuckets.Select(d => new ActivityUsageDay(d.Date, d.Bucket.ToTotals())).ToArray());
        var models = Array.AsReadOnly(modelBuckets
            .Select(kv => new ActivityUsageModel(kv.Key == "\0" ? null : kv.Key, kv.Value.ToTotals()))
            .OrderByDescending(m => m.Totals.TotalTokens ?? decimal.MinValue)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToArray());
        return new ActivityUsageSummary(startDay, endDay, scope, days, models, total.ToTotals());
    }

    // Older versions mislabeled Claude result.usage, which is per turn, as thread-total.
    private static string NormalizeScope(AgentUsage usage) =>
        usage.Source == "claude.stream-json" && usage.Scope == "thread-total" ? "turn" : usage.Scope;

    private sealed class DayBucket(DateOnly date)
    {
        public DateOnly Date { get; } = date;
        public UsageBucket Bucket { get; } = new();
    }

    private sealed class UsageBucket
    {
        private decimal _input;
        private decimal _output;
        private decimal _cached;
        private decimal _cacheWrite;
        private decimal _pairedTotal;
        private decimal _cacheNumerator;
        private decimal _cacheDenominator;
        private bool _hasInput;
        private bool _hasOutput;
        private bool _hasCached;
        private bool _hasCacheWrite;
        private bool _hasPairedTotal;
        private bool _hasCachePair;
        private decimal _costMin;
        private decimal _costMax;
        private bool _costOverflowed;

        public int ExecutionCount { get; set; }
        public int TokenReportedCount { get; private set; }
        public int CacheReportedCount { get; private set; }
        public int UsageReportedCount { get; private set; }
        public int ExcludedScopeCount { get; set; }
        public int CostEstimatedCount { get; private set; }
        public int CostRangeCount { get; private set; }

        public void Add(AgentUsage usage)
        {
            UsageReportedCount++;
            AddCost(usage);

            var input = GetNormalizedInput(usage);
            var output = NonNegative(usage.OutputTokens);
            if (input is decimal inputValue)
            {
                AddCounter(ref _input, ref _hasInput, inputValue);
                var cached = NonNegative(usage.CachedInputTokens);
                var write = NonNegative(usage.CacheWriteInputTokens);
                if (cached > inputValue) cached = null;
                if (write > inputValue) write = null;
                if (cached is decimal cachedValue) AddCounter(ref _cached, ref _hasCached, cachedValue);
                if (write is decimal writeValue) AddCounter(ref _cacheWrite, ref _hasCacheWrite, writeValue);

                if (cached is decimal validCached && validCached <= inputValue)
                {
                    _cacheNumerator += validCached;
                    _cacheDenominator += inputValue;
                    _hasCachePair = true;
                    CacheReportedCount++;
                }
            }
            if (output is decimal outputValue) AddCounter(ref _output, ref _hasOutput, outputValue);

            if (input is decimal pairedInput && output is decimal pairedOutput)
            {
                _pairedTotal += pairedInput + pairedOutput;
                _hasPairedTotal = true;
                TokenReportedCount++;
            }
        }

        private void AddCost(AgentUsage usage)
        {
            var estimate = AgentCostEstimator.Estimate(usage);
            if (estimate is null) return;
            CostEstimatedCount++;
            if (estimate.MinUsd != estimate.MaxUsd) CostRangeCount++;
            if (_costOverflowed) return;
            try
            {
                _costMin = checked(_costMin + estimate.MinUsd);
                _costMax = checked(_costMax + estimate.MaxUsd);
            }
            catch (OverflowException) { _costOverflowed = true; }
        }

        public ActivityUsageTotals ToTotals() => new(
            _hasInput ? _input : null,
            _hasOutput ? _output : null,
            _hasCached ? _cached : null,
            _hasCacheWrite ? _cacheWrite : null,
            _hasPairedTotal ? _pairedTotal : null,
            _hasCachePair && _cacheDenominator > 0 ? (double)(100m * _cacheNumerator / _cacheDenominator) : null,
            ExecutionCount, TokenReportedCount, CacheReportedCount, UsageReportedCount, ExcludedScopeCount)
        {
            Cost = new ActivityCostTotals(CostEstimatedCount > 0 && !_costOverflowed ? _costMin : null,
                CostEstimatedCount > 0 && !_costOverflowed ? _costMax : null, CostEstimatedCount, CostRangeCount)
        };

        private static decimal? GetNormalizedInput(AgentUsage usage)
        {
            if (usage.InputIncludesCache) return NonNegative(usage.InputTokens);

            // InputTokens is the uncached portion for this representation; include both cache buckets once.
            var input = NonNegative(usage.InputTokens);
            var cached = NonNegative(usage.CachedInputTokens);
            var write = NonNegative(usage.CacheWriteInputTokens);
            if (input is not decimal i || cached is not decimal c || write is not decimal w) return null;
            return i + c + w;
        }

        private static decimal? NonNegative(long? value) => value is >= 0 ? value.Value : null;

        private static void AddCounter(ref decimal total, ref bool hasValue, decimal value)
        {
            total += value;
            hasValue = true;
        }
    }
}

public sealed record ActivityUsageDay(DateOnly Date, ActivityUsageTotals Totals);

public sealed record ActivityUsageModel(string? Model, ActivityUsageTotals Totals);

public sealed record ActivityUsageTotals(
    decimal? InputTokens,
    decimal? OutputTokens,
    decimal? CachedInputTokens,
    decimal? CacheWriteInputTokens,
    decimal? TotalTokens,
    double? CacheHitPercent,
    int ExecutionCount,
    int TokenReportedCount,
    int CacheReportedCount,
    int UsageReportedCount,
    int ExcludedScopeCount)
{
    public ActivityCostTotals Cost { get; init; } = new(null, null, 0, 0);
}

public sealed record ActivityCostTotals(decimal? MinUsd, decimal? MaxUsd, int EstimatedCount, int RangeCount);
