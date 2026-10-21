namespace ChatAgentBridge.Core;

public sealed record BridgeSettings
{
    public string WorkspaceRoot { get; init; } = "";
    public string AgentExecutable { get; init; } = "codex";
    public string AgentArguments { get; init; } = "exec --json -";
    public string AgentMode { get; init; } = "exec";
    public string AgentProvider { get; init; } = AgentProfile.Codex;
    public string? AgentModel { get; init; }
    public string? AgentReasoningEffort { get; init; }
    // Set only after the conversation runner has verified an owner-scoped saved binding.
    // Internal properties are deliberately excluded from persisted settings JSON.
    internal string? AuthorizedCliSessionId { get; init; }
    public string AutoConnectProvider { get; init; } = "none";
    public string MattermostServerUrl { get; init; } = "";
    public string MattermostChannelId { get; init; } = "";
    public string MattermostToken { get; init; } = "";
    public bool MattermostBypassProxy { get; init; }
    public bool PostConnectionNotice { get; init; }
    public string SlackBotToken { get; init; } = "";
    public string SlackAppToken { get; init; } = "";
    public string MattermostAllowedUserIds { get; init; } = "";
    public string SlackAllowedUserIds { get; init; } = "";
    public bool ShareApprovalsInChat { get; init; }
    public bool ShareAgentQuestionsInChat { get; init; }
    public bool RequireApproval { get; init; } = true;
    public string AgentPermissionMode { get; init; } = "inherit";
    public bool AllowAgentNetwork { get; init; }
    public string TrustedWorkspaceRoots { get; init; } = "";
    public string ChatApprovalMode { get; init; } = "first";
    public string ChatResultMode { get; init; } = "manual";
    public string SlackAllowedChannelIds { get; init; } = "";
    public string SlackAllowedThreadIds { get; init; } = "";
    public string MattermostAllowedThreadIds { get; init; } = "";
    public string FileAttachmentApprovalMode { get; init; } = "always";
    public int FileAttachmentMaxSizeMb { get; init; } = 10;
    public int FileAttachmentMaxCount { get; init; } = 3;
    public int FileAttachmentMaxTotalSizeMb { get; init; } = 20;
}
