namespace ChatAgentBridge.Core;

/// <summary>Changes to persisted settings, grouped by the settings page that owns them.</summary>
public readonly record struct SettingsDraftChanges(bool Agent, bool Chat, bool General)
{
    public bool HasChanges => Agent || Chat || General;

    public static SettingsDraftChanges Compare(BridgeSettings saved, BridgeSettings draft) =>
        Compare(saved, draft, draft.FileAttachmentMaxSizeMb, draft.FileAttachmentMaxCount,
            draft.FileAttachmentMaxTotalSizeMb);

    // Keep the NumberBox values as doubles: converting an invalid draft to an integer can
    // otherwise make a fractional, out-of-range, or cleared value look already saved.
    public static SettingsDraftChanges Compare(BridgeSettings saved, BridgeSettings draft,
        double attachmentMaxSizeMb, double attachmentMaxCount, double attachmentMaxTotalSizeMb)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(draft);
        var agent = !AgentValues(saved).Equals(AgentValues(draft));
        var chat = !ChatValues(saved).Equals(ChatValues(draft)) ||
            attachmentMaxSizeMb != saved.FileAttachmentMaxSizeMb ||
            attachmentMaxCount != saved.FileAttachmentMaxCount ||
            attachmentMaxTotalSizeMb != saved.FileAttachmentMaxTotalSizeMb;
        return new(agent, chat, saved.AutoConnectProvider != draft.AutoConnectProvider);
    }

    private static object AgentValues(BridgeSettings settings) => (
        settings.WorkspaceRoot.Trim(), settings.AgentExecutable.Trim(), settings.AgentArguments.Trim(),
        settings.AgentMode, settings.AgentProvider, OptionalText(settings.AgentModel),
        OptionalText(settings.AgentReasoningEffort), settings.RequireApproval, settings.AgentPermissionMode,
        settings.AllowAgentNetwork, settings.TrustedWorkspaceRoots);

    private static object ChatValues(BridgeSettings settings) => (
        settings.MattermostServerUrl.Trim(), settings.MattermostChannelId.Trim(), settings.MattermostToken,
        settings.MattermostBypassProxy, settings.PostConnectionNotice, settings.SlackBotToken,
        settings.SlackAppToken, settings.MattermostAllowedUserIds, settings.SlackAllowedUserIds,
        settings.DiscordBotToken, settings.DiscordAllowedUserIds, settings.DiscordAllowedGuildIds,
        settings.DiscordAllowedChannelIds, settings.DiscordAllowedThreadIds, settings.DiscordAllowDirectMessages,
        settings.ShareApprovalsInChat, settings.ShareAgentQuestionsInChat, settings.ChatApprovalMode,
        settings.ChatResultMode, settings.ChatFollowUpMode, settings.ChatProgressDetail, settings.SlackAllowedChannelIds, settings.SlackAllowedThreadIds,
        settings.MattermostAllowedThreadIds, settings.FileAttachmentApprovalMode, settings.AutoApproveSwitchCommands);

    private static string? OptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
