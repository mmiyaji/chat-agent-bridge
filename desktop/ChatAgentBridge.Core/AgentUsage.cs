namespace ChatAgentBridge.Core;

/// <summary>Provider-reported counters; absent values stay unknown, never estimated.</summary>
public sealed record AgentUsage
{
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public bool InputIncludesCache { get; init; } = true;
    public string Scope { get; init; } = "last-request";
    public string Source { get; init; } = "";
    public string? Model { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public long? TotalInputTokens
    {
        get
        {
            if (InputIncludesCache) return InputTokens;
            if (InputTokens is not long input || CachedInputTokens is not long cached || CacheWriteInputTokens is not long written) return null;
            try { return checked(input + cached + written); }
            catch (OverflowException) { return null; }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public double? CacheHitPercent => TotalInputTokens is long total && total > 0 && CachedInputTokens is long cached && cached >= 0 && cached <= total
        ? 100d * cached / total : null;
}

public sealed record AgentExecutionMetrics
{
    public double? ProcessStartMilliseconds { get; init; }
    public double? InitializeMilliseconds { get; init; }
    public double? TotalMilliseconds { get; init; }
    public double? QueueMilliseconds { get; init; }
    public bool ProcessReused { get; init; }
}
