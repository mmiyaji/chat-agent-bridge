using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class ActivityUsageSummaryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly End = new(2026, 10, 18);

    [Fact]
    public void Build_EmptyAndInvalidArguments()
    {
        var empty = ActivityUsageSummary.Build([], End, 30, Utc);
        Assert.Equal(30, empty.Days.Count);
        Assert.Empty(empty.Models);
        Assert.Null(empty.Totals.TotalTokens);
        Assert.Equal(0, empty.Totals.ExecutionCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => ActivityUsageSummary.Build([], End, 0, Utc));
        Assert.Throws<ArgumentException>(() => ActivityUsageSummary.Build([], End, 1, Utc, "thread-total"));
    }

    [Fact]
    public void Build_DoesNotGuessLegacyScopeFromExecutionSourceOrMissingModel()
    {
        var wrongSource = Record(End, new AgentUsage { Scope = "thread-total", Source = "other", InputTokens = 999, OutputTokens = 1 })
            with { Source = "claude.stream-json" };
        var records = new[] { wrongSource,
            Record(End, new AgentUsage { Scope = "turn", Model = " ", InputTokens = 5, OutputTokens = 5 }),
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 10, OutputTokens = 10 }) };
        var summary = ActivityUsageSummary.Build(records, End, 1, Utc);
        Assert.Equal(30m, summary.Totals.TotalTokens);
        Assert.Single(summary.Models);
        Assert.Null(summary.Models[0].Model);
        Assert.Equal(1, summary.Totals.ExcludedScopeCount);
    }

    [Fact]
    public void Build_ZeroFillsDaysAndDistinguishesMissingFromReportedZero()
    {
        var records = new[]
        {
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 0, OutputTokens = 0, Model = "m" }),
            Record(End.AddDays(-2), null)
        };

        var result = ActivityUsageSummary.Build(records, End, 3, Utc);

        Assert.Equal(new DateOnly(2026, 10, 16), result.StartDay);
        Assert.Equal(new[] { new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 17), End }, result.Days.Select(x => x.Date));
        Assert.Equal(1, result.Days[0].Totals.ExecutionCount);
        Assert.Null(result.Days[0].Totals.InputTokens);
        Assert.Equal(0m, result.Days[2].Totals.InputTokens);
        Assert.Equal(0m, result.Days[2].Totals.TotalTokens);
        Assert.Equal(1, result.Days[2].Totals.TokenReportedCount);
    }

    [Fact]
    public void Build_NormalizesCachedInclusiveAndExclusiveInputAndComputesWeightedCacheRate()
    {
        var records = new[]
        {
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 100, OutputTokens = 10, CachedInputTokens = 100, InputIncludesCache = true }),
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 700, OutputTokens = 20, CachedInputTokens = 0, CacheWriteInputTokens = 200, InputIncludesCache = false })
        };

        var totals = ActivityUsageSummary.Build(records, End, 1, Utc).Totals;

        Assert.Equal(1000m, totals.InputTokens);
        Assert.Equal(30m, totals.OutputTokens);
        Assert.Equal(100m, totals.CachedInputTokens);
        Assert.Equal(200m, totals.CacheWriteInputTokens);
        Assert.Equal(1030m, totals.TotalTokens);
        Assert.Equal(10d, totals.CacheHitPercent!.Value, 8);
        Assert.Equal(2, totals.TokenReportedCount);
    }

    [Fact]
    public void Build_UsesReportedModelOnlyAndKeepsUnknownModelBucket()
    {
        var records = new[]
        {
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 3, OutputTokens = 2, Model = "reported" }) with { AgentModel = "requested" },
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 4, OutputTokens = 1 }) with { AgentModel = "requested-too" }
        };

        var models = ActivityUsageSummary.Build(records, End, 1, Utc).Models;

        Assert.Equal(2, models.Count);
        Assert.Contains(models, x => x.Model == "reported");
        Assert.Contains(models, x => x.Model is null);
        Assert.DoesNotContain(models, x => x.Model?.StartsWith("requested", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_SeparatesScopesAndCorrectsOnlyLegacyClaudeThreadTotal()
    {
        var records = new[]
        {
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 10, OutputTokens = 1 }),
            Record(End, new AgentUsage { Scope = "last-request", InputTokens = 20, OutputTokens = 2 }),
            Record(End, new AgentUsage { Scope = "thread-total", InputTokens = 30, OutputTokens = 3 }),
            Record(End, new AgentUsage { Scope = "thread-total", Source = "claude.stream-json", InputTokens = 40, OutputTokens = 4 })
        };

        var turn = ActivityUsageSummary.Build(records, End, 1, Utc);
        var request = ActivityUsageSummary.Build(records, End, 1, Utc, "last-request");

        Assert.Equal(55m, turn.Totals.TotalTokens);
        Assert.Equal(2, turn.Totals.ExcludedScopeCount);
        Assert.Equal(3, request.Totals.ExcludedScopeCount);
        Assert.Equal(22m, request.Totals.TotalTokens);
    }

    [Fact]
    public void Build_DeduplicatesIdsAndIncludesFailedRunsButIgnoresOutsideRange()
    {
        var id = Guid.NewGuid();
        var original = Record(End, new AgentUsage { Scope = "turn", InputTokens = 10, OutputTokens = 5 }) with { Id = id, Status = "failed" };
        var duplicate = original with { Usage = original.Usage! with { InputTokens = 100, OutputTokens = 50 } };
        var old = Record(End.AddDays(-2), new AgentUsage { Scope = "turn", InputTokens = 500, OutputTokens = 500 });

        var totals = ActivityUsageSummary.Build(new[] { original, duplicate, old }, End, 1, Utc).Totals;

        Assert.Equal(1, totals.ExecutionCount);
        Assert.Equal(15m, totals.TotalTokens);
    }

    [Fact]
    public void Build_UsesLocalStartDateForTimeZoneBoundary()
    {
        var japan = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo");
        var record = Record(new DateTimeOffset(2026, 10, 17, 15, 30, 0, TimeSpan.Zero),
            new AgentUsage { Scope = "turn", InputTokens = 2, OutputTokens = 3 });

        var result = ActivityUsageSummary.Build(new[] { record }, End, 1, japan);

        Assert.Equal(1, result.Totals.ExecutionCount);
        Assert.Equal(5m, result.Totals.TotalTokens);
    }

    [Fact]
    public void Build_IgnoresNegativeAndIncompletePairsAndLeavesZeroDenominatorCacheUnknown()
    {
        var records = new[]
        {
            Record(End, new AgentUsage { Scope = "turn", InputTokens = -1, OutputTokens = 9, CachedInputTokens = 10 }),
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 5, OutputTokens = null, CachedInputTokens = 8 }),
            Record(End, new AgentUsage { Scope = "turn", InputTokens = 0, OutputTokens = 0, CachedInputTokens = 0 })
        };

        var totals = ActivityUsageSummary.Build(records, End, 1, Utc).Totals;

        Assert.Equal(5m, totals.InputTokens);
        Assert.Equal(9m, totals.OutputTokens);
        Assert.Equal(0m, totals.TotalTokens);
        Assert.Null(totals.CacheHitPercent);
        Assert.Equal(1, totals.TokenReportedCount);
        Assert.Equal(1, totals.CacheReportedCount);
    }

    [Fact]
    public void Build_AccumulatesPastLongMaximumWithoutOverflow()
    {
        var usage = new AgentUsage { Scope = "turn", InputTokens = long.MaxValue, OutputTokens = long.MaxValue };
        var result = ActivityUsageSummary.Build(new[] { Record(End, usage), Record(End, usage) }, End, 1, Utc);

        Assert.Equal((decimal)long.MaxValue * 4, result.Totals.TotalTokens);
    }

    private static ExecutionRecord Record(DateOnly day, AgentUsage? usage) =>
        Record(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), usage);

    private static ExecutionRecord Record(DateTimeOffset startedAt, AgentUsage? usage) => new()
    {
        StartedAt = startedAt,
        Usage = usage,
        AgentModel = "requested-model",
        Status = "completed"
    };
}
