namespace ChatAgentBridge_Desktop.Startup;

/// <summary>The owner cancels a service and releases its source only after the worker has stopped.</summary>
public static class BackgroundTaskShutdown
{
    public static async Task StopAsync(Task? task, CancellationTokenSource lifetime)
    {
        lifetime.Cancel();
        try
        {
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Background shutdown: {error.GetType().Name}"); }
        finally { lifetime.Dispose(); }
    }
}
