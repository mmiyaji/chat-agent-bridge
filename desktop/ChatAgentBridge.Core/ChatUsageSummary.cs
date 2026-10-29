using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core;

/// <summary>Totals over execution records. Missing usage stays unknown instead of counting as zero.</summary>
public sealed record ChatUsageSummary(int Runs, long? InputTokens, long? OutputTokens, AgentCostEstimate? Cost)
{
    public static ChatUsageSummary Summarize(IEnumerable<ExecutionRecord> records)
    {
        var runs = 0;
        long? input = null, output = null;
        decimal? min = null, max = null;
        foreach (var record in records)
        {
            runs++;
            if (record.Usage is not { } usage) continue;
            if (usage.TotalInputTokens is long inputTokens) input = (input ?? 0) + inputTokens;
            if (usage.OutputTokens is long outputTokens) output = (output ?? 0) + outputTokens;
            if (AgentCostEstimator.Estimate(usage) is { } cost)
            {
                min = (min ?? 0) + cost.MinUsd;
                max = (max ?? 0) + cost.MaxUsd;
            }
        }
        return new ChatUsageSummary(runs, input, output, min is { } low && max is { } high ? new AgentCostEstimate(low, high) : null);
    }

    /// <summary>Records started by this requester in this provider on the given local calendar day.</summary>
    public static IEnumerable<ExecutionRecord> ForRequesterOnLocalDay(IEnumerable<ExecutionRecord> records,
        string provider, string userId, DateTime localDay) =>
        records.Where(record => record.Source == provider && record.UserId == userId &&
            record.StartedAt.ToLocalTime().Date == localDay.Date);

    /// <summary>Records of this requester in one conversation thread.</summary>
    public static IEnumerable<ExecutionRecord> ForThread(IEnumerable<ExecutionRecord> records,
        string provider, string channelId, string threadId, string userId) =>
        records.Where(record => record.Source == provider && record.ChannelId == channelId &&
            record.ThreadId == threadId && record.UserId == userId);
}
