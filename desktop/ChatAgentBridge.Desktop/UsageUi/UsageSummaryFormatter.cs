using System.Globalization;
using ChatAgentBridge.Core;

namespace ChatAgentBridge_Desktop.UsageUi;

/// <summary>Formats provider-reported usage without estimating missing values or cost.</summary>
public static class UsageSummaryFormatter
{
    public static string Format(AgentUsage? usage, AgentExecutionMetrics? metrics)
    {
        var lines = new List<string>();
        if (usage is null)
        {
            lines.Add("使用量: 未取得（プロバイダーから報告されていません）");
        }
        else
        {
            lines.Add($"使用量の観測範囲: {ScopeLabel(usage.Scope)} · 提供元: {SourceLabel(usage.Source)}");
            if (!string.IsNullOrWhiteSpace(usage.Model)) lines.Add($"使用量のモデル: {usage.Model}");
            lines.Add(usage.InputIncludesCache
                ? $"入力トークン合計: {Count(usage.TotalInputTokens)}"
                : $"通常入力トークン: {Count(usage.InputTokens)} · 入力トークン合計: {Count(usage.TotalInputTokens)}");
            lines.Add($"出力トークン: {Count(usage.OutputTokens)}");
            lines.Add($"キャッシュ入力トークン: {Count(usage.CachedInputTokens)}");
            lines.Add($"キャッシュ書込入力トークン: {Count(usage.CacheWriteInputTokens)}");
            lines.Add($"キャッシュ再利用率（入力トークン比）: {Percent(usage.CacheHitPercent)}");
        }

        if (metrics is not null)
        {
            var timings = new List<string>();
            if (metrics.ProcessStartMilliseconds is not null) timings.Add($"プロセス起動 {Duration(metrics.ProcessStartMilliseconds)}");
            if (metrics.InitializeMilliseconds is not null) timings.Add($"初期化 {Duration(metrics.InitializeMilliseconds)}");
            if (metrics.QueueMilliseconds is not null) timings.Add($"待ち時間 {Duration(metrics.QueueMilliseconds)}");
            if (metrics.TotalMilliseconds is not null) timings.Add($"合計 {Duration(metrics.TotalMilliseconds)}");
            if (timings.Count > 0) lines.Add($"実行時間: {string.Join(" · ", timings)}");
            lines.Add(metrics.ProcessReused ? "プロセス: 再利用" : "プロセス: 新規起動");
        }

        lines.Add("利用量はCLIの報告値です。料金・購読枠の消費率ではありません。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string ScopeLabel(string? scope) => scope switch
    {
        "turn" => "今回のターン",
        "last-request" => "直近のAPIリクエスト（実行全体ではありません）",
        "thread-total" => "会話累計（過去の実行を含む）",
        _ => string.IsNullOrWhiteSpace(scope) ? "不明" : scope
    };

    private static string ValueOrUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "不明" : value;
    private static string SourceLabel(string? value) => value switch
    {
        "codex.exec" => "Codex CLI",
        "claude.stream-json" => "Claude Code CLI",
        "thread-total-delta" or "thread/tokenUsage/updated:last" => "Codex App Server",
        _ => ValueOrUnknown(value)
    };
    private static string Count(long? value) => value is long count ? count.ToString("N0", CultureInfo.CurrentCulture) : "未取得";
    private static string Percent(double? value) => value is double percent && double.IsFinite(percent)
        ? $"{percent.ToString("N1", CultureInfo.CurrentCulture)}%" : "未取得";
    private static string Duration(double? milliseconds) => milliseconds is double value && double.IsFinite(value) && value >= 0
        ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} ms" : "未取得";
}
