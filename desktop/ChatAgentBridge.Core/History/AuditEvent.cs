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
}
