using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop.History;

public sealed record AuditFilterOption(string Key, string Label);

public sealed partial class HistoryPageViewModel
{
    public AuditColumnLayout AuditColumns { get; } = new();

    [ObservableProperty] public partial IReadOnlyList<AuditFilterOption> AuditServiceOptions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<AuditFilterOption> AuditKindOptions { get; private set; } = [];
    [ObservableProperty] public partial AuditFilterOption? AuditServiceFilter { get; set; }
    [ObservableProperty] public partial AuditFilterOption? AuditKindFilter { get; set; }
    [ObservableProperty] public partial AuditHistoryRow? SelectedAuditRow { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedExecution))]
    public partial ExecutionHistoryRow? SelectedExecution { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyExecutionOperations))]
    public partial IReadOnlyList<AuditHistoryRow> ExecutionOperations { get; private set; } = [];

    public bool HasSelectedExecution => SelectedExecution is not null;
    public bool ShowEmptyExecutionOperations => ExecutionOperations.Count == 0;

    private void RefreshExecutionOperations() =>
        ExecutionOperations = SelectedExecution is { Record.Id: var id }
            ? AuditEvents.Where(row => row.Record.ExecutionId == id).OrderBy(row => row.Record.Timestamp).ToArray()
            : [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyAuditFilter))]
    public partial IReadOnlyList<AuditHistoryRow> FilteredAuditEvents { get; private set; } = [];

    public bool ShowEmptyAuditFilter => HasAuditEvents && FilteredAuditEvents.Count == 0;

    partial void OnAuditServiceFilterChanged(AuditFilterOption? value)
    {
        ApplyRunFilters();
        ApplyAuditFilters();
    }
    partial void OnAuditKindFilterChanged(AuditFilterOption? value) => ApplyAuditFilters();

    private void RefreshAuditFilterOptions()
    {
        RefreshExecutionOperations();
        var service = AuditServiceFilter?.Key;
        var kind = AuditKindFilter?.Key;
        // One service filter serves both the run table and the operation table.
        AuditServiceOptions = BuildAuditOptions("AuditAllServices", AuditEvents.Select(row => new AuditFilterOption(row.Record.Source, row.Source))
            .Concat(_activityRecords.Select(record => new AuditFilterOption(record.Source, new ExecutionHistoryRow(record).Source))));
        AuditKindOptions = BuildAuditOptions("AuditAllKinds", AuditEvents.Select(row => new AuditFilterOption(row.FilterKind, row.FilterKindLabel)));
        AuditServiceFilter = AuditServiceOptions.FirstOrDefault(option => option.Key == service) ?? AuditServiceOptions[0];
        AuditKindFilter = AuditKindOptions.FirstOrDefault(option => option.Key == kind) ?? AuditKindOptions[0];
        ApplyAuditFilters();
    }

    private static AuditFilterOption[] BuildAuditOptions(string allLabel, IEnumerable<AuditFilterOption> options) =>
        [new("", DesktopText.Get(allLabel)), .. options.DistinctBy(option => option.Key).OrderBy(option => option.Label, StringComparer.CurrentCulture)];

    private void ApplyAuditFilters()
    {
        var service = AuditServiceFilter?.Key;
        var kind = AuditKindFilter?.Key;
        var accessOnly = HistoryViewMode == HistoryViewMode.Access;
        var query = RecordSearchQuery.Trim();
        var day = ExecutionDayFilter;
        FilteredAuditEvents = AuditEvents.Where(row =>
            (string.IsNullOrEmpty(service) || row.Record.Source == service) &&
            (string.IsNullOrEmpty(kind) || row.FilterKind == kind) &&
            (!accessOnly || ChatAgentBridge.Core.History.AuditEvent.IsAccessKind(row.Record.Kind)) &&
            (day is null || DateOnly.FromDateTime(row.Record.Timestamp.ToLocalTime().DateTime) == day) &&
            (query.Length == 0 || row.SearchIndex.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedAuditRow is { } selected)
        {
            SelectedAuditRow = FilteredAuditEvents.FirstOrDefault(row => row.Record == selected.Record);
            if (SelectedAuditRow is null && !IsRunsMode) IsDetailOpen = false;
        }
        NotifyRecordSummaryChanged();
    }
}

public sealed partial class AuditColumnLayout : ObservableObject
{
    [ObservableProperty] public partial GridLength Time { get; set; } = new(74);
    [ObservableProperty] public partial GridLength Action { get; set; } = new(132);
    [ObservableProperty] public partial GridLength Service { get; set; } = new(100);
    [ObservableProperty] public partial GridLength Conversation { get; set; } = new(160);
}
