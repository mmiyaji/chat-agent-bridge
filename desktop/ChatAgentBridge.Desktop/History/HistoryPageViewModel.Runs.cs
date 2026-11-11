using System.Globalization;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.UsageUi;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop.History;

/// <summary>What the run-record table lists: executions, every audited operation, or only access decisions.</summary>
public enum HistoryViewMode { Runs, Operations, Access }

public enum RunSortColumn { Started, Conversation, User, Result, Model, Duration, Tokens }

internal static class HistoryFormat
{
    public static string CompactTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? DesktopText.Get("HistoryUpdatedToday", local.ToString("HH:mm", CultureInfo.CurrentCulture))
            : local.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Keeps chat IDs recognisable in a narrow column without hiding that they are IDs.</summary>
    public static string ShortId(string? id) => string.IsNullOrWhiteSpace(id) ? "—" : id.Length <= 12 ? id : id[..10] + "…";

    public static string UserIdentity(string? name, string? id) =>
        Presentation.UserIdentityText.Format(name == DesktopText.Get("HistoryUserNameUnavailable") ? null : name, id);

    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value) return "—";
        if (value.TotalSeconds < 60) return DesktopText.Get("HistoryDurationSeconds", Math.Max(0, (int)Math.Round(value.TotalSeconds)));
        if (value.TotalHours < 1) return DesktopText.Get("HistoryDurationMinutes", (int)value.TotalMinutes, value.Seconds);
        return DesktopText.Get("HistoryDurationHours", (int)value.TotalHours, value.Minutes);
    }
}

public sealed partial class RunColumnLayout : ObservableObject
{
    internal static RunColumnLayout Default { get; } = new();
    [ObservableProperty] public partial GridLength Started { get; set; } = new(96);
    [ObservableProperty] public partial GridLength Conversation { get; set; } = new(200);
    [ObservableProperty] public partial GridLength User { get; set; } = new(120);
    [ObservableProperty] public partial GridLength Result { get; set; } = new(96);
    [ObservableProperty] public partial GridLength Model { get; set; } = new(130);
    [ObservableProperty] public partial GridLength Duration { get; set; } = new(84);
    [ObservableProperty] public partial GridLength Tokens { get; set; } = new(1, GridUnitType.Star);
}

public sealed partial class HistoryPageViewModel
{
    public RunColumnLayout RunColumns { get; } = new();

    [ObservableProperty] public partial HistoryViewMode HistoryViewMode { get; set; }
    [ObservableProperty] public partial string RecordSearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial RunSortColumn RunSort { get; set; } = RunSortColumn.Started;
    [ObservableProperty] public partial bool RunSortDescending { get; set; } = true;
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSessionExecutions))]
    public partial IReadOnlyList<ExecutionHistoryRow> SessionExecutions { get; private set; } = [];

    /// <summary>Asks the page to show the Conversations tab with this conversation selected.</summary>
    public event Action<SessionKey>? ConversationRequested;
    /// <summary>Asks the page to show the run-record tab.</summary>
    public event Action? RunRecordsRequested;

    private readonly Dictionary<Guid, ExecutionRecord> _executionsById = [];

    public bool IsRunsMode => HistoryViewMode == HistoryViewMode.Runs;
    public bool IsOperationsMode => HistoryViewMode != HistoryViewMode.Runs;
    public bool HasSessionExecutions => SessionExecutions.Count > 0;
    public bool ShowRunDetail => IsRunsMode && SelectedExecution is not null;
    public bool ShowAuditDetail => IsOperationsMode && SelectedAuditRow is not null;
    public bool CanOpenSelectedConversation => SelectedExecution?.Chat is { } chat && Sessions.Any(row => row.Binding.Key == chat.Session);
    public bool CanOpenAuditExecution => SelectedAuditRow?.Record.ExecutionId is { } id && _executionsById.ContainsKey(id);
    public string SelectedExecutionUsage => SelectedExecution is { } row ? UsageSummaryFormatter.Format(row.Record.Usage, row.Record.Metrics) : "";
    public bool ShowChatNamesNotice => HistoryViewMode != HistoryViewMode.Access && (IsRunsMode
        ? Executions.Any(static row => !row.HasChatNames)
        : FilteredAuditEvents.Any(static row => !row.HasChatNames && !AuditEvent.IsAccessKind(row.Record.Kind)));
    public string RecordSearchPlaceholder => DesktopText.Get("HistoryRecordSearchPlaceholder");
    public string RunResultSummary => IsRunsMode
        ? DesktopText.Get("HistoryRecordResults", Executions.Count, _activityRecords.Count)
        : DesktopText.Get("HistoryRecordResults", FilteredAuditEvents.Count, AuditEvents.Count);
    public bool HasRecordFilters => ExecutionDayFilter is not null || !string.IsNullOrWhiteSpace(RecordSearchQuery) ||
        !string.IsNullOrEmpty(AuditServiceFilter?.Key) || (IsOperationsMode && !string.IsNullOrEmpty(AuditKindFilter?.Key));
    public string ExecutionDayFilterLabel => ExecutionDayFilter is { } date ? DesktopText.Get("HistoryDayFilterChip", date.ToString("D")) : "";

    public string StartedHeader => SortHeader(RunSortColumn.Started, "HistoryRunStartedColumn");
    public string ConversationHeader => SortHeader(RunSortColumn.Conversation, "HistoryRunConversationColumn");
    public string UserHeader => SortHeader(RunSortColumn.User, "HistoryRunUserColumn");
    public string ResultHeader => SortHeader(RunSortColumn.Result, "HistoryRunResultColumn");
    public string ModelHeader => SortHeader(RunSortColumn.Model, "HistoryRunModelColumn");
    public string DurationHeader => SortHeader(RunSortColumn.Duration, "HistoryRunDurationColumn");
    public string TokensHeader => SortHeader(RunSortColumn.Tokens, "HistoryRunTokensColumn");

    private string SortHeader(RunSortColumn column, string key) =>
        RunSort == column ? $"{DesktopText.Get(key)} {(RunSortDescending ? "↓" : "↑")}" : DesktopText.Get(key);

    partial void OnHistoryViewModeChanged(HistoryViewMode value)
    {
        IsDetailOpen = false;
        foreach (var name in new[] { nameof(IsRunsMode), nameof(IsOperationsMode), nameof(ShowRunDetail), nameof(ShowAuditDetail) })
            OnPropertyChanged(name);
        ApplyAuditFilters();
        NotifyRecordSummaryChanged();
    }

    private CancellationTokenSource? _recordSearchDebounce;
    private string? _linkedAccessUserId;
    partial void OnRecordSearchQueryChanged(string value)
    {
        _linkedAccessUserId = null;
        _recordSearchDebounce?.Cancel();
        _recordSearchDebounce?.Dispose();
        var debounce = _recordSearchDebounce = new CancellationTokenSource();
        _ = ApplyRecordSearchAfterDelayAsync(string.IsNullOrWhiteSpace(value) ? 0 : 200, debounce.Token);
    }

    private async Task ApplyRecordSearchAfterDelayAsync(int delay, CancellationToken cancellationToken)
    {
        try { if (delay > 0) await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { return; }
        ApplyRunFilters();
        ApplyAuditFilters();
    }

    partial void OnRunSortChanged(RunSortColumn value) => ApplyRunFilters();
    partial void OnRunSortDescendingChanged(bool value) => ApplyRunFilters();
    partial void OnIsDetailOpenChanged(bool value)
    {
        // Closing the drawer (button, Esc or light dismiss) also clears the row selection.
        if (value) return;
        SelectedExecution = null;
        SelectedAuditRow = null;
    }

    public void SortRunsBy(RunSortColumn column)
    {
        if (RunSort == column) RunSortDescending = !RunSortDescending;
        else
        {
            // Text columns read best A→Z; time, duration and tokens largest first.
            RunSortDescending = column is RunSortColumn.Started or RunSortColumn.Duration or RunSortColumn.Tokens;
            RunSort = column;
        }
    }

    public void SelectExecutionRow(ExecutionHistoryRow? row)
    {
        SelectedExecution = row;
        if (row is not null) IsDetailOpen = true;
    }

    public void SelectAuditRow(AuditHistoryRow? row)
    {
        SelectedAuditRow = row;
        if (row is not null) IsDetailOpen = true;
    }

    /// <summary>Shows one execution in the run table, clearing filters that would hide it.</summary>
    public void OpenExecution(Guid id)
    {
        if (!_executionsById.ContainsKey(id)) return;
        HistoryViewMode = HistoryViewMode.Runs;
        if (Executions.All(row => row.Record.Id != id))
        {
            ExecutionDayFilter = null;
            RecordSearchQuery = string.Empty;
            AuditServiceFilter = AuditServiceOptions.FirstOrDefault();
            ApplyRunFilters();
        }
        SelectExecutionRow(Executions.FirstOrDefault(row => row.Record.Id == id));
        RunRecordsRequested?.Invoke();
    }

    /// <summary>Lists the access decisions and operations recorded for one chat user.</summary>
    public void ShowAccessHistory(string provider, string userId)
    {
        ExecutionDayFilter = null;
        HistoryViewMode = HistoryViewMode.Access;
        AuditKindFilter = AuditKindOptions.FirstOrDefault();
        SelectAccessHistoryService(provider);
        RecordSearchQuery = userId;
        _linkedAccessUserId = userId;
        ApplyAuditFilters();
        RunRecordsRequested?.Invoke();
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;

    [RelayCommand]
    private void OpenSelectedConversation()
    {
        if (SelectedExecution?.Chat is not { } chat) return;
        ConversationRequested?.Invoke(chat.Session);
    }

    [RelayCommand]
    private void OpenAuditExecution()
    {
        if (SelectedAuditRow?.Record.ExecutionId is { } id) OpenExecution(id);
    }

    [RelayCommand]
    private void ClearRecordFilters()
    {
        ExecutionDayFilter = null;
        RecordSearchQuery = string.Empty;
        AuditServiceFilter = AuditServiceOptions.FirstOrDefault();
        AuditKindFilter = AuditKindOptions.FirstOrDefault();
        ApplyRunFilters();
        ApplyAuditFilters();
    }

    /// <summary>Selects a conversation, clearing list filters that would hide it.</summary>
    public void SelectConversation(SessionKey key)
    {
        if (FilteredSessions.All(row => row.Binding.Key != key)) ClearSessionFilters();
        SelectedSession = FilteredSessions.FirstOrDefault(row => row.Binding.Key == key) ??
            Sessions.FirstOrDefault(row => row.Binding.Key == key);
    }

    private void ApplyRunFilters()
    {
        _executionsById.Clear();
        foreach (var record in _activityRecords) _executionsById.TryAdd(record.Id, record);
        var service = AuditServiceFilter?.Key;
        var query = RecordSearchQuery.Trim();
        var rows = _activityRecords
            .Where(record => ExecutionDayFilter is null || DateOnly.FromDateTime(record.StartedAt.ToLocalTime().DateTime) == ExecutionDayFilter)
            .Where(record => string.IsNullOrEmpty(service) || record.Source == service)
            .Select(record => new ExecutionHistoryRow(record, ExecutionChat(record), RunColumns, _allowedUserNames.GetValueOrDefault((record.Source, record.UserId ?? ""))))
            .Where(row => query.Length == 0 || row.SearchIndex.Contains(query, StringComparison.OrdinalIgnoreCase));
        IEnumerable<ExecutionHistoryRow> sorted = RunSort switch
        {
            RunSortColumn.Conversation => Order(rows, row => row.ConversationCell),
            RunSortColumn.User => Order(rows, row => row.UserCell),
            RunSortColumn.Result => Order(rows, row => row.Status),
            RunSortColumn.Model => Order(rows, row => row.ModelCell),
            RunSortColumn.Duration => Order(rows, row => row.Duration ?? TimeSpan.MinValue),
            RunSortColumn.Tokens => Order(rows, row => row.TokenTotal ?? -1),
            _ => Order(rows, row => row.Record.StartedAt)
        };
        var selectedId = SelectedExecution?.Record.Id;
        ReplaceItems(Executions, sorted.ToArray());
        HasExecutions = Executions.Count > 0;
        if (selectedId is { } id)
        {
            var selected = Executions.FirstOrDefault(row => row.Record.Id == id);
            SelectedExecution = selected;
            if (selected is null) IsDetailOpen = false;
        }
        foreach (var name in new[] { nameof(ExecutionsTabTitle), nameof(HasExecutionDayFilter),
                     nameof(ExecutionDayFilterLabel), nameof(ExecutionEmptyHeading), nameof(ExecutionEmptyDescription),
                     nameof(ShowEmptyExecutionHistory), nameof(ShowEmptyExecutionDayClearAction), nameof(ShowExecutionFilterClearButton),
                     nameof(StartedHeader), nameof(ConversationHeader), nameof(UserHeader), nameof(ResultHeader),
                     nameof(ModelHeader), nameof(DurationHeader), nameof(TokensHeader) })
            OnPropertyChanged(name);
        NotifyRecordSummaryChanged();
    }

    private IEnumerable<ExecutionHistoryRow> Order<TKey>(IEnumerable<ExecutionHistoryRow> rows, Func<ExecutionHistoryRow, TKey> key)
    {
        var comparer = typeof(TKey) == typeof(string) ? (IComparer<TKey>)StringComparer.CurrentCultureIgnoreCase : Comparer<TKey>.Default;
        var ordered = RunSortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        return ordered.ThenByDescending(static row => row.Record.StartedAt);
    }

    private void NotifyRecordSummaryChanged()
    {
        OnPropertyChanged(nameof(RunResultSummary));
        OnPropertyChanged(nameof(HasRecordFilters));
        OnPropertyChanged(nameof(ShowChatNamesNotice));
    }

    private void RefreshSessionExecutions()
    {
        SessionExecutions = SelectedSession is { } session
            ? _activityRecords.Where(record => _executionChats.GetValueOrDefault(record.Id)?.Session == session.Binding.Key)
                .OrderByDescending(static record => record.StartedAt).Take(5)
                .Select(record => new ExecutionHistoryRow(record, ExecutionChat(record), RunColumns, _allowedUserNames.GetValueOrDefault((record.Source, record.UserId ?? "")))).ToArray()
            : [];
    }

    partial void OnSelectedExecutionChanged(ExecutionHistoryRow? value)
    {
        RefreshExecutionOperations();
        OnPropertyChanged(nameof(ShowRunDetail));
        OnPropertyChanged(nameof(CanOpenSelectedConversation));
        OnPropertyChanged(nameof(SelectedExecutionUsage));
    }

    partial void OnSelectedAuditRowChanged(AuditHistoryRow? value)
    {
        OnPropertyChanged(nameof(ShowAuditDetail));
        OnPropertyChanged(nameof(CanOpenAuditExecution));
    }
}
