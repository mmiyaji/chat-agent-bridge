using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Normalizes the configured handling of chat messages received during execution.</summary>
public static class ChatFollowUpPolicy
{
    // Configuration commands keep their own confirmation path, even during an active turn.
    public static bool IsConfigurationCommand(string prompt) =>
        WorkspaceChatCommand.TryParse(prompt, out _) || AgentChatCommand.TryParse(prompt, out _);

    public const string Queue = "queue";
    public const string Steer = "steer";

    public static string Normalize(string? value) => value is Steer ? Steer : Queue;

    public static bool CanSteer(BridgeSettings current, BridgeSettings activeEffective,
        SessionKey incoming, SessionKey active) =>
        Normalize(current.ChatFollowUpMode) == Steer &&
        Normalize(activeEffective.ChatFollowUpMode) == Steer &&
        (activeEffective.AgentProvider == AgentProfile.Codex && activeEffective.AgentMode == "app-server" ||
         activeEffective.AgentProvider == AgentProfile.Claude && activeEffective.AgentMode == "exec") &&
        (activeEffective.ChatApprovalMode is "auto" or "first") &&
        incoming == active;
}
