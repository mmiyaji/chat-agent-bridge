using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Maintenance;

namespace ChatAgentBridge.Core.Tests;

public sealed class ExecutionHistoryRecoveryTests
{
    [Fact]
    public async Task CompletedResultSurvivesFailedHistoryWriteAndRepairsAfterLockIsReleased()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await store.RecordAsync(new ExecutionRecord { Id = id, StartedAt = startedAt, Status = "running" });
        var terminal = new ExecutionRecord
        {
            Id = id, StartedAt = startedAt, FinishedAt = startedAt.AddMinutes(2), Status = "completed", ExitCode = 0,
            Usage = new AgentUsage { Model = "test-model", Scope = "turn", InputTokens = 123, OutputTokens = 17 }
        };

        Exception? writeError;
        ExecutionRecord lockedView;
        using (OpenHistoryWriteLock(directory.Path))
        {
            writeError = await RecordAndCaptureError(store, terminal);
            Assert.NotNull(writeError);
            lockedView = Assert.Single(await store.ListAsync());
            AssertTerminal(terminal, lockedView);
            var primary = await File.ReadAllTextAsync(HistoryPath(directory.Path));
            Assert.Contains("running", primary, StringComparison.Ordinal);
            Assert.DoesNotContain("completed", primary, StringComparison.Ordinal);
        }
        Assert.NotNull(writeError);
        var pendingPath = PendingPath(directory.Path, id);
        Assert.True(File.Exists(pendingPath));
        var pendingJson = await File.ReadAllTextAsync(pendingPath);
        Assert.DoesNotContain("\"Prompt\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Output\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Message\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"StackTrace\"", pendingJson, StringComparison.Ordinal);
        var pending = ReadPending(pendingJson);
        Assert.Equal("completed", pending.Status);
        Assert.Equal(0, pending.ExitCode);
        Assert.Equal(123, pending.Usage?.InputTokens);
        Assert.NotNull(pending.HistorySaveDiagnostic);
        Assert.Equal(writeError.GetType().Name, pending.HistorySaveDiagnostic.ExceptionType);
        Assert.Equal(unchecked((uint)writeError.HResult).ToString("X8"), pending.HistorySaveDiagnostic.HResult);

        // A fresh store repairs the durable row and consumes the journal after the lock goes away.
        var repaired = Assert.Single(await new ExecutionHistoryStore(directory.Path).ListAsync());
        AssertTerminal(terminal, repaired);
        Assert.NotNull(repaired.HistorySaveDiagnostic);
        Assert.False(File.Exists(pendingPath));

        var activity = await new ActivityStatisticsStore(directory.Path)
            .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(1, activity.Activity.Completed);
        Assert.Equal(123m, activity.Usage.Totals.InputTokens);
        Assert.Equal(17m, activity.Usage.Totals.OutputTokens);
        // Reopening and listing again must not count the recovered event twice.
        var secondActivity = await new ActivityStatisticsStore(directory.Path)
            .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(1, secondActivity.Activity.TotalExecutions);
        Assert.Equal(123m, secondActivity.Usage.Totals.InputTokens);
    }

    [Fact]
    public async Task FailedNonzeroResultIsRepairedBeforeTheNextExecutionIsRecorded()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        var id = Guid.NewGuid();
        await store.RecordAsync(new ExecutionRecord { Id = id, StartedAt = startedAt, Status = "running" });
        var failed = new ExecutionRecord
        {
            Id = id, StartedAt = startedAt, FinishedAt = startedAt.AddMinutes(3), Status = "failed", ExitCode = 23
        };

        await FailTerminalWriteWhileHistoryIsLocked(directory.Path, store, failed);
        var pending = Assert.Single(new DirectoryInfo(Path.Combine(directory.Path, "history-recovery")).GetFiles("*.json"));
        Assert.Equal($"{id:N}.json", pending.Name);

        var next = new ExecutionRecord
        {
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow.AddSeconds(1), Status = "completed", ExitCode = 0
        };
        await new ExecutionHistoryStore(directory.Path).RecordAsync(next);

        var records = await new ExecutionHistoryStore(directory.Path).ListAsync();
        var previous = Assert.Single(records, record => record.Id == id);
        AssertTerminal(failed, previous);
        Assert.NotNull(previous.HistorySaveDiagnostic);
        Assert.Contains(records, record => record.Id == next.Id && record.Status == "completed");
        Assert.False(File.Exists(pending.FullName));
    }

    [Fact]
    public async Task PrimaryTerminalRecordWinsOverStaleJournalForSameId()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var primary = new ExecutionRecord
        {
            Id = Guid.NewGuid(), StartedAt = startedAt, FinishedAt = startedAt.AddSeconds(30), Status = "completed", ExitCode = 0
        };
        await store.RecordAsync(primary);

        var recoveryDirectory = Path.Combine(directory.Path, "history-recovery");
        Directory.CreateDirectory(recoveryDirectory);
        var stale = primary with { Status = "failed", ExitCode = 9, FinishedAt = startedAt.AddMinutes(1) };
        var pendingPath = PendingPath(directory.Path, primary.Id);
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Record = stale }));

        var actual = Assert.Single(await new ExecutionHistoryStore(directory.Path).ListAsync());
        AssertTerminal(primary, actual);
        Assert.False(File.Exists(pendingPath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("oversized")]
    public async Task InvalidPendingJournalFailsClosedAndRemainsForInspection(string invalidKind)
    {
        using var directory = new TemporaryDirectory();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(directory.Path, "history-recovery"));
        var path = PendingPath(directory.Path, id);
        var payload = invalidKind == "corrupt" ? "{" : new string(' ', 128 * 1024 + 1);
        await File.WriteAllTextAsync(path, payload);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionHistoryStore(directory.Path).ListAsync());

        Assert.True(File.Exists(path));
        Assert.Equal(payload.Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task IfBothRecoveryAndPrimaryWritesFailTheRunningRecordIsUntrackedAndDisplaysInterrupted()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var running = new ExecutionRecord { Status = "running" };
        await store.RecordAsync(running);
        var recoveryPath = Path.Combine(directory.Path, "history-recovery");
        await File.WriteAllTextAsync(recoveryPath, "recovery storage unavailable");

        Exception? writeError;
        using (OpenHistoryWriteLock(directory.Path))
            writeError = await RecordAndCaptureError(store, running with
            {
                FinishedAt = running.StartedAt.AddSeconds(1), Status = "failed", ExitCode = 5
            });

        Assert.NotNull(writeError);
        Assert.False(Directory.Exists(recoveryPath));
        var listed = Assert.Single(await new ExecutionHistoryStore(directory.Path).ListAsync());
        Assert.Equal("interrupted", listed.Status);
    }

    [Fact]
    public async Task ClearingHistoryAndUsageRemovesPendingResultsWithoutTouchingUnrelatedFiles()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExecutionHistoryStore(directory.Path);
        var running = new ExecutionRecord { Status = "running" };
        await store.RecordAsync(running);
        var failed = running with
        {
            FinishedAt = running.StartedAt.AddSeconds(1), Status = "failed", ExitCode = 7,
            Usage = new AgentUsage { Model = "test-model", Scope = "turn", InputTokens = 8, OutputTokens = 2 }
        };
        await FailTerminalWriteWhileHistoryIsLocked(directory.Path, store, failed);
        var pendingPath = PendingPath(directory.Path, running.Id);
        Assert.True(File.Exists(pendingPath));
        var unrelatedPath = Path.Combine(directory.Path, "keep.txt");
        await File.WriteAllTextAsync(unrelatedPath, "keep this file");

        await new LocalDataManager(directory.Path).ClearAsync([LocalDataKind.ExecutionHistory, LocalDataKind.UsageStatistics]);

        Assert.False(File.Exists(pendingPath));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(unrelatedPath));
        Assert.Empty(await new ExecutionHistoryStore(directory.Path).ListAsync());
        var statistics = await new ActivityStatisticsStore(directory.Path)
            .ReadAsync(DateOnly.FromDateTime(DateTime.Today), 30, TimeZoneInfo.Local);
        Assert.Equal(0, statistics.Activity.TotalExecutions);
        Assert.Null(statistics.Usage.Totals.InputTokens);
    }

    private static async Task<Exception> FailTerminalWriteWhileHistoryIsLocked(string directory, ExecutionHistoryStore store,
        ExecutionRecord terminal)
    {
        Exception? error;
        using (OpenHistoryWriteLock(directory))
            error = await RecordAndCaptureError(store, terminal);
        Assert.NotNull(error);
        return error;
    }

    private static async Task<Exception?> RecordAndCaptureError(ExecutionHistoryStore store, ExecutionRecord terminal)
    {
        try
        {
            await store.RecordAsync(terminal);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static FileStream OpenHistoryWriteLock(string directory) =>
        new(HistoryPath(directory), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static string HistoryPath(string directory) => Path.Combine(directory, "history.json");
    private static string PendingPath(string directory, Guid id) => Path.Combine(directory, "history-recovery", $"{id:N}.json");

    private static ExecutionRecord ReadPending(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Deserialize<ExecutionRecord>(document.RootElement.GetProperty("Record").GetRawText())!;
    }

    private static void AssertTerminal(ExecutionRecord expected, ExecutionRecord actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.FinishedAt, actual.FinishedAt);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.Usage, actual.Usage);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-history-recovery-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("cab-history-recovery-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
