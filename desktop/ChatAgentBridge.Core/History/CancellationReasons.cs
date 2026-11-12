namespace ChatAgentBridge.Core.History;

/// <summary>Why a running chat execution stopped, recorded on the cancelled audit event.</summary>
public static class CancellationReasons
{
    /// <summary>The stop button in this app.</summary>
    public const string UserApp = "user_app";
    /// <summary>A stop reaction or stop command in the chat.</summary>
    public const string UserChat = "user_chat";
    /// <summary>The chat connection failed or ended while running.</summary>
    public const string ConnectionLost = "connection_lost";
    /// <summary>The connection was closed from this app, or replaced by a reconnect.</summary>
    public const string Disconnected = "disconnected";
    /// <summary>The requester lost access or the attachment policy changed.</summary>
    public const string AccessChanged = "access_changed";
    /// <summary>The settings could not be read or contained an invalid workspace.</summary>
    public const string SettingsUnavailable = "settings_unavailable";
    /// <summary>The app was closing.</summary>
    public const string AppClosing = "app_closing";
    /// <summary>
    /// Nothing in this app asked to stop: the agent runner gave up on its own (for example a protocol or
    /// output wait timeout) and surfaced it as a cancellation.
    /// </summary>
    public const string AgentAborted = "agent_aborted";

    public static bool IsKnown(string value) => value is UserApp or UserChat or ConnectionLost or Disconnected or
        AccessChanged or SettingsUnavailable or AppClosing or AgentAborted;
}
