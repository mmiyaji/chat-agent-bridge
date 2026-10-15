namespace ChatAgentBridge.Core.History;

/// <summary>Allowlisted event metadata. This record has no free-form payload or error field.</summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string Kind,
    Guid? ExecutionId,
    string Source,
    string ChannelId,
    string ThreadId);
