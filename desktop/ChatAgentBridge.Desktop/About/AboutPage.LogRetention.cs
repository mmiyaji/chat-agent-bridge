using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Maintenance;
using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge_Desktop.About;

public sealed partial class AboutPage
{
    private async Task ApplyLogRetentionAsync(LogRetentionPreferences preferences)
    {
        var directory = new SettingsStore().DirectoryPath;
        var failures = await new LogRetentionMaintenance(directory).SaveAndApplyAsync(preferences);
        try { await NotificationDiagnostics.ApplyRetentionAsync(directory, preferences); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new LogRetentionApplyException(); }
        if (failures.Count != 0) throw new LogRetentionApplyException();
    }
}
