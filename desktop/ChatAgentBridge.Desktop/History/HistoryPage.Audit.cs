using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
#if CAB_EXPERIMENTAL_TABLE_VIEW
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Tabular;
using Microsoft.UI.Xaml.Data;
#endif

namespace ChatAgentBridge_Desktop.History;

public sealed partial class HistoryPage
{
    private double _auditConversationMinWidth = 160;

    private void AuditHistoryList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        FillAuditConversationColumn();

    private void FillAuditConversationColumn()
    {
        var audit = ViewModel.AuditColumns;
        // Both the header and the rows have 12 DIP side padding and three 8 DIP gaps.
        var available = AuditHistoryList.ActualWidth - 48 - audit.Time.Value - audit.Action.Value - audit.Service.Value;
        audit.Conversation = new GridLength(Math.Max(_auditConversationMinWidth, available));
    }

    private void HistoryColumn_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeHistoryColumn((Thumb)sender, e.HorizontalChange);

    private void HistoryColumn_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        ResizeHistoryColumn((Thumb)sender, e.Key == VirtualKey.Left ? -8 : 8);
        e.Handled = true;
    }

    /// <summary>Thumb tags name the table and column, for example "Run.Model" or "Audit.Time".</summary>
    private void ResizeHistoryColumn(Thumb thumb, double delta)
    {
        static GridLength Resize(GridLength width, double change) => new(Math.Clamp(width.Value + change, 48, 640));

        var audit = ViewModel.AuditColumns;
        var runs = ViewModel.RunColumns;
        switch (thumb.Tag)
        {
            case "Audit.Time": audit.Time = Resize(audit.Time, delta); break;
            case "Audit.Action": audit.Action = Resize(audit.Action, delta); break;
            case "Audit.Service": audit.Service = Resize(audit.Service, delta); break;
            case "Audit.Conversation":
                _auditConversationMinWidth = Math.Max(48, audit.Conversation.Value + delta);
                break;
            case "Run.Started": runs.Started = Resize(runs.Started, delta); break;
            case "Run.Conversation": runs.Conversation = Resize(runs.Conversation, delta); break;
            case "Run.User": runs.User = Resize(runs.User, delta); break;
            case "Run.Result": runs.Result = Resize(runs.Result, delta); break;
            case "Run.Model": runs.Model = Resize(runs.Model, delta); break;
            case "Run.Duration": runs.Duration = Resize(runs.Duration, delta); break;
        }
        if (thumb.Tag is string column && column.StartsWith("Audit.", StringComparison.Ordinal))
            FillAuditConversationColumn();
    }

#if CAB_EXPERIMENTAL_TABLE_VIEW
    partial void InitializeExperimentalTables()
    {
        Application.Current.Resources.MergedDictionaries.Add(new TabularControlsResources());

        var runs = CreateTable("ExecutionHistoryTable", ViewModel.ExecutionsTabTitle,
            (nameof(ExecutionHistoryRow.StartedCompact), "HistoryRunStartedColumn", new GridLength(96)),
            (nameof(ExecutionHistoryRow.ConversationCell), "HistoryRunConversationColumn", new GridLength(200)),
            (nameof(ExecutionHistoryRow.UserCell), "HistoryRunUserColumn", new GridLength(120)),
            (nameof(ExecutionHistoryRow.Status), "HistoryRunResultColumn", new GridLength(96)),
            (nameof(ExecutionHistoryRow.ModelCell), "HistoryRunModelColumn", new GridLength(130)),
            (nameof(ExecutionHistoryRow.DurationCell), "HistoryRunDurationColumn", new GridLength(84)),
            (nameof(ExecutionHistoryRow.TokensCell), "HistoryRunTokensColumn", new GridLength(1, GridUnitType.Star)));
        runs.ItemsSource = ViewModel.Executions;
        runs.SelectionChanged += (sender, _) => ViewModel.SelectExecutionRow(sender.SelectedItem as ExecutionHistoryRow);
        ExecutionHistoryList.Visibility = Visibility.Collapsed;
        RunTableHost.Children.Insert(0, runs);

        var audit = CreateTable("AuditHistoryTable", ViewModel.AuditTabTitle,
            (nameof(AuditHistoryRow.TimeCompact), "AuditTimeColumn", new GridLength(96)),
            (nameof(AuditHistoryRow.Kind), "AuditActionColumn", new GridLength(160)),
            (nameof(AuditHistoryRow.Source), "AuditServiceColumn", new GridLength(100)),
            (nameof(AuditHistoryRow.Conversation), "AuditConversationColumn", new GridLength(1, GridUnitType.Star)));
        audit.ItemsSource = ViewModel.FilteredAuditEvents;
        audit.SelectionChanged += (sender, _) => ViewModel.SelectAuditRow(sender.SelectedItem as AuditHistoryRow);
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.FilteredAuditEvents))
                audit.ItemsSource = ViewModel.FilteredAuditEvents;
        };
        AuditHistoryList.Visibility = Visibility.Collapsed;
        AuditBrowser.Children.Insert(0, audit);
    }

    private static TableView CreateTable(string automationId, string name, params (string Property, string Resource, GridLength Width)[] columns)
    {
        var table = new TableView
        {
            Density = TableViewDensity.Compact,
            CanUserSortColumns = false,
            CanUserResizeColumns = true
        };
        AutomationProperties.SetAutomationId(table, automationId);
        AutomationProperties.SetName(table, name);
        foreach (var (property, resource, width) in columns)
            table.Columns.Add(new TableViewTextColumn
            {
                Binding = new Binding { Path = new PropertyPath(property) },
                Header = DesktopText.Get(resource),
                Width = width,
                MinWidth = 48,
                CanResize = true
            });
        return table;
    }
#endif
}
