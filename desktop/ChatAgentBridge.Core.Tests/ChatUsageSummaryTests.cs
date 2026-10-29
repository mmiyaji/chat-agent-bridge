using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatUsageSummaryTests
{
    [Fact]
    public void MissingUsageStaysUnknown()
    {
        var summary = ChatUsageSummary.Summarize([new ExecutionRecord(), new ExecutionRecord()]);
        Assert.Equal(2, summary.Runs);
        Assert.Null(summary.InputTokens);
        Assert.Null(summary.OutputTokens);
        Assert.Null(summary.Cost);
    }

    [Fact]
    public void SumsTokensAcrossRecordsWithUsage()
    {
        var usage = new AgentUsage { InputTokens = 100, OutputTokens = 10, Model = "unpriced-model-xyz" };
        var summary = ChatUsageSummary.Summarize([new ExecutionRecord { Usage = usage }, new ExecutionRecord { Usage = usage }, new ExecutionRecord()]);
        Assert.Equal(3, summary.Runs);
        Assert.Equal(200, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Null(summary.Cost);
    }

    [Fact]
    public void FiltersByThreadAndLocalDay()
    {
        var now = DateTimeOffset.Now;
        var mine = new ExecutionRecord { Source = "slack", ChannelId = "c", ThreadId = "t", UserId = "u", StartedAt = now };
        var otherThread = mine with { Id = Guid.NewGuid(), ThreadId = "x" };
        var otherUser = mine with { Id = Guid.NewGuid(), UserId = "v" };
        var yesterday = mine with { Id = Guid.NewGuid(), StartedAt = now.AddDays(-2) };
        var all = new[] { mine, otherThread, otherUser, yesterday };

        Assert.Equal(new[] { mine.Id, yesterday.Id }.Order(), ChatUsageSummary.ForThread(all, "slack", "c", "t", "u").Select(r => r.Id).Order());
        Assert.Equal(2, ChatUsageSummary.ForRequesterOnLocalDay(all, "slack", "u", now.LocalDateTime).Count());
    }
}
