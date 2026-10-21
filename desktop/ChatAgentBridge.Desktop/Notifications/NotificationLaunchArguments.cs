namespace ChatAgentBridge_Desktop.Notifications;

public static class NotificationLaunchArguments
{
    private const string ActivationMarker = "----AppNotificationActivated:";

    public static bool IsNotificationLaunch(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0].StartsWith(ActivationMarker, StringComparison.Ordinal);
}
