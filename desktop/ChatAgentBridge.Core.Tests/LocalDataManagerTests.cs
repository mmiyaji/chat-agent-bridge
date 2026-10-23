using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Maintenance;

namespace ChatAgentBridge.Core.Tests;

public sealed class LocalDataManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-data-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RemovingConversationDataPreservesSettingsAndUnrelatedFiles()
    {
        Directory.CreateDirectory(_directory);
        foreach (var name in new[] { "sessions.json", "conversation-summaries.json", "attachment-consents.json", "chat-identities.json", "settings.json", "keep.txt" })
            await File.WriteAllTextAsync(Path.Combine(_directory, name), "sentinel");
        await new LocalDataManager(_directory).ClearAsync([LocalDataKind.Conversations]);
        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_directory, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_directory, "sessions.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "attachment-consents.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "chat-identities.json")));
    }

    [Fact]
    public async Task RemovingDetailsKeepsMigratedStatistics()
    {
        var store = new ExecutionHistoryStore(_directory);
        await store.RecordAsync(new ExecutionRecord { Status = "completed", FinishedAt = DateTimeOffset.UtcNow });
        await new LocalDataManager(_directory).ClearAsync([LocalDataKind.ExecutionHistory]);
        Assert.Empty(await store.ListAsync());
        var statistics = await new ActivityStatisticsStore(_directory).ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local, "turn", null);
        Assert.Equal(1, statistics.Activity.TotalExecutions);
    }

    [Fact]
    public async Task RemovingStatisticsDoesNotReimportRetainedHistory()
    {
        var history = new ExecutionHistoryStore(_directory);
        await history.RecordAsync(new ExecutionRecord { Status = "completed" });
        await history.RecordAsync(new ExecutionRecord { Status = "completed" });
        var journal = Path.Combine(_directory, "activity-statistics.journal.jsonl");
        Assert.True(File.Exists(journal));
        await new LocalDataManager(_directory).ClearAsync([LocalDataKind.UsageStatistics]);
        Assert.False(File.Exists(journal));
        Assert.Equal(2, (await history.ListAsync()).Count);
        var statistics = new ActivityStatisticsStore(_directory);
        await statistics.MigrateAsync(await new ExecutionHistoryStore(_directory).ListAsync());
        Assert.Equal(0, (await statistics.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local, "turn", null)).Activity.TotalExecutions);
    }

    [Fact]
    public async Task RemovingDetailsAndStatisticsCanRecoverAnUnreadableStatisticsFile()
    {
        await new ExecutionHistoryStore(_directory).RecordAsync(new ExecutionRecord { Status = "completed" });
        await File.WriteAllTextAsync(Path.Combine(_directory, "activity-statistics.json"), "broken JSON");
        await new LocalDataManager(_directory).ClearAsync([LocalDataKind.ExecutionHistory, LocalDataKind.UsageStatistics]);
        Assert.Empty(await new ExecutionHistoryStore(_directory).ListAsync());
        var snapshot = await new ActivityStatisticsStore(_directory).ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(0, snapshot.Activity.TotalExecutions);
    }

    [Fact]
    public async Task DiagnosticsCleanupIsLimitedToKnownFiles()
    {
        var path = Path.Combine(_directory, "diagnostics"); Directory.CreateDirectory(path);
        foreach (var name in new[] { "notification-events-123.jsonl", "notification-events-123.jsonl.bak", "keep.json", "notification-events-secret.jsonl" })
            await File.WriteAllTextAsync(Path.Combine(path, name), "sentinel");
        await new LocalDataManager(_directory).ClearAsync([LocalDataKind.Diagnostics]);
        Assert.False(File.Exists(Path.Combine(path, "notification-events-123.jsonl")));
        Assert.True(File.Exists(Path.Combine(path, "keep.json")));
        Assert.True(File.Exists(Path.Combine(path, "notification-events-secret.jsonl")));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("cab-data-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
