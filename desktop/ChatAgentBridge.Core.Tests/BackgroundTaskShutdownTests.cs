using ChatAgentBridge_Desktop.Startup;

namespace ChatAgentBridge.Core.Tests;

public sealed class BackgroundTaskShutdownTests
{
    [Fact]
    public async Task CancelledStartupGateDoesNotDisposeTheServiceSourceBeforeOwnerStopsIt()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CancellationTokenSource();
        var service = Task.Run(async () => await gate.Task);
        gate.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service);
        await BackgroundTaskShutdown.StopAsync(service, source);
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }

    [Fact]
    public async Task StopSignalsCancellationButWaitsForWorkerBeforeDisposal()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CancellationTokenSource();
        var stop = BackgroundTaskShutdown.StopAsync(worker.Task, source);
        Assert.True(source.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        _ = source.Token;
        worker.SetResult();
        await stop;
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }
}
