using ChatAgentBridge.Core.History;

namespace ChatAgentBridge_Desktop;

/// <summary>Records app start and stop in the audit log so gaps in chat handling can be explained later.</summary>
internal static class AppLifecycleAudit
{
    private static int _stopped;

    /// <summary>Set before a planned exit (update or language restart) so the stop event names it.</summary>
    public static string StopReason { get; set; } = AuditCodes.StopExit;

    public static async Task RecordStartedAsync()
    {
        var version = new string(Deployment.DeploymentRuntime.GetDisplayVersion().ToLowerInvariant()
            .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-').Take(64).ToArray());
        await AppendAsync("app_started", version.Length > 0 ? version : "unknown");
    }

    public static Task RecordStoppedAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return Task.CompletedTask;
        return AppendAsync("app_stopped", StopReason);
    }

    /// <summary>For exits that do not return (process restart): waits briefly instead of awaiting on the UI thread.</summary>
    public static void RecordStoppedBeforeRestart(string reason)
    {
        StopReason = reason;
        try { Task.Run(RecordStoppedAsync).Wait(TimeSpan.FromMilliseconds(500)); }
        catch (Exception) { }
    }

    private static async Task AppendAsync(string kind, string detail)
    {
        try
        {
            await new AuditStore().AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, null, "local", "", "") { Detail = detail })
                .ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
