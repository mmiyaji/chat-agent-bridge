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
public sealed record ActivityDailyBar(DateOnly Date, int Count, int Maximum)
{
    public double Height => Maximum > 0 ? 96d * Count / Maximum : 0;
    public string Label => DesktopText.Get("ActivityDayCount", Date.ToString("D"), Count);
}

public sealed partial class HistoryPageViewModel
{
    private IReadOnlyList<ExecutionRecord> _activityRecords = [];
    private ActivitySummary _activity = ActivitySummary.Build([], DateOnly.FromDateTime(DateTime.Today), 90, TimeZoneInfo.Local);
    public IReadOnlyList<ActivityCalendarCell> ActivityCalendar { get; private set; } = [];
    public ObservableCollection<ActivityBarRow> ActivityWeekdays { get; } = [];
    public ObservableCollection<ActivityBarRow> ActivityModels { get; } = [];
    public ObservableCollection<ActivityDailyBar> ActivityDaily { get; } = [];
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
    public string SelectedActivityDayText => SelectedActivityDay?.Label ?? DesktopText.Get("ActivitySelectDay");
    public bool CanOpenActivityDay => SelectedActivityDay is { InRange: true, Count: > 0 };
    public string ActivityDailyStart => ActivityDaily.FirstOrDefault()?.Date.ToString("m") ?? "";
    public string ActivityDailyEnd => ActivityDaily.LastOrDefault()?.Date.ToString("m") ?? "";
    public string ActivityDailyScale => DesktopText.Get("ActivityDailyScale", ActivityDaily.Count > 0 ? ActivityDaily.Max(day => day.Count) : 0);
    public bool HasExecutionDayFilter => ExecutionDayFilter is not null;
    public bool ShowEmptyExecutionHistory => IsHistoryLoadSuccessful && !IsBusy && !HasExecutions;
    public bool ShowEmptyAuditHistory => IsHistoryLoadSuccessful && !IsBusy && !HasAuditEvents;
    public bool ShowEmptyExecutionDayClearAction => ShowEmptyExecutionHistory && HasExecutionDayFilter;
    public bool ShowExecutionFilterClearButton => IsHistoryLoadSuccessful && !IsBusy && HasExecutionDayFilter && HasExecutions;

    partial void OnActivityRangeIndexChanged(int value) => RebuildActivity();
    partial void OnSelectedActivityDayChanged(ActivityCalendarCell? value)
    {
        OnPropertyChanged(nameof(SelectedActivityDayText));
        OnPropertyChanged(nameof(CanOpenActivityDay));
    }
    partial void OnExecutionDayFilterChanged(DateOnly? value) => ApplyExecutionDayFilter();

    private void SetExecutionRecords(IReadOnlyList<ExecutionRecord> records)
    {
        _activityRecords = records;
        RebuildChatNameIndexes();
        ApplyExecutionDayFilter();
        RebuildActivity();
    }

    private void RebuildActivity()
    {
        var count = ActivityRangeIndex switch { 0 => 30, 2 => 365, _ => 90 };
        _activity = ActivitySummary.Build(_activityRecords, DateOnly.FromDateTime(DateTime.Today), count, TimeZoneInfo.Local);
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
        ActivityModels.Clear();
        var maxModel = _activity.Models.Count > 0 ? _activity.Models.Max(model => model.Count) : 1;
        foreach (var model in _activity.Models)
            ActivityModels.Add(new(string.IsNullOrWhiteSpace(model.Model) ? DesktopText.Get("ActivityUnknownModel") : model.Model,
                model.Count, Math.Max(1, maxModel)));
        ActivityDaily.Clear();
        var recent = _activity.Days.TakeLast(28).ToArray();
        var maxDay = Math.Max(1, recent.Max(day => day.Count));
        foreach (var day in recent) ActivityDaily.Add(new(day.Date, day.Count, maxDay));
        RebuildUsage();
        foreach (var name in new[] { nameof(ActivityRange), nameof(ActivityTotal), nameof(ActivityActiveDays), nameof(ActivityStreak),
                     nameof(ActivityPeak), nameof(ActivityOutcomes), nameof(HasActivity), nameof(ActivityDailyStart), nameof(ActivityDailyEnd), nameof(ActivityDailyScale) })
            OnPropertyChanged(name);
    }

    public void ShowExecutionsForActivityDay()
    {
        if (CanOpenActivityDay) ExecutionDayFilter = SelectedActivityDay!.Date;
    }

    [RelayCommand]
    private void ClearExecutionDayFilter() => ExecutionDayFilter = null;

    private void ApplyExecutionDayFilter()
    {
        Executions.Clear();
        foreach (var record in _activityRecords.Where(record => ExecutionDayFilter is null ||
                     DateOnly.FromDateTime(record.StartedAt.ToLocalTime().DateTime) == ExecutionDayFilter))
            Executions.Add(new(record, ExecutionChatLabel(record)));
        HasExecutions = Executions.Count > 0;
        OnPropertyChanged(nameof(ExecutionsTabTitle));
        OnPropertyChanged(nameof(ExecutionSummary));
        OnPropertyChanged(nameof(HasExecutionDayFilter));
        OnPropertyChanged(nameof(ExecutionEmptyHeading));
        OnPropertyChanged(nameof(ExecutionEmptyDescription));
        OnPropertyChanged(nameof(ShowEmptyExecutionHistory));
        OnPropertyChanged(nameof(ShowEmptyExecutionDayClearAction));
        OnPropertyChanged(nameof(ShowExecutionFilterClearButton));
    }
}
