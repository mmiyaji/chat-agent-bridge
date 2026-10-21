using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private static IEnumerable<string> SettingsReviewDetails(BridgeSettings saved, BridgeSettings draft)
    {
        var details = new List<string>();
        void Add(string key, object? before, object? after, bool hidden = false)
        {
            if (Equals(before, after)) return;
            var label = DesktopText.Get("SettingsReview" + key);
            details.Add(hidden ? DesktopText.Get("SettingsReviewHidden", label)
                : DesktopText.Get("SettingsReviewChange", label, Value(before), Value(after)));
        }
        static string Value(object? value)
        {
            if (value is bool flag) return DesktopText.Get(flag ? "SettingsReviewOn" : "SettingsReviewOff");
            var text = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text)) return DesktopText.Get("SettingsReviewEmpty");
            // Keep the dialog bounded even for a pasted allowlist. Tokens and CLI
            // command strings never reach this formatter.
            return text.Length <= 4000 ? text : text[..4000] + "…";
        }
        string Approval(string value) => ViewModelText.Get(value switch
        {
            "first" => "ApprovalFirstOnly", "auto" => "ApprovalAutomatic", _ => "ApprovalEveryTime"
        });
        string Permission(string value) => ViewModelText.Get(value switch
        {
            "full-access" => "PermissionFullAccess", "read-only" => "PermissionReadOnly",
            "workspace-write" => "PermissionWorkspaceWrite", "ask" => "PermissionAsk", _ => "PermissionCliDefault"
        });
        Add("Provider", saved.AgentProvider, draft.AgentProvider);
        Add("Command", saved.AgentExecutable, draft.AgentExecutable, hidden: true);
        Add("Arguments", saved.AgentArguments, draft.AgentArguments, hidden: true);
        Add("Mode", saved.AgentMode, draft.AgentMode);
        Add("Permissions", Permission(saved.AgentPermissionMode), Permission(draft.AgentPermissionMode));
        Add("ManualApproval", saved.RequireApproval, draft.RequireApproval);
        Add("Network", saved.AllowAgentNetwork, draft.AllowAgentNetwork);
        Add("Folders", saved.TrustedWorkspaceRoots, draft.TrustedWorkspaceRoots);
        Add("RequestApproval", Approval(saved.ChatApprovalMode), Approval(draft.ChatApprovalMode));
        Add("MattermostUsers", saved.MattermostAllowedUserIds, draft.MattermostAllowedUserIds);
        Add("SlackUsers", saved.SlackAllowedUserIds, draft.SlackAllowedUserIds);
        Add("SlackChannels", saved.SlackAllowedChannelIds, draft.SlackAllowedChannelIds);
        Add("SlackThreads", saved.SlackAllowedThreadIds, draft.SlackAllowedThreadIds);
        Add("MattermostThreads", saved.MattermostAllowedThreadIds, draft.MattermostAllowedThreadIds);
        string Results(string mode) => ViewModelText.Get(mode switch
        {
            "auto" => "ResultAutomatic", "none" => "ResultDoNotShare", _ => "ResultManual"
        });
        Add("Results", Results(saved.ChatResultMode), Results(draft.ChatResultMode));
        Add("Approvals", saved.ShareApprovalsInChat, draft.ShareApprovalsInChat);
        Add("Questions", saved.ShareAgentQuestionsInChat, draft.ShareAgentQuestionsInChat);
        string Attachment(string mode) => MrtText.Get("FileAttachmentMode" + (mode switch { "disabled" => "Disabled", "first" => "First", "auto" => "Auto", _ => "Always" }) + ".Content");
        Add("AttachmentApproval", Attachment(saved.FileAttachmentApprovalMode), Attachment(draft.FileAttachmentApprovalMode));
        Add("AttachmentSize", saved.FileAttachmentMaxSizeMb, draft.FileAttachmentMaxSizeMb);
        Add("AttachmentCount", saved.FileAttachmentMaxCount, draft.FileAttachmentMaxCount);
        Add("AttachmentTotal", saved.FileAttachmentMaxTotalSizeMb, draft.FileAttachmentMaxTotalSizeMb);
        // Server URLs can contain credentials in user-info or query parameters.
        Add("Server", saved.MattermostServerUrl, draft.MattermostServerUrl, hidden: true);
        Add("MattermostChannel", saved.MattermostChannelId, draft.MattermostChannelId);
        Add("Credentials", (saved.MattermostToken, saved.SlackBotToken, saved.SlackAppToken),
            (draft.MattermostToken, draft.SlackBotToken, draft.SlackAppToken), hidden: true);
        return details;
    }
}
