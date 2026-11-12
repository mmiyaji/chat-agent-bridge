namespace ChatAgentBridge.Core.Sessions;

/// <summary>Result of a chat approval decision. Reason is a short code that never contains message text or paths.</summary>
public sealed record ApprovalCheck(bool Required, string? Reason)
{
    public const string StaleBinding = "stale_binding";
    public const string SwitchCommand = "switch_command";
    public const string AlwaysMode = "always_mode";
    public const string NewConversation = "new_conversation";
    /// <summary>The binding exists but holds no approval: never succeeded, or cleared by a policy or agent change.</summary>
    public const string NoPriorApproval = "no_prior_approval";
    /// <summary>Approved before per-item digests were recorded, so the changed item cannot be named.</summary>
    public const string ChangedUnknown = "changed:unknown";
    /// <summary>Every item matches but the combined fingerprint differs (fingerprint format change).</summary>
    public const string ChangedFormat = "changed:fingerprint_format";
    public const string ChangedPrefix = "changed:";
    public const string AgentSwitchPending = "agent_switch_pending";

    public static ApprovalCheck NotRequired { get; } = new(false, null);
    public static ApprovalCheck Require(string reason) => new(true, reason);
}
