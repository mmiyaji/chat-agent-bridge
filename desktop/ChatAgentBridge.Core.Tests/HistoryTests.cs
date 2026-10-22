using System.Text.Json;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class HistoryTests
{
    [Theory]
    [InlineData("workspace_changed")]
    [InlineData("model_changed")]
    [InlineData("follow_up_applied")]
    [InlineData("follow_up_uncertain")]
    public async Task ConfigurationChangeAuditCanBeRecordedAndReloaded(string kind)
    {
        using var directory = new TemporaryDirectory();
        var record = new AuditEvent(DateTimeOffset.UtcNow, kind, null, "slack", "C1", "T1");
        await new AuditStore(directory.Path).AppendAsync(record);
        Assert.Equal(record, Assert.Single(await new AuditStore(directory.Path).ListAsync()));
    }

    [Fact]
    public async Task ExecutionHistory_ReplacesByIdAndSurvivesReload()
    {
        using var directory = new TemporaryDirectory();
        var id = Guid.NewGuid();
        var store = new ExecutionHistoryStore(directory.Path);
        await store.RecordAsync(new ExecutionRecord { Id = id, StartedAt = DateTimeOffset.UtcNow, Status = "running", Source = "slack" });
        Assert.Null(Assert.Single(await store.ListAsync()).AgentReasoningEffort);
        var updated = new ExecutionRecord
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            Status = "completed", ExitCode = 0, Source = "slack", ChannelId = "C1", ThreadId = "T1",
            AgentReasoningEffort = "future-level"
        };
        await store.RecordAsync(updated);

        var reloaded = await new ExecutionHistoryStore(directory.Path).ListAsync();
        var only = Assert.Single(reloaded);
        Assert.Equal(id, only.Id);
        Assert.Equal("completed", only.Status);
        Assert.Equal("C1", only.ChannelId);
        Assert.Equal("future-level", only.AgentReasoningEffort);
    }

    [Fact]
    public async Task ExecutionHistory_IsBoundedToNewestFiveHundred()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-501);
        for (var index = 0; index < 501; index++)
            await store.RecordAsync(new ExecutionRecord { StartedAt = start.AddMinutes(index), Status = "completed" });

        var records = await store.ListAsync();
        Assert.Equal(500, records.Count);
        Assert.Equal(start.AddMinutes(500), records[0].StartedAt);
        Assert.DoesNotContain(records, record => record.StartedAt == start);
    }

    [Fact]
    public async Task AuditStore_IsBoundedAndReloadsInNewestFirstOrder()
    {
        using var directory = new TemporaryDirectory();
        var store = new AuditStore(directory.Path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-501);
        for (var index = 0; index < 501; index++)
            await store.AppendAsync(new AuditEvent(start.AddMinutes(index), "received", null, "local", "", ""));

        var events = await new AuditStore(directory.Path).ListAsync();
        Assert.Equal(500, events.Count);
        Assert.Equal(start.AddMinutes(500), events[0].Timestamp);
        Assert.DoesNotContain(events, item => item.Timestamp == start);
    }

    [Fact]
    public async Task AuditStore_CachedDocumentFollowsExternalFileChanges()
    {
        using var directory = new TemporaryDirectory();
        var store = new AuditStore(directory.Path);
        var first = new AuditEvent(DateTimeOffset.UtcNow.AddMinutes(-2), "received", null, "local", "", "");
        await store.AppendAsync(first);
        Assert.Single(await store.ListAsync());

        var path = Path.Combine(directory.Path, "audit.json");
        var external = new AuditEvent(DateTimeOffset.UtcNow.AddMinutes(-1), "failed", null, "slack", "C-external-channel", "T1");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Records = new[] { first, external, external with { Timestamp = external.Timestamp.AddSeconds(1) } }
        }));
        Assert.Equal(3, (await store.ListAsync()).Count);

        // An append after the external edit must build on the edited file, not on the stale parse.
        await store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "started", null, "local", "", ""));
        Assert.Equal(4, (await new AuditStore(directory.Path).ListAsync()).Count);

        File.Delete(path);
        Assert.Empty(await store.ListAsync());
        await File.WriteAllTextAsync(path, "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
    }

    [Fact]
    public async Task AuditStore_ListedRecordsCannotChangeCachedState()
    {
        using var directory = new TemporaryDirectory();
        var store = new AuditStore(directory.Path);
        await store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "received", null, "local", "", ""));
        var listed = (AuditEvent[])await store.ListAsync();
        listed[0] = listed[0] with { Kind = "failed" };
        Assert.Equal("received", Assert.Single(await store.ListAsync()).Kind);
    }

    [Fact]
    public async Task Stores_SerializeConcurrentInstancesForSameFile()
    {
        using var directory = new TemporaryDirectory();
        var histories = Enumerable.Range(0, 4).Select(_ => new ExecutionHistoryStore(directory.Path)).ToArray();
        var audits = Enumerable.Range(0, 4).Select(_ => new AuditStore(directory.Path)).ToArray();
        var historyWrites = Enumerable.Range(0, 40).Select(index => histories[index % histories.Length].RecordAsync(
            new ExecutionRecord { StartedAt = DateTimeOffset.UtcNow.AddTicks(index), Status = "completed" }));
        var auditWrites = Enumerable.Range(0, 40).Select(index => audits[index % audits.Length].AppendAsync(
            new AuditEvent(DateTimeOffset.UtcNow.AddTicks(index), "started", null, "local", "", "")));
        await Task.WhenAll(historyWrites.Concat(auditWrites));

        Assert.Equal(40, (await new ExecutionHistoryStore(directory.Path).ListAsync()).Count);
        Assert.Equal(40, (await new AuditStore(directory.Path).ListAsync()).Count);
    }

    [Fact]
    public async Task RunningExecution_RemainsRunningWhileLiveAndStaleRecordDisplaysInterrupted()
    {
        using var directory = new TemporaryDirectory();
        var live = new ExecutionRecord { Status = "running" };
        var store = new ExecutionHistoryStore(directory.Path);
        await store.RecordAsync(live);
        Assert.Equal("running", Assert.Single(await store.ListAsync()).Status);

        var staleId = Guid.NewGuid();
        var staleJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Records = new[] { new ExecutionRecord { Id = staleId, Status = "running" } }
        });
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), staleJson);
        Assert.Equal("interrupted", Assert.Single(await new ExecutionHistoryStore(directory.Path).ListAsync()).Status);
    }

    [Fact]
    public async Task EndTracking_DisplaysPersistedRunningRecordAsInterruptedWithoutWritingFile()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var record = new ExecutionRecord { Status = "running" };
        await store.RecordAsync(record);
        var path = Path.Combine(directory.Path, "history.json");
        var before = await File.ReadAllBytesAsync(path);

        store.EndTracking(record.Id);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var listed = Assert.Single(await store.ListAsync());
        Assert.Equal("interrupted", listed.Status);
    }

    [Fact]
    public async Task Stores_RejectInvalidStatusEventAndFreeformPayloadFields()
    {
        using var directory = new TemporaryDirectory();
        var history = new ExecutionHistoryStore(directory.Path);
        var audit = new AuditStore(directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => history.RecordAsync(new ExecutionRecord { Status = "error: secret prompt" }));
        await Assert.ThrowsAsync<ArgumentException>(() => audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "failed: stack trace", null, "local", "", "")));

        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"),
            """{"SchemaVersion":1,"Records":[],"Prompt":"private prompt"}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => history.ListAsync());

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "audit.json"),
            """{"SchemaVersion":1,"Records":[{"Timestamp":"2026-01-01T00:00:00+00:00","Kind":"received","ExecutionId":null,"Source":"local","ChannelId":"","ThreadId":"","Payload":"private output"}]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => audit.ListAsync());
    }

    [Fact]
    public async Task Stores_RejectCorruptJsonAndUnsupportedSchema()
    {
        using var directory = new TemporaryDirectory();
        var history = new ExecutionHistoryStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => history.ListAsync());

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), "{\"SchemaVersion\":99,\"Records\":[]}");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => history.ListAsync());
        Assert.Contains("version 99", error.Message);
    }

    [Fact]
    public async Task Stores_RejectMissingSchemaCollectionsAndNullRecordsExplicitly()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var history = new ExecutionHistoryStore(directory.Path);
        var audit = new AuditStore(directory.Path);

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), "{\"Records\":[]}");
        var missingHistorySchema = await Assert.ThrowsAsync<InvalidDataException>(() => history.ListAsync());
        Assert.Contains("missing its schema", missingHistorySchema.Message);

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "audit.json"), "{\"SchemaVersion\":1}");
        await Assert.ThrowsAsync<InvalidDataException>(() => audit.ListAsync());

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "history.json"), "{\"SchemaVersion\":1,\"Records\":[null]}");
        var nullHistoryRecord = await Assert.ThrowsAsync<InvalidDataException>(() => history.ListAsync());
        Assert.Contains("null record", nullHistoryRecord.Message);

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "audit.json"), "{\"SchemaVersion\":1,\"Records\":[null]}");
        var nullAuditRecord = await Assert.ThrowsAsync<InvalidDataException>(() => audit.ListAsync());
        Assert.Contains("null record", nullAuditRecord.Message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
