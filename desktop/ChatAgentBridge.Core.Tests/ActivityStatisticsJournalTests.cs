using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class ActivityStatisticsJournalTests
{
    [Fact]
    public async Task OrdinaryUpdatesKeepSnapshotUnchangedAndReplayWithoutPrivateRequestFields()
    {
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        var snapshot = await File.ReadAllTextAsync(data.Snapshot);
        var second = Completed() with
        {
            WorkspaceRoot = "private-workspace", ChannelId = "private-channel",
            UserId = "private-user", ThreadId = "private-thread", SessionId = "private-agent-session"
        };
        await store.RecordAsync(second);
        Assert.Equal(snapshot, await File.ReadAllTextAsync(data.Snapshot));
        var delta = await File.ReadAllTextAsync(data.Journal);
        Assert.DoesNotContain("private-", delta);
        Assert.EndsWith("\n", delta);
        Assert.True(new FileInfo(data.Journal).Length < 2048);

        using var reopened = data.Copy();
        var cold = new ActivityStatisticsStore(reopened.Path);
        await cold.RecordAsync(second); // A retried finalized event must not count twice.
        Assert.Equal(2, (await Read(cold)).Activity.TotalExecutions);
        Assert.Equal(24m, (await Read(cold)).Usage.Totals.TotalTokens);
    }

    [Fact]
    public async Task IncompleteAppendIsIgnoredAndRepairedBeforeNextWrite()
    {
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        await store.RecordAsync(Completed());
        await File.AppendAllTextAsync(data.Journal, "{\"SchemaVersion\":1");
        using var reopened = data.Copy();
        var cold = new ActivityStatisticsStore(reopened.Path);
        Assert.Equal(2, (await Read(cold)).Activity.TotalExecutions);
        await cold.RecordAsync(Completed());
        Assert.Equal(3, (await Read(cold)).Activity.TotalExecutions);
        foreach (var line in await File.ReadAllLinesAsync(reopened.Journal))
            using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public async Task ClearGenerationPreventsOldJournalFromResurrectingClearedUsage()
    {
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        await store.RecordAsync(Completed());
        var oldJournal = await File.ReadAllTextAsync(data.Journal);
        await store.ClearAsync();
        // Simulate a crash/backup restoration leaving the old journal after an atomic checkpoint.
        await File.WriteAllTextAsync(data.Journal, oldJournal);
        using var reopened = data.Copy();
        Assert.Equal(0, (await Read(new ActivityStatisticsStore(reopened.Path))).Activity.TotalExecutions);
    }

    [Fact]
    public async Task CompactionCheckpointsAllEventsAndOldGenerationDoesNotDoubleCount()
    {
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        await store.RecordAsync(Completed());
        var oldJournal = await File.ReadAllTextAsync(data.Journal);
        for (var i = 1; i < ActivityStatisticsStore.MaxJournalEvents; i++)
            await store.RecordAsync(Completed());
        Assert.False(File.Exists(data.Journal));
        await File.WriteAllTextAsync(data.Journal, oldJournal);
        using var reopened = data.Copy();
        Assert.Equal(ActivityStatisticsStore.MaxJournalEvents + 1,
            (await Read(new ActivityStatisticsStore(reopened.Path))).Activity.TotalExecutions);
    }

    [Fact]
    public async Task EventsAppendedAfterACheckpointSurviveAColdRead()
    {
        // The journal generation is derived from the checkpoint twice: from the in-memory document when the
        // snapshot is written, and from the deserialized file on a cold read. They must agree, or a restart
        // would silently discard every delta recorded after a compaction.
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        // The first record writes the snapshot; the next MaxJournalEvents - 1 fill the journal and the one after compacts it.
        for (var i = 0; i < ActivityStatisticsStore.MaxJournalEvents + 1; i++)
            await store.RecordAsync(Completed());
        Assert.False(File.Exists(data.Journal));
        await store.RecordAsync(Completed());
        await store.RecordAsync(Completed());
        Assert.True(File.Exists(data.Journal));

        using var reopened = data.Copy();
        Assert.Equal(ActivityStatisticsStore.MaxJournalEvents + 3,
            (await Read(new ActivityStatisticsStore(reopened.Path))).Activity.TotalExecutions);
    }

    [Fact]
    public async Task FailedAppendDoesNotPolluteCachedAggregatesAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        await store.RecordAsync(Completed());
        var next = Completed();
        using (var locked = new FileStream(data.Journal, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => store.RecordAsync(next));
        Assert.Equal(2, (await Read(store)).Activity.TotalExecutions);
        await store.RecordAsync(next);
        Assert.Equal(3, (await Read(store)).Activity.TotalExecutions);
    }

    [Fact]
    public async Task MalformedCommittedEntryFailsClosedButClearRepairsIt()
    {
        using var data = new TestData();
        var store = new ActivityStatisticsStore(data.Path);
        await store.RecordAsync(Completed());
        await File.WriteAllTextAsync(data.Journal, "{malformed}\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordAsync(Completed()));
        await store.ClearAsync();
        Assert.Equal(0, (await Read(store)).Activity.TotalExecutions);
    }

    private static ExecutionRecord Completed() => new()
    {
        StartedAt = DateTimeOffset.UtcNow, Status = "completed",
        Usage = new AgentUsage { InputTokens = 10, OutputTokens = 2, Scope = "turn", Source = "codex.exec", Model = "test-model" }
    };
    private static Task<ActivityStatisticsSnapshot> Read(ActivityStatisticsStore store) =>
        store.ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);

    private sealed class TestData : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-statistics-journal-" + Guid.NewGuid().ToString("N"));
        public string Snapshot => System.IO.Path.Combine(Path, "activity-statistics.json");
        public string Journal => System.IO.Path.Combine(Path, "activity-statistics.journal.jsonl");
        public TestData() => Directory.CreateDirectory(Path);
        public TestData Copy()
        {
            var copy = new TestData();
            foreach (var file in Directory.EnumerateFiles(Path)) File.Copy(file, System.IO.Path.Combine(copy.Path, System.IO.Path.GetFileName(file)));
            return copy;
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
