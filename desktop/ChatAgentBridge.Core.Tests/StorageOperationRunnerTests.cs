using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class StorageOperationRunnerTests
{
    [Fact]
    public async Task RetriesOnlyBoundedSharingViolationsAndRecordsSafeDiagnostics()
    {
        var runner = new StorageOperationRunner();
        var calls = 0;
        var result = await runner.RunAsync("read", _ =>
        {
            if (Interlocked.Increment(ref calls) < 3) throw new IOException("private path/token", 32);
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Contains(StorageOperationDiagnostics.Snapshot(), x => x.Operation == "read" && x.RetryCount == 2 && x.Outcome == "success");
        var diagnostic = StorageOperationDiagnostics.Snapshot().Last(x => x.Operation == "read" && x.RetryCount == 2 && x.Outcome == "success");
        Assert.Equal(2, diagnostic.RetryCount);
        Assert.Equal("success", diagnostic.Outcome);
        Assert.Null(diagnostic.HResult);
        Assert.DoesNotContain("private", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharingViolationRetryCountIsCapped()
    {
        var runner = new StorageOperationRunner();
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("read", _ =>
        {
            calls++;
            throw new IOException("sharing", 33);
        }));

        Assert.Equal(3, calls);
        Assert.Equal(33, error.HResult & 0xffff);
        Assert.Contains(StorageOperationDiagnostics.Snapshot(), x => x.Operation == "read" && x.RetryCount == 2 && x.Outcome == "failure");
    }

    [Fact]
    public async Task DoesNotRetryCorruptionOrUnauthorizedFailures()
    {
        var runner = new StorageOperationRunner();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync("read", _ =>
        {
            calls++;
            throw new InvalidDataException("bad data");
        }));
        Assert.Equal(1, calls);
        calls = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync("write", _ =>
        {
            calls++;
            throw new UnauthorizedAccessException("denied");
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SerializesCallsAndDoesNotAllowEarlierWorkToFinishAfterLaterWork()
    {
        var runner = new StorageOperationRunner();
        var order = new List<int>();
        var first = runner.RunAsync("write", async _ =>
        {
            await Task.Delay(40);
            order.Add(1);
        });
        var second = runner.RunAsync("write", _ => { order.Add(2); return Task.CompletedTask; });
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task DispatchesBlockingActionToWorkerWithoutBlockingCaller()
    {
        var runner = new StorageOperationRunner();
        var callerThread = Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = runner.RunAsync("probe", _ =>
        {
            entered.TrySetResult(Environment.CurrentManagedThreadId);
            release.Task.GetAwaiter().GetResult();
            return Task.CompletedTask;
        });

        var workerThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(callerThread, workerThread);
        release.TrySetResult();
        await operation;
    }
}
