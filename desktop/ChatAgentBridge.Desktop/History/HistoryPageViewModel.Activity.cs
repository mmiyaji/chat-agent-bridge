using System.Collections.ObjectModel;
using System.Globalization;
using ChatAgentBridge.Core.History;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.History;

public sealed record ActivityCalendarCell(DateOnly Date, int Count, bool InRange)
{
    public int Level => !InRange ? -1 : Count switch { 0 => 0, 1 => 1, < 4 => 2, < 8 => 3, _ => 4 };
    public string Label => InRange ? DesktopText.Get("ActivityDayCount", Date.ToString("D"), Count) : DesktopText.Get("ActivityOutsideDay", Date.ToString("D"));
    public string OutsideText => InRange ? "" : "–";
}
public sealed record ActivityBarRow(string Label, int Count, int Maximum)
{
    public string CountLabel => DesktopText.Get("ActivityRunCount", Count);
    public double Percent => Maximum > 0 ? 100d * Count / Maximum : 0;
    public string AccessibleLabel => $"{Label}: {CountLabel}";
}

public sealed partial class HistoryPageViewModel
{
    private IReadOnlyList<ExecutionRecord> _activityRecords = [];
    private readonly ActivityStatisticsStore _activityStatisticsStore = new();
    private ActivityStatisticsSnapshot? _activityStatistics;
    private bool _activityStatisticsFailed;
    private int _activityStatisticsRequest;
    private ActivitySummary _activity = ActivitySummary.Build([], DateOnly.FromDateTime(DateTime.Today), 90, TimeZoneInfo.Local);
    public IReadOnlyList<ActivityCalendarCell> ActivityCalendar { get; private set; } = [];
    public ObservableCollection<ActivityBarRow> ActivityWeekdays { get; } = [];
    public IReadOnlyList<string> ActivityRanges { get; } = [DesktopText.Get("Activity30Days"), DesktopText.Get("Activity90Days"), DesktopText.Get("Activity365Days")];
    public string Activity30DaysLabel => ActivityRanges[0];
    public string Activity90DaysLabel => ActivityRanges[1];
    public string Activity365DaysLabel => ActivityRanges[2];
    [ObservableProperty] public partial int ActivityRangeIndex { get; set; } = 1;
    [ObservableProperty] public partial ActivityCalendarCell? SelectedActivityDay { get; set; }
    [ObservableProperty] public partial DateOnly? ExecutionDayFilter { get; set; }

    public string ActivityTabTitle => DesktopText.Get("ActivityTabTitle");
    public string ActivityRange => $"{_activity.StartDay.ToString("d")} – {_activity.EndDay.ToString("d")}";
    public string ActivityTotal => _activity.TotalExecutions.ToString("N0");
    public string ActivityActiveDays => DesktopText.Get("ActivityDays", _activity.ActiveDays);
    public string ActivityStreak => DesktopText.Get("ActivityDays", _activity.LongestStreak);
    public string ActivityPeak => _activity.PeakDay is { } peak ? DesktopText.Get("ActivityPeakValue", peak.Date.ToString("m"), peak.Count) : DesktopText.Get("ActivityNoPeak");
    public string ActivityOutcomes => DesktopText.Get("ActivityOutcomes", _activity.Completed, _activity.Failed,
        _activity.Cancelled, _activity.TotalExecutions - _activity.Completed - _activity.Failed - _activity.Cancelled);
    public bool HasActivity => _activity.TotalExecutions > 0;
    public bool ShowActivityEmptyState => ActivityStatisticsAvailable && !HasActivity;
    // The summary tiles only repeat "0" while the empty state already explains there is no activity.
    public bool ShowActivitySummary => ActivityStatisticsAvailable && HasActivity;
    public bool ActivityStatisticsAvailable => IsHistoryLoadSuccessful && !IsBusy && !_activityStatisticsFailed && _activityStatistics is not null;
    public string ActivityStatisticsNotice => _activityStatisticsFailed
        ? DesktopText.Get("ActivityStatisticsUnavailable")
        : _activityStatistics?.MigratedFromAvailableRetainedHistory == true && _activityRecords.Count > 0
            ? DesktopText.Get("ActivityStatisticsMigrationCoverage") : "";
    public bool HasActivityStatisticsNotice => ActivityStatisticsNotice.Length > 0;
    public string ActivityEmptyHeading => DesktopText.Get(_activityRecords.Count > 0
        ? "ActivityNoRunsInPeriodHeading"
        : "ActivityNoRunsHeading");
    public string ActivityEmptyDescription => DesktopText.Get(_activityRecords.Count > 0
        ? "ActivityNoRunsInPeriodDescription"
        : "ActivityNoRunsDescription");
    public string SelectedActivityDayText => SelectedActivityDay?.Label ?? DesktopText.Get("ActivitySelectDay");
    public bool CanOpenActivityDay => SelectedActivityDay is { InRange: true, Count: > 0 };
    public bool HasExecutionDayFilter => ExecutionDayFilter is not null;
    public bool ShowEmptyExecutionHistory => IsHistoryLoadSuccessful && !IsBusy && !HasExecutions;
    public bool ShowEmptyAuditHistory => IsHistoryLoadSuccessful && !IsBusy && !HasAuditEvents;
    public bool ShowEmptyExecutionDayClearAction => ShowEmptyExecutionHistory && HasRecordFilters;
    public bool ShowExecutionFilterClearButton => IsHistoryLoadSuccessful && !IsBusy && HasExecutionDayFilter;

    partial void OnActivityRangeIndexChanged(int value) => _ = RefreshActivityStatisticsAsync();
    partial void OnSelectedActivityDayChanged(ActivityCalendarCell? value)
    {
        OnPropertyChanged(nameof(SelectedActivityDayText));
        OnPropertyChanged(nameof(CanOpenActivityDay));
        NotifySelectedDayUsageChanged();
    }
    partial void OnExecutionDayFilterChanged(DateOnly? value)
    {
        ApplyRunFilters();
        ApplyAuditFilters();
    }

    private void SetExecutionRecords(IReadOnlyList<ExecutionRecord> records)
    {
        _activityRecords = records;
        RebuildChatNameIndexes();
        ApplyRunFilters();
        _ = RefreshActivityStatisticsAsync();
    }

    private async Task RefreshActivityStatisticsAsync()
    {
        var request = Interlocked.Increment(ref _activityStatisticsRequest);
        var count = ActivityRangeIndex switch { 0 => 30, 2 => 365, _ => 90 };
        var scope = UsageScopeIndex == 1 ? "last-request" : "turn";
        try
        {
            var snapshot = await _activityStatisticsStore.ReadAsync(DateOnly.FromDateTime(DateTime.Today), count,
                TimeZoneInfo.Local, scope, App.Pricing.Current);
            if (request != _activityStatisticsRequest) return;
            _activityStatistics = snapshot;
            _activityStatisticsFailed = false;
            RebuildActivity();
        }
        catch
        {
            if (request != _activityStatisticsRequest) return;
            _activityStatistics = null;
            _activityStatisticsFailed = true;
            RebuildActivity();
        }
    }

    private void RebuildActivity()
    {
        var count = ActivityRangeIndex switch { 0 => 30, 2 => 365, _ => 90 };
        _activity = _activityStatisticsFailed
            ? ActivitySummary.Build([], DateOnly.FromDateTime(DateTime.Today), count, TimeZoneInfo.Local)
            : _activityStatistics?.Activity ?? ActivitySummary.Build(_activityRecords, DateOnly.FromDateTime(DateTime.Today), count, TimeZoneInfo.Local);
        var selectedDate = SelectedActivityDay?.Date;
        var calendar = new List<ActivityCalendarCell>();
        var firstMonday = _activity.StartDay.AddDays(-(((int)_activity.StartDay.DayOfWeek + 6) % 7));
        var days = _activity.Days.ToDictionary(day => day.Date);
        var endSunday = _activity.EndDay.AddDays((7 - (int)_activity.EndDay.DayOfWeek) % 7);
        for (var date = firstMonday; date <= endSunday; date = date.AddDays(1))
            calendar.Add(new(date, days.GetValueOrDefault(date)?.Count ?? 0, days.ContainsKey(date)));
        ActivityCalendar = calendar.AsReadOnly();
        OnPropertyChanged(nameof(ActivityCalendar));
        SelectedActivityDay = ActivityCalendar.FirstOrDefault(day => day.InRange && day.Date == selectedDate)
            ?? ActivityCalendar.LastOrDefault(day => day.InRange);
        OnPropertyChanged(nameof(SelectedActivityDay));
        ActivityWeekdays.Clear();
        var maxWeekday = Math.Max(1, _activity.Weekdays.Max(day => day.Count));
        foreach (var day in _activity.Weekdays)
            ActivityWeekdays.Add(new(CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day.DayOfWeek), day.Count, maxWeekday));
        RebuildUsage();
        foreach (var name in new[] { nameof(ActivityRange), nameof(ActivityTotal), nameof(ActivityActiveDays), nameof(ActivityStreak),
                     nameof(ActivityPeak), nameof(ActivityOutcomes), nameof(HasActivity), nameof(ShowActivityEmptyState), nameof(ShowActivitySummary),
                     nameof(ActivityStatisticsAvailable), nameof(ActivityStatisticsNotice), nameof(HasActivityStatisticsNotice),
                     nameof(ActivityEmptyHeading), nameof(ActivityEmptyDescription) })
            OnPropertyChanged(name);
    }

    public void ShowExecutionsForActivityDay()
    {
        if (!CanOpenActivityDay) return;
        HistoryViewMode = HistoryViewMode.Runs;
        ExecutionDayFilter = SelectedActivityDay!.Date;
    }

    [RelayCommand]
    private void ClearExecutionDayFilter() => ExecutionDayFilter = null;
}
