using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Pricing;
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
    private PricingSnapshot? _usagePricing;
    public IReadOnlyList<ActivityUsageBar> UsageDaily { get; private set; } = [];
    public IReadOnlyList<ActivityUsageBar> UsageModels { get; private set; } = [];
    public IReadOnlyList<ActivityUsageDayChoice> UsageDays { get; private set; } = [];
    [ObservableProperty] public partial int UsageMetricIndex { get; set; }
    [ObservableProperty] public partial int UsageScopeIndex { get; set; }
    [ObservableProperty] public partial ActivityUsageDayChoice? SelectedUsageDay { get; set; }
    public string UsageTokenOption => DesktopText.Get("UsageTokens");
    public string UsageCacheOption => DesktopText.Get("UsageCache");
    public string UsageCostOption => DesktopText.Get("UsageCostMetric");
    public string UsageTurnOption => DesktopText.Get("UsageTurn");
    public string UsageRequestOption => DesktopText.Get("UsageRequest");
    public string UsageHeadline => DesktopText.Get("UsageHeadline", Tokens(_usage.Totals.TotalTokens), Rate(_usage.Totals.CacheHitPercent));
    public string UsageCoverage => Coverage(_usage.Totals);
    public string UsageCostHeadline => DesktopText.Get("UsageCostHeadline", Cost(_usage.Totals.Cost));
    public string UsageCostCoverage => CostCoverage(_usage.Totals.Cost, _usage.Totals.ExecutionCount);
    public string UsageCostMethodDate => DesktopText.Get("UsageCostMethodDate", (_usagePricing?.CheckedOn ?? AgentCostEstimator.PriceCheckedOn).ToString("d", CultureInfo.CurrentCulture));
    public string UsageScopeNote => DesktopText.Get(UsageScopeIndex == 1 ? "UsageRequestNote" : "UsageTurnNote");
    public string UsageDailyScale { get; private set; } = "";
    public string UsageDailyRange => DesktopText.Get("UsageRecentRange", _usage.EndDay.AddDays(-27).ToString("d"), _usage.EndDay.ToString("d"));
    public string UsageSelectedDetails => SelectedUsageDay is { } day
        ? DesktopText.Get("UsageDayDetails", day.Label, DesktopText.Get("UsageBarDetails",
            DesktopText.Get("UsageHeadline", Tokens(day.Totals.TotalTokens), Rate(day.Totals.CacheHitPercent)), Details(day.Totals))) : "";
    public string UsageSelectedCoverage => SelectedUsageDay is { } day ? Coverage(day.Totals) : "";
    public string UsageSelectedCost => SelectedUsageDay is { } day
        ? CostDetails(day.Totals.Cost, day.Totals.ExecutionCount) : "";
    partial void OnUsageMetricIndexChanged(int value) => RebuildUsage();
    partial void OnUsageScopeIndexChanged(int value) => _ = RefreshActivityStatisticsAsync();
    partial void OnSelectedUsageDayChanged(ActivityUsageDayChoice? value)
    {
        OnPropertyChanged(nameof(UsageSelectedDetails));
        OnPropertyChanged(nameof(UsageSelectedCoverage));
        OnPropertyChanged(nameof(UsageSelectedCost));
    }
    private static string Tokens(decimal? value) => value?.ToString("N0") ?? DesktopText.Get("UsageUnknown");
    private static string Rate(double? value) => value is { } rate ? DesktopText.Get("UsagePercent", rate.ToString("N1")) : DesktopText.Get("UsageUnknown");
    private static string Coverage(ActivityUsageTotals totals) => DesktopText.Get("UsageCoverage", totals.TokenReportedCount,
        totals.ExecutionCount, totals.CacheReportedCount, totals.ExcludedScopeCount);
    private static string Details(ActivityUsageTotals totals) => DesktopText.Get("UsageDetails", Tokens(totals.InputTokens),
        Tokens(totals.OutputTokens), Tokens(totals.CachedInputTokens), Tokens(totals.CacheWriteInputTokens));
    private static string Usd(decimal? value)
    {
        if (value is not decimal amount) return DesktopText.Get("UsageCostUnknown");
        if (amount == 0) return "$0";
        if (Math.Abs(amount) < 0.000001m) return "<$0.000001";
        return "$" + amount.ToString("0.######", CultureInfo.InvariantCulture);
    }
    private static string Cost(ActivityCostTotals cost)
    {
        if (cost.EstimatedCount == 0 || cost.MinUsd is not decimal min || cost.MaxUsd is not decimal max)
            return DesktopText.Get("UsageCostUnknown");
        var minimum = Usd(min);
        var maximum = Usd(max);
        if (min == max) return minimum;
        if (minimum == maximum && minimum == "<$0.000001") return minimum;
        if (minimum == maximum) return "≈" + maximum;
        return DesktopText.Get("UsageCostRange", minimum, maximum);
    }
    private static string CostCoverage(ActivityCostTotals cost, int executionCount) =>
        DesktopText.Get("UsageCostCoverage", cost.EstimatedCount, executionCount,
            Math.Max(0, executionCount - cost.EstimatedCount), cost.RangeCount);
    private static string CostDetails(ActivityCostTotals cost, int executionCount) =>
        DesktopText.Get("UsageCostDetails", Cost(cost), CostCoverage(cost, executionCount));
    private decimal? Metric(ActivityUsageTotals totals) => UsageMetricIndex switch
    {
        1 => totals.CacheHitPercent is { } rate ? (decimal)rate : null,
        2 => totals.Cost.MaxUsd,
        _ => totals.TotalTokens
    };
    private ActivityUsageBar UsageBar(string label, ActivityUsageTotals totals, decimal maximum)
    {
        var value = Metric(totals);
        var display = UsageMetricIndex switch
        {
            1 => Rate(totals.CacheHitPercent),
            2 => totals.Cost.EstimatedCount == 0 ? DesktopText.Get("UsageCostUnknown") :
                DesktopText.Get("UsageCostBarValue", Cost(totals.Cost), totals.Cost.EstimatedCount, totals.ExecutionCount),
            _ => Tokens(value)
        };
        if (totals.ExecutionCount == 0) display = DesktopText.Get("UsageNoRuns");
        var percent = value is { } reported && maximum > 0 ? (double)(100m * reported / maximum) : 0;
        return new(label, display, Math.Clamp(percent, 0, 100),
            DesktopText.Get("UsageBarDetails", UsageMetricIndex == 2
                    ? DesktopText.Get("UsageCostHeadline", Cost(totals.Cost)) : Details(totals),
                UsageMetricIndex == 2 ? CostCoverage(totals.Cost, totals.ExecutionCount) : Coverage(totals)),
            totals.ExecutionCount == 0 ? "·" : value is null ? "?" : value == 0 ? "0" : "");
    }
    private void RebuildUsage()
    {
        _usagePricing = App.Pricing.Current;
        _usage = _activityStatisticsFailed
            ? ActivityUsageSummary.Build([], _activity.EndDay, _activity.Days.Count, TimeZoneInfo.Local,
                UsageScopeIndex == 1 ? "last-request" : "turn", _usagePricing)
            : _activityStatistics?.Usage ?? ActivityUsageSummary.Build(_activityRecords, _activity.EndDay, _activity.Days.Count, TimeZoneInfo.Local,
                UsageScopeIndex == 1 ? "last-request" : "turn", _usagePricing);
        var recent = _usage.Days.TakeLast(28).ToArray();
        var dailyMax = UsageMetricIndex == 1 ? 100m : Math.Max(UsageMetricIndex == 2 ? 0.000001m : 1m,
            recent.Max(day => Metric(day.Totals) ?? 0));
        UsageDaily = recent.Select(day => UsageBar(day.Date.ToString("D"), day.Totals, dailyMax)).ToArray();
        // Use reported model attribution, never the requested model as a substitute.
        var models = _usage.Models.OrderByDescending(model => Metric(model.Totals) ?? -1).ToArray();
        var modelMax = UsageMetricIndex == 1 ? 100m : Math.Max(UsageMetricIndex == 2 ? 0.000001m : 1m,
            models.Select(model => Metric(model.Totals) ?? 0).DefaultIfEmpty().Max());
        UsageModels = models.Select(model => UsageBar(model.Model == ActivityStatisticsStore.OtherModelsKey
            ? DesktopText.Get("ActivityOtherModels") : string.IsNullOrWhiteSpace(model.Model)
            ? DesktopText.Get("ActivityUnknownModel") : model.Model, model.Totals, modelMax)).ToArray();
        var selected = SelectedUsageDay?.Date;
        UsageDays = _usage.Days.Reverse().Select(day => new ActivityUsageDayChoice(day.Date, day.Totals)).ToArray();
        OnPropertyChanged(nameof(UsageDays));
        SelectedUsageDay = UsageDays.FirstOrDefault(day => day.Date == selected) ?? UsageDays.FirstOrDefault();
        UsageDailyScale = UsageMetricIndex switch { 1 => Rate(100), 2 => Usd(dailyMax), _ => Tokens(dailyMax) };
        foreach (var name in new[] { nameof(UsageDaily), nameof(UsageModels), nameof(UsageHeadline), nameof(UsageCoverage),
            nameof(UsageCostHeadline), nameof(UsageCostCoverage), nameof(UsageCostMethodDate), nameof(UsageScopeNote),
            nameof(UsageDailyScale), nameof(UsageDailyRange), nameof(UsageSelectedCost) }) OnPropertyChanged(name);
    }

    public void RefreshPricing() => _ = RefreshActivityStatisticsAsync();
}
