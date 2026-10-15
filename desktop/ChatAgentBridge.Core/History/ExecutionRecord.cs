namespace ChatAgentBridge.Core.History;

/// <summary>Durable execution metadata only. Prompt, output, tokens, and error text are intentionally absent.</summary>
public sealed record ExecutionRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public string Source { get; init; } = "local";
    public string ChannelId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string WorkspaceRoot { get; init; } = "";
    public string Status { get; init; } = "running";
    public int? ExitCode { get; init; }
    public string? SessionId { get; init; }
}
