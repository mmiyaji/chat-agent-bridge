using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Maintenance;

namespace ChatAgentBridge.Core.Tests;

public sealed class LogRetentionMaintenanceTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Path { get; }
        public TemporaryDirectory()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, "cab-retention-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            if (System.IO.Path.GetDirectoryName(Path)?.TrimEnd(System.IO.Path.DirectorySeparatorChar) != _root.TrimEnd(System.IO.Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public async Task PruningDetailsMigratesStatisticsAndPreservesUnrelatedFiles()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var record = new ExecutionRecord { StartedAt = now.AddDays(-10), FinishedAt = now.AddDays(-10).AddMinutes(1), Status = "completed" };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, Records = new[] { record } }));
        var unrelated = Path.Combine(directory.Path, "audit-segment-my-document.jsonl");
        await File.WriteAllTextAsync(unrelated, "keep this document");
        var preferences = new LogRetentionPreferences { HistoryRetentionDays = 1, StatisticsRetentionDays = 365 };
        Assert.Empty(await new LogRetentionMaintenance(directory.Path).SaveAndApplyAsync(preferences));
        Assert.Empty(await new ExecutionHistoryStore(directory.Path).ListAsync());
        var summary = await new ActivityStatisticsStore(directory.Path).ReadAsync(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime), 30, TimeZoneInfo.Local);
        Assert.Equal(1, summary.Activity.TotalExecutions);
        Assert.Equal("keep this document", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(preferences, await new LogRetentionPreferencesStore(directory.Path).LoadAsync());
    }

    [Fact]
    public async Task CorruptPolicyPreventsCleanupAndCategoryFailureIsReportedAfterSave()
    {
        using var directory = new TemporaryDirectory();
        var history = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(history, "invalid history kept as evidence");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "log-retention.json"), "invalid policy");
        var maintenance = new LogRetentionMaintenance(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => maintenance.ApplyAsync());
        Assert.Equal("invalid history kept as evidence", await File.ReadAllTextAsync(history));
        var preferences = new LogRetentionPreferences();
        Assert.Contains("history", await maintenance.SaveAndApplyAsync(preferences));
        Assert.Equal(preferences, await new LogRetentionPreferencesStore(directory.Path).LoadAsync());
        Assert.Equal("invalid history kept as evidence", await File.ReadAllTextAsync(history));
    }
}
