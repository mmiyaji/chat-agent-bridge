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

    /// <summary>Settings that must change before the steer mode can take effect for the chosen provider.</summary>
    public sealed record SteerRequirements(bool ProviderSupported, bool RequiresAppServer, bool RequiresApprovalChange)
    {
        public bool HasChanges => RequiresAppServer || RequiresApprovalChange;
    }

    /// <summary>The approval mode that steer falls back to; it is the least permissive mode that still allows steering.</summary>
    public const string SteerApprovalMode = "first";

    public static SteerRequirements GetSteerRequirements(string? provider, bool useAppServer, string? approvalMode)
    {
        var supported = provider is AgentProfile.Codex or AgentProfile.Claude;
        return new SteerRequirements(
            supported,
            supported && provider == AgentProfile.Codex && !useAppServer,
            supported && approvalMode is not ("auto" or "first"));
    }
}
