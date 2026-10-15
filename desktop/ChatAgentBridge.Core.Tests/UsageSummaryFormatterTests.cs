using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.UsageUi;

namespace ChatAgentBridge.Core.Tests;

public sealed class UsageSummaryFormatterTests
{
    [Fact]
    public void MissingUsageAndMetricsRemainUnknown()
    {
        var result = UsageSummaryFormatter.Format(null, null);

        Assert.Contains("使用量: 未取得", result);
        Assert.Contains("料金・購読枠の消費率ではありません", result);
        Assert.DoesNotContain("入力トークン合計: 0", result);
    }

    [Fact]
    public void NonInclusiveInputShowsRawAndComputedTotalSeparately()
    {
        var result = UsageSummaryFormatter.Format(new AgentUsage
        {
            InputTokens = 100,
            CachedInputTokens = 20,
            CacheWriteInputTokens = 5,
            OutputTokens = 12,
            InputIncludesCache = false,
            Scope = "last-request",
            Source = "codex-cli"
        }, null);

        Assert.Contains("通常入力トークン: 100", result);
        Assert.Contains("入力トークン合計: 125", result);
        Assert.Contains("キャッシュ書込入力トークン: 5", result);
        Assert.Contains("キャッシュ再利用率（入力トークン比）: 16.0%", result);
        Assert.Contains("直近のAPIリクエスト（実行全体ではありません）", result);
    }

    [Fact]
    public void PartialUsageAndMetricsDoNotInventMissingCountersOrTimings()
    {
        var result = UsageSummaryFormatter.Format(new AgentUsage { OutputTokens = 7, Scope = "turn" },
            new AgentExecutionMetrics { TotalMilliseconds = 1250, ProcessReused = true });

        Assert.Contains("入力トークン合計: 未取得", result);
        Assert.Contains("出力トークン: 7", result);
        Assert.Contains("キャッシュ再利用率（入力トークン比）: 未取得", result);
        Assert.Contains("合計 1,250 ms", result);
        Assert.Contains("プロセス: 再利用", result);
    }
}
