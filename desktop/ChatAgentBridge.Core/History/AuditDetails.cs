namespace ChatAgentBridge.Core.History;

/// <summary>
/// Message-free context recorded when an execution starts, so a later re-approval or failure can be compared with
/// the conditions it ran under. The workspace is stored as a short digest, never as a path.
/// </summary>
public sealed record AuditExecutionContext(
    string Provider,
    string AgentMode,
    string PermissionMode,
    string ApprovalMode,
    string WorkspaceDigest,
    string? Model = null)
{
    /// <summary>Builds a context that always validates; unexpected values are recorded as "other".</summary>
    public static AuditExecutionContext From(BridgeSettings settings)
    {
        static string Code(string? value) => value is { Length: > 0 and <= 64 } &&
            value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') ? value : "other";
        var model = settings.AgentModel is { Length: > 0 and <= 128 } m && !m.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? m : null;
        return new(Code(AgentProfile.ResolveProvider(settings)), Code(settings.AgentMode), Code(settings.AgentPermissionMode),
            Code(settings.ChatApprovalMode),
            AuditCodes.Digest(Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot))), model);
    }

    internal void Validate()
    {
        AuditCodes.ValidateCode(Provider, nameof(Provider));
        AuditCodes.ValidateCode(AgentMode, nameof(AgentMode));
        AuditCodes.ValidateCode(PermissionMode, nameof(PermissionMode));
        AuditCodes.ValidateCode(ApprovalMode, nameof(ApprovalMode));
        if (WorkspaceDigest is not { Length: 16 } || !WorkspaceDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("Workspace digest must be 16 hexadecimal characters.", nameof(WorkspaceDigest));
        if (Model is not null && (Model.Length is 0 or > 128 || Model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
            throw new ArgumentException("Model must be a short identifier.", nameof(Model));
    }
}

/// <summary>Why an execution failed, without its output. Category is one of the constants below.</summary>
public sealed record AuditFailure(string Category, int? ExitCode = null)
{
    /// <summary>The CLI asked the user to sign in.</summary>
    public const string LoginRequired = "login_required";
    /// <summary>The CLI exited with a nonzero code.</summary>
    public const string ExitCodeCategory = "exit_code";
    /// <summary>The CLI exited normally but reported a failed turn or no result.</summary>
    public const string AgentReported = "agent_reported";

    internal void Validate()
    {
        if (Category is not (LoginRequired or ExitCodeCategory or AgentReported))
            throw new ArgumentException("Unsupported failure category.", nameof(Category));
    }
}

/// <summary>Short machine-readable codes stored in audit details.</summary>
public static class AuditCodes
{
    public const string ApprovedAutomatically = "auto";
    public const string ApprovedInApp = "app";
    public const string ApprovedByNotification = "notification";
    public const string ApprovedInChat = "chat_action";

    public const string ConnectionFirst = "first";
    public const string ConnectionReconnected = "reconnected";
    public const string ConnectionEnded = "ended";
    public const string ConnectionError = "error";
    public const string DisconnectedByUser = "user";
    public const string DisconnectedSettingsChanged = "settings_changed";
    public const string DisconnectedAppClosing = "app_closing";

    public const string StopExit = "exit";
    public const string StopUpdate = "update";
    public const string StopLanguageRestart = "language_restart";

    internal static bool IsValidDetail(string? value) =>
        value is { Length: > 0 and <= 256 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or ',' or ':' or '-');

    internal static void ValidateCode(string? value, string field)
    {
        if (value is not { Length: > 0 and <= 64 } || !value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
            throw new ArgumentException("Audit code must be a short lowercase identifier.", field);
    }

    internal static string Digest(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("audit-v1\0" + value)))[..16].ToLowerInvariant();
}

/// <summary>Names the security-relevant settings that differ, for the settings_changed audit event.</summary>
public static class SettingsAuditChanges
{
    public static IReadOnlyList<string> Describe(BridgeSettings before, BridgeSettings after)
    {
        var changes = new List<string>();
        void Check(string name, object? left, object? right) { if (!Equals(left, right)) changes.Add(name); }
        Check("chat_approval_mode", before.ChatApprovalMode, after.ChatApprovalMode);
        Check("agent_permission_mode", before.AgentPermissionMode, after.AgentPermissionMode);
        Check("agent_provider", before.AgentProvider, after.AgentProvider);
        Check("agent_executable", before.AgentExecutable, after.AgentExecutable);
        Check("agent_arguments", before.AgentArguments, after.AgentArguments);
        Check("agent_mode", before.AgentMode, after.AgentMode);
        Check("workspace", before.WorkspaceRoot, after.WorkspaceRoot);
        Check("trusted_roots", before.TrustedWorkspaceRoots, after.TrustedWorkspaceRoots);
        Check("network", before.AllowAgentNetwork, after.AllowAgentNetwork);
        Check("auto_approve_switch", before.AutoApproveSwitchCommands, after.AutoApproveSwitchCommands);
        Check("file_attachment_approval", before.FileAttachmentApprovalMode, after.FileAttachmentApprovalMode);
        Check("file_attachment_scope", before.FileAttachmentScope, after.FileAttachmentScope);
        Check("allowed_users",
            (before.MattermostAllowedUserIds, before.SlackAllowedUserIds, before.DiscordAllowedUserIds),
            (after.MattermostAllowedUserIds, after.SlackAllowedUserIds, after.DiscordAllowedUserIds));
        Check("credentials",
            (before.MattermostToken, before.SlackBotToken, before.SlackAppToken, before.DiscordBotToken),
            (after.MattermostToken, after.SlackBotToken, after.SlackAppToken, after.DiscordBotToken));
        return changes;
    }
}
