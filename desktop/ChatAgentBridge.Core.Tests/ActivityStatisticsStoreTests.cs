using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Pricing;
using System.Text.Json;

namespace ChatAgentBridge.Core.Tests;

public sealed class ActivityStatisticsStoreTests
{
    [Fact]
    public async Task HistoryRollsAt500WhileDurableActivityAndUsageRemainAndFinalRetryIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var history = new ExecutionHistoryStore(directory.Path);
        var first = DateTimeOffset.UtcNow.AddDays(-2);
        ExecutionRecord? last = null;
        for (var i = 0; i < 505; i++)
        {
            last = new ExecutionRecord
            {
                Id = Guid.NewGuid(), StartedAt = first.AddMinutes(i), FinishedAt = first.AddMinutes(i).AddSeconds(1),
                Status = "completed", Usage = new AgentUsage { Model = "model-a", Scope = "turn", InputTokens = 2, OutputTokens = 3 }
            };
            await history.RecordAsync(last);
        }

        Assert.Equal(500, (await new ExecutionHistoryStore(directory.Path).ListAsync()).Count);
        var statistics = new ActivityStatisticsStore(directory.Path);
        var before = await statistics.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(505, before.Activity.TotalExecutions);
        Assert.Equal(505, before.Usage.Totals.ExecutionCount);
        Assert.Equal(2525m, before.Usage.Totals.TotalTokens);

        await new ExecutionHistoryStore(directory.Path).RecordAsync(last!);
        var afterRestart = await new ActivityStatisticsStore(directory.Path)
            .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(505, afterRestart.Activity.TotalExecutions);
        Assert.Equal(2525m, afterRestart.Usage.Totals.TotalTokens);
    }

    [Fact]
    public async Task FirstReadMigratesRetainedHistoryOnceAndClearDoesNotReimportIt()
    {
        using var directory = new TemporaryDirectory();
        var record = new ExecutionRecord
        {
            StartedAt = DateTimeOffset.Now.AddHours(-1), Status = "failed",
            Usage = new AgentUsage { Model = "migration-model", Scope = "turn", InputTokens = 0, OutputTokens = 0 }
        };
        var historyPath = Path.Combine(directory.Path, "history.json");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Records = new[] { record } }));

        var store = new ActivityStatisticsStore(directory.Path);
        var first = await store.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.True(first.MigratedFromAvailableRetainedHistory);
        Assert.Equal(1, first.Activity.Failed);
        await store.MigrateAsync(await new ExecutionHistoryStore(directory.Path).ListAsync());
        Assert.Equal(1, (await store.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local)).Activity.TotalExecutions);

        await store.ClearAsync();
        Assert.Equal(0, (await store.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local)).Activity.TotalExecutions);
    }

    [Fact]
    public async Task DurableUsageMatchesHistoryAggregationForScopesUnknownCountersCacheAndRepricing()
    {
        using var directory = new TemporaryDirectory();
        var zone = TimeZoneInfo.Local;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var now = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), zone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
        var records = new[]
        {
            Record(now, "completed", new AgentUsage { Model = "priced", Scope = "turn", InputTokens = 100, OutputTokens = 10, CachedInputTokens = 20, InputIncludesCache = true }),
            Record(now.AddDays(-1).AddHours(1), "completed", new AgentUsage { Model = "priced", Scope = "turn", InputTokens = 0, OutputTokens = 0, CachedInputTokens = 0, InputIncludesCache = true }),
            Record(now.AddHours(2), "completed", new AgentUsage { Model = "priced", Scope = "last-request", InputTokens = 40, OutputTokens = 5, InputIncludesCache = false, CachedInputTokens = 10, CacheWriteInputTokens = 5 }),
            Record(now.AddHours(3), "completed", new AgentUsage { Model = "priced", Scope = "thread-total", Source = "other", InputTokens = 900, OutputTokens = 1 }),
            Record(now.AddDays(-2), "completed", null)
        };
        var pricing = new PricingSnapshot(day,
            [new ModelPrice("priced", new(1, 2), new(.5m, 1), new(1, 2), new(3, 4))], []);
        var store = new ActivityStatisticsStore(directory.Path);
        await store.MigrateAsync(records);

        foreach (var scope in new[] { "turn", "last-request" })
        {
            var expected = ActivityUsageSummary.Build(records, day, 2, zone, scope, pricing);
            var actual = (await store.ReadAsync(day, 2, zone, scope, pricing)).Usage;
            Assert.Equal(expected.Totals, actual.Totals);
            Assert.Equal(expected.Days, actual.Days);
            Assert.Equal(expected.Models, actual.Models);
        }

        var zero = (await store.ReadAsync(day, 2, zone, "turn", pricing)).Usage.Days[0].Totals;
        Assert.Equal(0m, zero.InputTokens);
        Assert.Equal(0m, zero.TotalTokens);
        Assert.Null((await store.ReadAsync(day.AddDays(-2), 1, zone, "turn", pricing)).Usage.Totals.InputTokens);
    }

    [Fact]
    public async Task ActiveMarkerLimitFailsClosedInsteadOfDroppingLiveExecutions()
    {
        using var directory = new TemporaryDirectory();
        var store = new ActivityStatisticsStore(directory.Path);
        await store.MigrateAsync([]);
        for (var i = 0; i < ActivityStatisticsStore.MaxActiveExecutions; i++)
            await store.RecordAsync(new ExecutionRecord { Id = Guid.NewGuid(), Status = "running" });
        await Assert.ThrowsAsync<IOException>(() => store.RecordAsync(new ExecutionRecord { Id = Guid.NewGuid(), Status = "running" }));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "activity-statistics.json")));
        Assert.Equal(ActivityStatisticsStore.MaxActiveExecutions, document.RootElement.GetProperty("Active").GetArrayLength());
    }

    [Fact]
    public async Task LiveActiveMarkerSurvivesHistoryEvictionAndIsFinalizedAfterTrackingEnds()
    {
        using var directory = new TemporaryDirectory();
        var history = new ExecutionHistoryStore(directory.Path);
        var id = Guid.NewGuid();
        try
        {
            await history.RecordAsync(new ExecutionRecord { Id = id, Status = "running" });
            await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), "{\"SchemaVersion\":1,\"Records\":[]}");

            var running = await new ActivityStatisticsStore(directory.Path)
                .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
            Assert.Equal(1, running.Activity.Running);
            Assert.Equal(1, running.Activity.TotalExecutions);

            history.EndTracking(id);
            var interrupted = await history.ListAsync();
            Assert.Empty(interrupted);
            var finalized = await new ActivityStatisticsStore(directory.Path)
                .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
            Assert.Equal(0, finalized.Activity.Running);
            Assert.Equal(1, finalized.Activity.TotalExecutions);
        }
        finally { history.EndTracking(id); }
    }

    private static ExecutionRecord Record(DateTimeOffset at, string status, AgentUsage? usage) => new()
    {
        StartedAt = at, FinishedAt = at.AddSeconds(1), Status = status, Usage = usage
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-activity-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("cab-activity-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
