using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentCostEstimatorTests
{
    [Fact]
    public void CatalogHasCheckedDateAndPricesKnownInclusiveAndExclusiveCounters()
    {
        Assert.Equal(new DateOnly(2026, 10, 20), AgentCostEstimator.PriceCheckedOn);
        var inclusive = Estimate("gpt-5.3-codex", 1000, 500, 200, 100, true);
        var exclusive = Estimate("gpt-5.3-codex", 700, 500, 200, 100, false);

        Assert.Equal(new AgentCostEstimate(0.008435m, 0.008435m), inclusive);
        Assert.Equal(inclusive, exclusive);
    }

    [Fact]
    public void InclusiveInputBoundsUnknownCacheCountersAndKeepsCatalogRanges()
    {
        var unknownWrite = Estimate("gpt-5.4", 100, 1, 20, null, true);
        var unknownCaches = Estimate("gpt-5.4", 100, 0, null, null, true);
        var longContext = Estimate("gpt-5.4", 300_000, 300_000, 0, 0, true);
        var ttlRange = Estimate("claude-sonnet-4-5", 100, 10, 20, 30, false);

        Assert.Equal(new AgentCostEstimate(0.000220m, 0.0004325m), unknownWrite);
        Assert.Equal(new AgentCostEstimate(0.000025m, 0.0005m), unknownCaches);
        Assert.Equal(new AgentCostEstimate(5.25m, 8.25m), longContext);
        Assert.Equal(new AgentCostEstimate(0.0005685m, 0.000636m), ttlRange);
    }

    [Fact]
    public void RejectsUnknownModelsInvalidCountersAndUnsupportedScopes()
    {
        Assert.Null(Estimate("opus", 1, 1, 0, 0, true));
        Assert.Null(Estimate("sonnet", 1, 1, 0, 0, true));
        Assert.Null(Estimate("default", 1, 1, 0, 0, true));
        Assert.Null(Estimate("gpt-5.4", 1, null, 0, 0, true));
        Assert.Null(Estimate("gpt-5.4", 1, 1, -1, 0, true));
        Assert.Null(Estimate("gpt-5.4", 1, 1, 2, 0, true));
        Assert.Null(Estimate("gpt-5.4", null, 1, 0, 0, true));
        Assert.Null(Estimate("gpt-5.4", 1, null, 0, 0, false));
        Assert.Null(AgentCostEstimator.Estimate(Usage("gpt-5.4", 1, 1, 0, 0, true) with { Scope = "thread-total" }));
        Assert.Null(AgentCostEstimator.Estimate(Usage("gpt-5.4", 1, 1, 0, 0, true) with { Scope = "thread-total", Source = "other" }));
        Assert.Equal(new AgentCostEstimate(0m, 0m), Estimate("gpt-5.4", 0, 0, 0, 0, true));
    }

    [Fact]
    public void AcceptsOnlyValidDatedSuffixesForCatalogModels()
    {
        Assert.Equal(Estimate("gpt-5.4", 10, 10, 0, 0, true), Estimate("gpt-5.4-2026-10-20", 10, 10, 0, 0, true));
        Assert.Equal(Estimate("claude-sonnet-4-5", 10, 10, 0, 0, true), Estimate("claude-sonnet-4-5-20261020", 10, 10, 0, 0, true));
        Assert.Null(Estimate("gpt-5.4-2026-99-99", 10, 10, 0, 0, true));
        Assert.Null(Estimate("gpt-5.4-v2", 10, 10, 0, 0, true));
    }

    [Fact]
    public void SummaryAggregatesOnlyPricedIncludedDeduplicatedUsageByDayAndModel()
    {
        var day = new DateOnly(2026, 10, 20);
        var priced = Record(day, Usage("gpt-5.3-codex", 1000, 500, 200, 100, true));
        var pricedRange = Record(day, Usage("gpt-5.4", 100, 1, 20, null, true));
        var unknown = Record(day, Usage("opus", 500, 20, 0, 0, true));
        var excluded = Record(day, Usage("gpt-5.3-codex", 10, 10, 0, 0, true) with { Scope = "last-request" });
        var missing = Record(day, null);
        var duplicate = priced with { Id = priced.Id, Usage = Usage("gpt-5.3-codex", 10_000, 10_000, 0, 0, true) };

        var result = ActivityUsageSummary.Build([priced, pricedRange, unknown, excluded, missing, duplicate], day, 1, TimeZoneInfo.Utc);

        Assert.Equal(5, result.Totals.ExecutionCount);
        Assert.Equal(1, result.Totals.ExcludedScopeCount);
        Assert.Equal(3, result.Totals.UsageReportedCount);
        Assert.Equal(2, result.Totals.Cost.EstimatedCount);
        Assert.Equal(1, result.Totals.Cost.RangeCount);
        Assert.Equal(0.008655m, result.Totals.Cost.MinUsd);
        Assert.Equal(0.0088675m, result.Totals.Cost.MaxUsd);
        Assert.Equal(1, Assert.Single(result.Models, x => x.Model == "gpt-5.3-codex").Totals.Cost.EstimatedCount);
        Assert.Equal(0, Assert.Single(result.Models, x => x.Model == "opus").Totals.Cost.EstimatedCount);
        Assert.Equal(result.Totals.Cost, Assert.Single(result.Days).Totals.Cost);
    }

    [Fact]
    public void SummaryUsesRequestedScopeAndSupportsOldSerializedTotalsWithoutCost()
    {
        var day = new DateOnly(2026, 10, 20);
        var turn = Record(day, Usage("gpt-5.3-codex", 100, 10, 0, 0, true));
        var request = Record(day, Usage("gpt-5.3-codex", 200, 20, 0, 0, true) with { Scope = "last-request" });
        var legacyClaude = Record(day, Usage("claude-sonnet-4-5", 100, 10, 0, 0, true) with
        { Scope = "thread-total", Source = "claude.stream-json" });

        var turnTotals = ActivityUsageSummary.Build([turn, request, legacyClaude], day, 1, TimeZoneInfo.Utc).Totals;
        var requestTotals = ActivityUsageSummary.Build([turn, request, legacyClaude], day, 1, TimeZoneInfo.Utc, "last-request").Totals;
        var oldJson = """{"InputTokens":null,"OutputTokens":null,"CachedInputTokens":null,"CacheWriteInputTokens":null,"TotalTokens":null,"CacheHitPercent":null,"ExecutionCount":0,"TokenReportedCount":0,"CacheReportedCount":0,"UsageReportedCount":0,"ExcludedScopeCount":0}""";
        var deserialized = JsonSerializer.Deserialize<ActivityUsageTotals>(oldJson);

        Assert.Equal(2, turnTotals.Cost.EstimatedCount);
        Assert.Equal(1, requestTotals.Cost.EstimatedCount);
        Assert.Equal(new ActivityCostTotals(null, null, 0, 0), deserialized!.Cost);
    }

    private static AgentCostEstimate? Estimate(string model, long? input, long? output,
        long? cached, long? write, bool inclusive) => AgentCostEstimator.Estimate(new AgentUsage
    {
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CachedInputTokens = cached,
        CacheWriteInputTokens = write,
        InputIncludesCache = inclusive,
        Scope = "turn"
    });

    private static AgentUsage Usage(string model, long? input, long? output, long? cached, long? write, bool inclusive) => new()
    {
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CachedInputTokens = cached,
        CacheWriteInputTokens = write,
        InputIncludesCache = inclusive,
        Scope = "turn"
    };

    private static ExecutionRecord Record(DateOnly day, AgentUsage? usage) => new()
    {
        StartedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
        Usage = usage,
        Status = "completed"
    };
}
