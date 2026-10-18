using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge_Desktop.UsageUi;

/// <summary>Formats provider-reported usage without estimating missing values or cost.</summary>
public static class UsageSummaryFormatter
{
    public static string Format(AgentUsage? usage, AgentExecutionMetrics? metrics)
    {
        var lines = new List<string>();
        if (usage is null)
        {
            lines.Add(CoreText.Get("UsageMissing"));
        }
        else
        {
            lines.Add(CoreText.Get("UsageScopeSource", ScopeLabel(usage.Scope), SourceLabel(usage.Source)));
            if (!string.IsNullOrWhiteSpace(usage.Model)) lines.Add(CoreText.Get("UsageModel", usage.Model));
            lines.Add(usage.InputIncludesCache
                ? CoreText.Get("UsageInputTotal", Count(usage.TotalInputTokens))
                : CoreText.Get("UsageInputNormalAndTotal", Count(usage.InputTokens), Count(usage.TotalInputTokens)));
            lines.Add(CoreText.Get("UsageOutputTokens", Count(usage.OutputTokens)));
            lines.Add(CoreText.Get("UsageCachedInputTokens", Count(usage.CachedInputTokens)));
            lines.Add(CoreText.Get("UsageCacheWriteTokens", Count(usage.CacheWriteInputTokens)));
            lines.Add(CoreText.Get("UsageCacheHitRate", Percent(usage.CacheHitPercent)));
        }

        if (metrics is not null)
        {
            var timings = new List<string>();
            if (metrics.ProcessStartMilliseconds is not null) timings.Add(CoreText.Get("UsageProcessStart", Duration(metrics.ProcessStartMilliseconds)));
            if (metrics.InitializeMilliseconds is not null) timings.Add(CoreText.Get("UsageInitialize", Duration(metrics.InitializeMilliseconds)));
            if (metrics.QueueMilliseconds is not null) timings.Add(CoreText.Get("UsageQueue", Duration(metrics.QueueMilliseconds)));
            if (metrics.TotalMilliseconds is not null) timings.Add(CoreText.Get("UsageTotal", Duration(metrics.TotalMilliseconds)));
            if (timings.Count > 0) lines.Add(CoreText.Get("UsageExecutionTime", string.Join(" · ", timings)));
            lines.Add(CoreText.Get(metrics.ProcessReused ? "UsageProcessReused" : "UsageProcessNew"));
        }

        lines.Add(CoreText.Get("UsageDisclaimer"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string ScopeLabel(string? scope) => scope switch
    {
        "turn" => CoreText.Get("UsageScopeTurn"),
        "last-request" => CoreText.Get("UsageScopeLastRequest"),
        "thread-total" => CoreText.Get("UsageScopeThreadTotal"),
        _ => string.IsNullOrWhiteSpace(scope) ? CoreText.Get("Unknown") : scope
    };

    private static string ValueOrUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? CoreText.Get("Unknown") : value;
    private static string SourceLabel(string? value) => value switch
    {
        "codex.exec" => CoreText.Get("CodexCli"),
        "claude.stream-json" => CoreText.Get("ClaudeCodeCli"),
        "thread-total-delta" or "thread/tokenUsage/updated:last" => CoreText.Get("CodexAppServer"),
        _ => ValueOrUnknown(value)
    };
    private static string Count(long? value) => value is long count ? count.ToString("N0", CultureInfo.CurrentCulture) : CoreText.Get("NotReported");
    private static string Percent(double? value) => value is double percent && double.IsFinite(percent)
        ? $"{percent.ToString("N1", CultureInfo.CurrentCulture)}%" : CoreText.Get("NotReported");
    private static string Duration(double? milliseconds) => milliseconds is double value && double.IsFinite(value) && value >= 0
        ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} ms" : CoreText.Get("NotReported");
}
