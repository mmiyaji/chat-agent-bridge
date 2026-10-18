using ChatAgentBridge.Core.History;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.History;

public sealed record ActivityUsageBar(string Label, string ValueLabel, double Percent, string Details, string Marker)
{
    public double Height => Percent * .96;
    public string AccessibleLabel => DesktopText.Get("UsageBarLabel", Label, ValueLabel, Details);
}
public sealed record ActivityUsageDayChoice(DateOnly Date, ActivityUsageTotals Totals)
{
    public string Label => Date.ToString("d");
}

public sealed partial class HistoryPageViewModel
{
    private ActivityUsageSummary _usage = ActivityUsageSummary.Build([], DateOnly.FromDateTime(DateTime.Today), 90, TimeZoneInfo.Local);
    public IReadOnlyList<ActivityUsageBar> UsageDaily { get; private set; } = [];
    public IReadOnlyList<ActivityUsageBar> UsageModels { get; private set; } = [];
    public IReadOnlyList<ActivityUsageDayChoice> UsageDays { get; private set; } = [];
    [ObservableProperty] public partial int UsageMetricIndex { get; set; }
    [ObservableProperty] public partial int UsageScopeIndex { get; set; }
    [ObservableProperty] public partial ActivityUsageDayChoice? SelectedUsageDay { get; set; }
    public string UsageTokenOption => DesktopText.Get("UsageTokens");
    public string UsageCacheOption => DesktopText.Get("UsageCache");
    public string UsageTurnOption => DesktopText.Get("UsageTurn");
    public string UsageRequestOption => DesktopText.Get("UsageRequest");
    public string UsageHeadline => DesktopText.Get("UsageHeadline", Tokens(_usage.Totals.TotalTokens), Rate(_usage.Totals.CacheHitPercent));
    public string UsageCoverage => Coverage(_usage.Totals);
    public string UsageScopeNote => DesktopText.Get(UsageScopeIndex == 1 ? "UsageRequestNote" : "UsageTurnNote");
    public string UsageDailyScale { get; private set; } = "";
    public string UsageDailyRange => DesktopText.Get("UsageRecentRange", _usage.EndDay.AddDays(-27).ToString("d"), _usage.EndDay.ToString("d"));
    public string UsageSelectedDetails => SelectedUsageDay is { } day
        ? DesktopText.Get("UsageDayDetails", day.Label, DesktopText.Get("UsageBarDetails",
            DesktopText.Get("UsageHeadline", Tokens(day.Totals.TotalTokens), Rate(day.Totals.CacheHitPercent)), Details(day.Totals))) : "";
    public string UsageSelectedCoverage => SelectedUsageDay is { } day ? Coverage(day.Totals) : "";
    partial void OnUsageMetricIndexChanged(int value) => RebuildUsage();
    partial void OnUsageScopeIndexChanged(int value) => RebuildUsage();
    partial void OnSelectedUsageDayChanged(ActivityUsageDayChoice? value)
    {
        OnPropertyChanged(nameof(UsageSelectedDetails));
        OnPropertyChanged(nameof(UsageSelectedCoverage));
    }
    private static string Tokens(decimal? value) => value?.ToString("N0") ?? DesktopText.Get("UsageUnknown");
    private static string Rate(double? value) => value is { } rate ? DesktopText.Get("UsagePercent", rate.ToString("N1")) : DesktopText.Get("UsageUnknown");
    private static string Coverage(ActivityUsageTotals totals) => DesktopText.Get("UsageCoverage", totals.TokenReportedCount,
        totals.ExecutionCount, totals.CacheReportedCount, totals.ExcludedScopeCount);
    private static string Details(ActivityUsageTotals totals) => DesktopText.Get("UsageDetails", Tokens(totals.InputTokens),
        Tokens(totals.OutputTokens), Tokens(totals.CachedInputTokens), Tokens(totals.CacheWriteInputTokens));
    private decimal? Metric(ActivityUsageTotals totals) => UsageMetricIndex == 1
        ? totals.CacheHitPercent is { } rate ? (decimal)rate : null : totals.TotalTokens;
    private ActivityUsageBar UsageBar(string label, ActivityUsageTotals totals, decimal maximum)
    {
        var value = Metric(totals);
        var display = UsageMetricIndex == 1 ? Rate(totals.CacheHitPercent) : Tokens(value);
        if (totals.ExecutionCount == 0) display = DesktopText.Get("UsageNoRuns");
        var percent = value is { } reported && maximum > 0 ? (double)(100m * reported / maximum) : 0;
        return new(label, display, Math.Clamp(percent, 0, 100),
            DesktopText.Get("UsageBarDetails", Details(totals), Coverage(totals)),
            totals.ExecutionCount == 0 ? "·" : value is null ? "?" : value == 0 ? "0" : "");
    }
    private void RebuildUsage()
    {
        _usage = ActivityUsageSummary.Build(_activityRecords, _activity.EndDay, _activity.Days.Count, TimeZoneInfo.Local,
            UsageScopeIndex == 1 ? "last-request" : "turn");
        var recent = _usage.Days.TakeLast(28).ToArray();
        var dailyMax = UsageMetricIndex == 1 ? 100m : Math.Max(1m, recent.Max(day => Metric(day.Totals) ?? 0));
        UsageDaily = recent.Select(day => UsageBar(day.Date.ToString("D"), day.Totals, dailyMax)).ToArray();
        // Use reported model attribution, never the requested model as a substitute.
        var models = _usage.Models.OrderByDescending(model => Metric(model.Totals) ?? -1).ToArray();
        var modelMax = UsageMetricIndex == 1 ? 100m : Math.Max(1m, models.Select(model => Metric(model.Totals) ?? 0).DefaultIfEmpty().Max());
        UsageModels = models.Select(model => UsageBar(string.IsNullOrWhiteSpace(model.Model)
            ? DesktopText.Get("ActivityUnknownModel") : model.Model, model.Totals, modelMax)).ToArray();
        var selected = SelectedUsageDay?.Date;
        UsageDays = _usage.Days.Reverse().Select(day => new ActivityUsageDayChoice(day.Date, day.Totals)).ToArray();
        OnPropertyChanged(nameof(UsageDays));
        SelectedUsageDay = UsageDays.FirstOrDefault(day => day.Date == selected) ?? UsageDays.FirstOrDefault();
        UsageDailyScale = UsageMetricIndex == 1 ? Rate(100) : Tokens(dailyMax);
        foreach (var name in new[] { nameof(UsageDaily), nameof(UsageModels), nameof(UsageHeadline), nameof(UsageCoverage),
            nameof(UsageScopeNote), nameof(UsageDailyScale), nameof(UsageDailyRange) }) OnPropertyChanged(name);
    }
}
