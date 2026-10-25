namespace ChatAgentBridge.Core;

/// <summary>Privacy and execution changes requiring review before settings are persisted.</summary>
/// <remarks>The result contains categories only; it never contains credentials or setting values.</remarks>
public readonly record struct SettingsSensitiveChanges(
    bool CliCommand,
    bool AgentPermissions,
    bool TrustedWorkspaces,
    bool ChatAccess,
    bool ChatSharing,
    bool FileAttachments,
    bool ConnectionIdentity)
{
    public bool HasChanges => CliCommand || AgentPermissions || TrustedWorkspaces || ChatAccess ||
        ChatSharing || FileAttachments || ConnectionIdentity;

    public static bool Equivalent(BridgeSettings left, BridgeSettings right) => !Compare(left, right).HasChanges;

    public static SettingsSensitiveChanges Compare(BridgeSettings saved, BridgeSettings draft)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(draft);
        return new(
            (saved.AgentExecutable.Trim(), saved.AgentArguments.Trim(), saved.AgentProvider, saved.AgentMode) !=
                (draft.AgentExecutable.Trim(), draft.AgentArguments.Trim(), draft.AgentProvider, draft.AgentMode),
            (saved.AgentPermissionMode, saved.AllowAgentNetwork, saved.RequireApproval) !=
                (draft.AgentPermissionMode, draft.AllowAgentNetwork, draft.RequireApproval),
            saved.TrustedWorkspaceRoots != draft.TrustedWorkspaceRoots,
            (saved.ChatApprovalMode, saved.SlackAllowedChannelIds, saved.SlackAllowedThreadIds,
                saved.MattermostAllowedThreadIds, saved.MattermostAllowedUserIds, saved.SlackAllowedUserIds,
                saved.DiscordAllowedUserIds, saved.DiscordAllowedGuildIds, saved.DiscordAllowedChannelIds,
                saved.DiscordAllowedThreadIds, saved.DiscordAllowDirectMessages, saved.AutoApproveSwitchCommands) !=
                (draft.ChatApprovalMode, draft.SlackAllowedChannelIds, draft.SlackAllowedThreadIds,
                draft.MattermostAllowedThreadIds, draft.MattermostAllowedUserIds, draft.SlackAllowedUserIds,
                draft.DiscordAllowedUserIds, draft.DiscordAllowedGuildIds, draft.DiscordAllowedChannelIds,
                draft.DiscordAllowedThreadIds, draft.DiscordAllowDirectMessages, draft.AutoApproveSwitchCommands),
            (saved.ChatResultMode, saved.ShareApprovalsInChat, saved.ShareAgentQuestionsInChat) !=
                (draft.ChatResultMode, draft.ShareApprovalsInChat, draft.ShareAgentQuestionsInChat) ||
            saved.ChatFollowUpMode != draft.ChatFollowUpMode,
            (saved.FileAttachmentApprovalMode, saved.FileAttachmentMaxSizeMb, saved.FileAttachmentMaxCount,
                saved.FileAttachmentMaxTotalSizeMb) !=
                (draft.FileAttachmentApprovalMode, draft.FileAttachmentMaxSizeMb, draft.FileAttachmentMaxCount,
                draft.FileAttachmentMaxTotalSizeMb),
            (saved.MattermostServerUrl.Trim(), saved.MattermostChannelId.Trim(), saved.MattermostToken,
                saved.SlackBotToken, saved.SlackAppToken) !=
                (draft.MattermostServerUrl.Trim(), draft.MattermostChannelId.Trim(), draft.MattermostToken,
                draft.SlackBotToken, draft.SlackAppToken) ||
            !string.Equals(saved.DiscordBotToken, draft.DiscordBotToken, StringComparison.Ordinal));
    }
}
