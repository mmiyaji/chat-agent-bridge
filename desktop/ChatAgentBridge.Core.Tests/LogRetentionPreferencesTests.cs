using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class LogRetentionPreferencesTests
{
    [Fact]
    public async Task PreferencesRoundTripAndRejectInvalidOrCorruptData()
    {
        using var directory = new TemporaryDirectory();
        var store = new LogRetentionPreferencesStore(directory.Path);
        Assert.Equal(1825, (await store.LoadAsync()).StatisticsRetentionDays);
        var value = new LogRetentionPreferences { HistoryRetentionDays = 12 };
        await store.SaveAsync(value);
        Assert.Equal(12, (await new LogRetentionPreferencesStore(directory.Path).LoadAsync()).HistoryRetentionDays);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(value with { HistoryMaxRecords = 20 }));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "log-retention.json"), "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LogRetentionPreferencesStore(directory.Path).LoadAsync());
    }

    [Fact]
    public async Task VersionOnePreferencesRequireEveryFieldAndNeverDefaultMissingRetentionValues()
    {
        var sparseDocuments = new[]
        {
            "{\"SchemaVersion\":1,\"Preferences\":{}}",
            """
            {"SchemaVersion":1,"Preferences":{
              "StatisticsRetentionDays":1825,"AuditRetentionDays":365,"AuditSegmentSizeMb":4,
              "AuditMaxSegments":16,"HistoryMaxRecords":500,"ConversationRetentionDays":30,
              "DiagnosticsRetentionDays":7,"DiagnosticsFileKiB":128,"DiagnosticsMaxFiles":4
            }}
            """
        };
        foreach (var sparse in sparseDocuments)
        {
            using var directory = new TemporaryDirectory();
            var oldRecord = new ExecutionRecord
            {
                Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddDays(-200),
                FinishedAt = DateTimeOffset.UtcNow.AddDays(-200).AddMinutes(1), Status = "completed"
            };
            var historyPath = Path.Combine(directory.Path, "history.json");
            await File.WriteAllTextAsync(historyPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Records = new[] { oldRecord }
            }));
            var originalHistory = await File.ReadAllBytesAsync(historyPath);
            await new AuditStore(directory.Path).AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "completed", null, "local", "", ""));
            var originalAuditFiles = Directory.GetFiles(directory.Path, "audit-*")
                .ToDictionary(path => path, File.ReadAllBytes);
            await File.WriteAllTextAsync(Path.Combine(directory.Path, "log-retention.json"), sparse);

            await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionHistoryStore(directory.Path).ApplyRetentionAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(directory.Path).ApplyRetentionAsync());
            Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
            foreach (var (path, bytes) in originalAuditFiles) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task HistoryRetentionDoesNotRemoveAggregatedStatistics()
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        await new LogRetentionPreferencesStore(directory.Path).SaveAsync(new LogRetentionPreferences
        {
            HistoryRetentionDays = 1,
            StatisticsRetentionDays = 30
        });
        var old = new ExecutionRecord
        {
            Id = Guid.NewGuid(), StartedAt = now.AddDays(-10), FinishedAt = now.AddDays(-10).AddMinutes(1),
            Status = "completed", AgentModel = "retained-model"
        };
        var history = new ExecutionHistoryStore(directory.Path, clock);
        await history.RecordAsync(old);
        Assert.Empty(await history.ListAsync());
        var result = await new ActivityStatisticsStore(directory.Path, clock)
            .ReadAsync(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime), 30, TimeZoneInfo.Local);
        Assert.Equal(1, result.Activity.TotalExecutions);
    }

    [Fact]
    public async Task CorruptPreferencesNeverTriggerHistoryCleanup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(path, "existing history");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "log-retention.json"), "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionHistoryStore(directory.Path).ApplyRetentionAsync());
        Assert.Equal("existing history", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnavailableDataPathDoesNotFallBackToDefaults()
    {
        using var directory = new TemporaryDirectory();
        var notADirectory = Path.Combine(directory.Path, "data-root");
        await File.WriteAllTextAsync(notADirectory, "occupied");
        var store = new LogRetentionPreferencesStore(notADirectory);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());

        var prefsPath = Path.Combine(directory.Path, "log-retention.json");
        await File.WriteAllTextAsync(prefsPath, "{} ");
        using var locked = new FileStream(prefsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new LogRetentionPreferencesStore(directory.Path).LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
