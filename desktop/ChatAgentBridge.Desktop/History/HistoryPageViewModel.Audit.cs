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
        var service = AuditServiceFilter;
        var kind = AuditKindFilter?.Key;
        // One service filter serves both the run table and the operation table.
        var services = AuditEvents.Select(row => row.Record.Source).Concat(_activityRecords.Select(record => record.Source));
        if (service is { Key.Length: > 0 }) services = services.Append(service.Key);
        AuditServiceOptions = BuildAuditOptions("AuditAllServices", services.Select(key => new AuditFilterOption(key, ExecutionHistoryRow.SourceLabel(key))));
        AuditKindOptions = BuildAuditOptions("AuditAllKinds", AuditEvents.Select(row => new AuditFilterOption(row.FilterKind, row.FilterKindLabel)));
        AuditServiceFilter = AuditServiceOptions.FirstOrDefault(option => option.Key == service?.Key) ?? AuditServiceOptions[0];
        AuditKindFilter = AuditKindOptions.FirstOrDefault(option => option.Key == kind) ?? AuditKindOptions[0];
        ApplyAuditFilters();
    }

    private void SelectAccessHistoryService(string provider)
    {
        if (AuditServiceOptions.All(option => option.Key != provider))
            AuditServiceOptions = BuildAuditOptions("AuditAllServices", AuditServiceOptions.Where(option => option.Key.Length > 0)
                .Append(new AuditFilterOption(provider, ExecutionHistoryRow.SourceLabel(provider))));
        AuditServiceFilter = AuditServiceOptions.First(option => option.Key == provider);
    }

    private static AuditFilterOption[] BuildAuditOptions(string allLabel, IEnumerable<AuditFilterOption> options) =>
        [new("", DesktopText.Get(allLabel)), .. options.DistinctBy(option => option.Key).OrderBy(option => option.Label, StringComparer.CurrentCulture)];

    private void ApplyAuditFilters()
    {
        var service = AuditServiceFilter?.Key;
        var kind = AuditKindFilter?.Key;
        var accessOnly = HistoryViewMode == HistoryViewMode.Access;
        var linkedUserId = accessOnly ? _linkedAccessUserId : null;
        var query = RecordSearchQuery.Trim();
        var day = ExecutionDayFilter;
        FilteredAuditEvents = AuditEvents.Where(row =>
            (string.IsNullOrEmpty(service) || row.Record.Source == service) &&
            (string.IsNullOrEmpty(kind) || row.FilterKind == kind) &&
            (!accessOnly || ChatAgentBridge.Core.History.AuditEvent.IsAccessKind(row.Record.Kind)) &&
            (day is null || DateOnly.FromDateTime(row.Record.Timestamp.ToLocalTime().DateTime) == day) &&
            (linkedUserId is not null ? row.Record.Access?.UserId == linkedUserId :
                query.Length == 0 || row.SearchIndex.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
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
    // The trailing star starts at a visually negligible weight. It only becomes visible when the user
    // deliberately drags the conversation divider inward, and lets that column keep resizing responsively.
    [ObservableProperty] public partial GridLength Conversation { get; set; } = new(1000, GridUnitType.Star);
    [ObservableProperty] public partial GridLength TrailingSpace { get; set; } = new(0.001, GridUnitType.Star);
}
