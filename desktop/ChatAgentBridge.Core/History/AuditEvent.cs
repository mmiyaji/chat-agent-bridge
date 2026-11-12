namespace ChatAgentBridge.Core.History;

/// <summary>Allowlisted event metadata, optionally including bounded, message-free execution diagnostics.</summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string Kind,
    Guid? ExecutionId,
    string Source,
    string ChannelId,
    string ThreadId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionExceptionDiagnostic? ExecutionDiagnostic { get; init; }

    /// <summary>Who was allowed or refused, by whom and how. Present only on access events.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AuditAccessSubject? Access { get; init; }

    /// <summary>Why a chat request needs approval again (an ApprovalCheck reason code). Present only on approval_required.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovalReason { get; init; }

    /// <summary>What stopped the execution (a CancellationReasons code). Present only on cancelled events.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CancellationReason { get; init; }

    /// <summary>
    /// A short code: the approval method on approved, the connection or stop reason on connection and app events,
    /// the version on app_started, or the changed setting names on settings_changed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    /// <summary>The conditions an execution started under. Present only on started.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AuditExecutionContext? Execution { get; init; }

    /// <summary>Why an execution failed. Present only on failed.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AuditFailure? Failure { get; init; }

    public static bool CanHaveDetail(string kind) => kind is "approved" or "connected" or "reconnecting" or "connection_lost" or
        "disconnected" or "app_started" or "app_stopped" or "settings_changed";

    internal static bool IsValidApprovalReason(string? value) =>
        value is { Length: > 0 and <= 256 } && value.All(c => c is >= 'a' and <= 'z' or '_' or ':' or ',');

    public static bool IsAccessKind(string kind) => kind is "access_requested" or "access_allowed" or
        "access_allowed_once" or "access_denied" or "allowed_user_added" or "allowed_user_removed";
}

/// <summary>
/// The chat user an access event is about. UserId is the provider's immutable ID; DisplayName is
/// informational only. Actor is the local Windows account that made the decision.
/// </summary>
public sealed record AuditAccessSubject(string UserId, string? DisplayName, string Actor, string Method)
{
    public const string ChatRequestMethod = "chat_request";
    public const string SettingsMethod = "settings";

    internal void Validate()
    {
        HistoryFileStore.ValidateText(UserId, nameof(UserId), 128, allowEmpty: false);
        HistoryFileStore.ValidateText(DisplayName, nameof(DisplayName), 256);
        HistoryFileStore.ValidateText(Actor, nameof(Actor), 256);
        if (Method is not (ChatRequestMethod or SettingsMethod))
            throw new ArgumentException("Unsupported access audit method.", nameof(Method));
    }
}
