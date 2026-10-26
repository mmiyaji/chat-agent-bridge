using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Maintenance;
using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge_Desktop;

public partial class App
{
    private readonly CancellationTokenSource _logRetentionLifetime = new();

    private async Task RunLogRetentionAsync(string directory)
    {
        try
        {
            if (Window is MainWindow window) await window.StartupReady.WaitAsync(_logRetentionLifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                try
                {
                    _ = await new LogRetentionMaintenance(directory).ApplyAsync(_logRetentionLifetime.Token);
                    var preferences = await new LogRetentionPreferencesStore(directory).LoadAsync(_logRetentionLifetime.Token);
                    await NotificationDiagnostics.ApplyRetentionAsync(directory, preferences, _logRetentionLifetime.Token);
                }
                catch (OperationCanceledException) when (_logRetentionLifetime.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { System.Diagnostics.Debug.WriteLine($"Log retention deferred: {error.GetType().Name}"); }
            } while (await timer.WaitForNextTickAsync(_logRetentionLifetime.Token));
        }
        catch (OperationCanceledException) when (_logRetentionLifetime.IsCancellationRequested || Window is MainWindow startup && startup.StartupReady.IsCanceled) { }
    }
}
