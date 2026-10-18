using System.Collections.Concurrent;
using ChatAgentBridge_Desktop.Startup;

namespace ChatAgentBridge.Core.Tests;

public sealed class StartupOperationGuardTests
{
    [Fact]
    public void TryEnter_AcquiresOnceAndReleasesOnDispose()
    {
        var guard = new StartupOperationGuard();

        var lease = guard.TryEnter();

        Assert.NotNull(lease);
        Assert.Null(guard.TryEnter());

        lease.Dispose();

        using var nextLease = guard.TryEnter();
        Assert.NotNull(nextLease);
    }

    [Fact]
    public void DisposingLeaseTwice_DoesNotReleaseSubsequentLease()
    {
        var guard = new StartupOperationGuard();
        var firstLease = guard.TryEnter();
        Assert.NotNull(firstLease);

        firstLease.Dispose();
        var secondLease = guard.TryEnter();
        Assert.NotNull(secondLease);

        firstLease.Dispose();

        Assert.Null(guard.TryEnter());
        secondLease.Dispose();
        using var nextLease = guard.TryEnter();
        Assert.NotNull(nextLease);
    }

    [Fact]
    public async Task TryEnter_RejectsCallersWhileAsyncOperationHoldsLease()
    {
        var guard = new StartupOperationGuard();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = HoldUntilReleasedAsync();
        await entered.Task;

        Assert.Null(guard.TryEnter());
        release.SetResult();
        await operation;

        using var nextLease = guard.TryEnter();
        Assert.NotNull(nextLease);

        async Task HoldUntilReleasedAsync()
        {
            using var lease = guard.TryEnter();
            Assert.NotNull(lease);
            entered.SetResult();
            await release.Task;
        }
    }

    [Fact]
    public async Task TryEnter_SimultaneousCallersHaveOneWinnerUntilAllAttemptsFinish()
    {
        const int callerCount = 12;
        var guard = new StartupOperationGuard();
        using var ready = new CountdownEvent(callerCount);
        using var attempted = new CountdownEvent(callerCount);
        using var start = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var leases = new ConcurrentBag<IDisposable>();
        var winners = 0;

        var callers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();

                var lease = guard.TryEnter();
                if (lease is not null)
                {
                    Interlocked.Increment(ref winners);
                    leases.Add(lease);
                }

                attempted.Signal();
                release.Wait();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        ready.Wait();
        start.Set();
        attempted.Wait();

        try
        {
            Assert.Equal(1, Volatile.Read(ref winners));
            Assert.Single(leases);
            Assert.Null(guard.TryEnter());
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(callers);

        leases.Single().Dispose();
        using var nextLease = guard.TryEnter();
        Assert.NotNull(nextLease);
    }
}
