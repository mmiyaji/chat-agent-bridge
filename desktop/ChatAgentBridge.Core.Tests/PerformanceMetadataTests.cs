using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class PerformanceMetadataTests
{
    [Fact]
    public async Task HistoryAtomicReplacementToleratesShortWindowsReaderLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "cab-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExecutionHistoryStore(directory);
            var record = new ExecutionRecord { Status = "completed" };
            await store.RecordAsync(record);
            var reader = new FileStream(Path.Combine(directory, "history.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var update = store.RecordAsync(record with { Status = "failed" });
            try
            {
                await Task.Delay(100);
                Assert.False(update.IsCompleted, "The reader must hold the atomic rename until released.");
            }
            finally { reader.Dispose(); }
            await update.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("failed", Assert.Single(await store.ListAsync()).Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UsagePreservesUnknownAndSeparatesProviderInputConventions()
    {
        var unknown = new AgentUsage();
        Assert.Null(unknown.TotalInputTokens);
        Assert.Null(unknown.CacheHitPercent);
        var codex = new AgentUsage { InputTokens = 100, CachedInputTokens = 75 };
        Assert.Equal(100L, codex.TotalInputTokens);
        Assert.Equal(75d, codex.CacheHitPercent);
        var claude = new AgentUsage { InputIncludesCache = false, InputTokens = 10, CachedInputTokens = 70, CacheWriteInputTokens = 20 };
        Assert.Equal(100L, claude.TotalInputTokens);
        Assert.Equal(70d, claude.CacheHitPercent);
        Assert.Null((claude with { CacheWriteInputTokens = null }).TotalInputTokens);
        Assert.Null((codex with { InputTokens = 0 }).CacheHitPercent);
        Assert.Null((claude with { InputTokens = long.MaxValue }).TotalInputTokens);
    }

    [Fact]
    public async Task HistoryRoundTripsObservedUsageAndRejectsInvalidCounters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-metrics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExecutionHistoryStore(directory);
            var original = new ExecutionRecord
            {
                Status = "completed",
                Usage = new AgentUsage { Scope = "last-request", Source = "codex-app-server", InputTokens = 80, CachedInputTokens = 60 },
                Metrics = new AgentExecutionMetrics { TotalMilliseconds = 400, ProcessReused = true }
            };
            await store.RecordAsync(original);
            var loaded = Assert.Single(await store.ListAsync());
            Assert.Equal(original.Usage, loaded.Usage);
            Assert.Equal(original.Metrics, loaded.Metrics);
            Assert.Null(loaded.Usage!.OutputTokens);
            await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(original with { Usage = original.Usage with { InputTokens = -1 } }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(original with { Metrics = original.Metrics with { TotalMilliseconds = double.PositiveInfinity } }));
            await store.RecordAsync(original with { Usage = null, Metrics = null });
            Assert.Null(Assert.Single(await store.ListAsync()).Usage);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
