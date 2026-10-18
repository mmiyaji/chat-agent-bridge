namespace ChatAgentBridge.Core.History;

/// <summary>Durable execution metadata and usage counters. Prompts, output, credentials, and error text are absent.</summary>
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
    public string? AgentModel { get; init; }
    public string? AgentReasoningEffort { get; init; }
    public AgentUsage? Usage { get; init; }
    public AgentExecutionMetrics? Metrics { get; init; }
}
