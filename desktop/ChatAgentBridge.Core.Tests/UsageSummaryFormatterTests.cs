using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.UsageUi;
using System.Globalization;

namespace ChatAgentBridge.Core.Tests;

public sealed class UsageSummaryFormatterTests
{
    [Theory]
    [InlineData("ja-JP", "使用量: 未取得", "料金・購読枠の消費率ではありません", "入力トークン合計: 0")]
    [InlineData("en-US", "Usage: not reported by the provider", "not a measure of cost or subscription allowance consumed", "Total input tokens: 0")]
    public void MissingUsageAndMetricsRemainUnknown(string culture, string missingUsage, string disclaimer, string inventedTotal)
    {
        var result = FormatInCulture(culture, () => UsageSummaryFormatter.Format(null, null));

        Assert.Contains(missingUsage, result);
        Assert.Contains(disclaimer, result);
        Assert.DoesNotContain(inventedTotal, result);
    }

    [Theory]
    [InlineData("ja-JP", "通常入力トークン: 100", "入力トークン合計: 125", "キャッシュ書込入力トークン: 5", "キャッシュ再利用率（入力トークン比）: 16.0%", "直近のAPIリクエスト（実行全体ではありません）")]
    [InlineData("en-US", "Regular input tokens: 100", "Total input tokens: 125", "Cache-write input tokens: 5", "Cache reuse rate (share of input tokens): 16.0%", "Most recent API request (not the entire execution)")]
    public void NonInclusiveInputShowsRawAndComputedTotalSeparately(string culture, string raw, string total, string cacheWrite, string hitRate, string scope)
    {
        var result = FormatInCulture(culture, () => UsageSummaryFormatter.Format(new AgentUsage
        {
            InputTokens = 100,
            CachedInputTokens = 20,
            CacheWriteInputTokens = 5,
            OutputTokens = 12,
            InputIncludesCache = false,
            Scope = "last-request",
            Source = "codex-cli"
        }, null));

        Assert.Contains(raw, result);
        Assert.Contains(total, result);
        Assert.Contains(cacheWrite, result);
        Assert.Contains(hitRate, result);
        Assert.Contains(scope, result);
    }

    [Theory]
    [InlineData("ja-JP", "入力トークン合計: 未取得", "出力トークン: 7", "キャッシュ再利用率（入力トークン比）: 未取得", "合計 1,250 ms", "プロセス: 再利用", "通常入力トークン: 0")]
    [InlineData("en-US", "Total input tokens: Not reported", "Output tokens: 7", "Cache reuse rate (share of input tokens): Not reported", "Total 1,250 ms", "Process: reused", "Regular input tokens: 0")]
    public void PartialUsageAndMetricsDoNotInventMissingCountersOrTimings(
        string culture, string missingTotal, string output, string missingCacheRate, string totalTime, string process, string inventedInput)
    {
        var result = FormatInCulture(culture, () => UsageSummaryFormatter.Format(new AgentUsage { OutputTokens = 7, Scope = "turn" },
            new AgentExecutionMetrics { TotalMilliseconds = 1250, ProcessReused = true }));

        Assert.Contains(missingTotal, result);
        Assert.Contains(output, result);
        Assert.Contains(missingCacheRate, result);
        Assert.Contains(totalTime, result);
        Assert.Contains(process, result);
        Assert.DoesNotContain(inventedInput, result);
    }

    private static string FormatInCulture(string culture, Func<string> format)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var selectedCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = selectedCulture;
            CultureInfo.CurrentUICulture = selectedCulture;
            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
