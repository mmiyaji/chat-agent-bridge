using ChatAgentBridge.Core.History;
using System.Threading.Channels;

namespace ChatAgentBridge.Core.Tests;

public sealed class ReceivedAuditQueueTests
{
    [Fact]
    public async Task Queue_BackpressuresAtCapacityAndWritesInEnqueueOrder()
    {
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<AuditEvent>();
        var writeGate = new object();
        await using var queue = new ReceivedAuditQueue(async (batch, token) =>
        {
            writerEntered.TrySetResult();
            await releaseWriter.Task.WaitAsync(token);
            lock (writeGate) written.AddRange(batch);
        }, capacity: 1, batchSize: 8);

        var start = DateTimeOffset.UtcNow;
        var first = Event(start, "C1");
        var second = Event(start.AddTicks(1), "C2");
        var third = Event(start.AddTicks(2), "C3");
        await queue.EnqueueAsync(first);
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(second);
        var thirdEnqueue = queue.EnqueueAsync(third).AsTask();
        Assert.False(thirdEnqueue.IsCompleted);

        releaseWriter.TrySetResult();
        await thirdEnqueue.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.CompleteAndDrainAsync(TimeSpan.FromSeconds(1));

        lock (writeGate) Assert.Equal(new[] { first, second, third }, written);
    }

    [Fact]
    public async Task Queue_CancelsBlockedWriterAfterBoundedDrainDeadline()
    {
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new ReceivedAuditQueue(async (_, token) =>
        {
            writerEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await queue.EnqueueAsync(Event(DateTimeOffset.UtcNow, "C1"));
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await queue.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Queue_CancelsBlockedEnqueueAndDoesNotWriteAfterShutdown()
    {
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var queue = new ReceivedAuditQueue(async (_, token) =>
        {
            Interlocked.Increment(ref writes);
            writerEntered.TrySetResult();
            await releaseWriter.Task.WaitAsync(token);
        }, capacity: 1, batchSize: 1);

        await queue.EnqueueAsync(Event(DateTimeOffset.UtcNow, "C1"));
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(Event(DateTimeOffset.UtcNow.AddTicks(1), "C2"));
        using var connection = new CancellationTokenSource();
        var blockedEnqueue = queue.EnqueueAsync(Event(DateTimeOffset.UtcNow.AddTicks(2), "C3"), connection.Token).AsTask();
        Assert.False(blockedEnqueue.IsCompleted);
        connection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedEnqueue);

        releaseWriter.TrySetResult();
        Assert.True(await queue.CompleteAndDrainAsync(TimeSpan.FromSeconds(1)));
        var completedWrites = Volatile.Read(ref writes);
        await Assert.ThrowsAsync<ChannelClosedException>(() => queue.EnqueueAsync(Event(DateTimeOffset.UtcNow, "C4")).AsTask());
        await queue.DisposeAsync();

        Assert.Equal(2, completedWrites);
        Assert.Equal(completedWrites, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task Queue_ReportsManyBatchFailuresOnlyOnceAndDisposeReusesShutdown()
    {
        var failureCount = 0;
        await using var queue = new ReceivedAuditQueue((_, _) => Task.FromException(new IOException("private path")),
            _ => Interlocked.Increment(ref failureCount), batchSize: 1);

        for (var index = 0; index < 100; index++)
            await queue.EnqueueAsync(Event(DateTimeOffset.UtcNow.AddTicks(index), $"C{index}"));

        Assert.True(await queue.CompleteAndDrainAsync(TimeSpan.FromSeconds(1)));
        await queue.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref failureCount));
    }

    [Fact]
    public void Queue_RejectsNullStoreBeforeConstructingWorker()
    {
        Assert.Throws<ArgumentNullException>(() => new ReceivedAuditQueue((AuditStore)null!));
    }

    private static AuditEvent Event(DateTimeOffset timestamp, string channelId) =>
        new(timestamp, "received", null, "local", channelId, "");
}
