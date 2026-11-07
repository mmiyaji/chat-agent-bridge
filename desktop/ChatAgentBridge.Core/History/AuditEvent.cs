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
