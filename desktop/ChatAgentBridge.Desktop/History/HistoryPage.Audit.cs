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
    private void AuditColumn_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeAuditColumn((Thumb)sender, e.HorizontalChange);

    private void AuditColumn_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        ResizeAuditColumn((Thumb)sender, e.Key == VirtualKey.Left ? -8 : 8);
        e.Handled = true;
    }

    private void ResizeAuditColumn(Thumb thumb, double delta)
    {
        static GridLength Resize(GridLength width, double change) => new(Math.Clamp(width.Value + change, 48, 640));

        var columns = ViewModel.AuditColumns;
        switch (thumb.Tag)
        {
            case "Time": columns.Time = Resize(columns.Time, delta); break;
            case "Action": columns.Action = Resize(columns.Action, delta); break;
            case "Service": columns.Service = Resize(columns.Service, delta); break;
            case "Conversation": columns.Conversation = Resize(columns.Conversation, delta); break;
        }
    }

#if CAB_EXPERIMENTAL_TABLE_VIEW
    partial void InitializeExperimentalAuditTable()
    {
        Application.Current.Resources.MergedDictionaries.Add(new TabularControlsResources());
        var table = new TableView
        {
            Density = TableViewDensity.Compact,
            CanUserSortColumns = false,
            CanUserResizeColumns = true
        };
        AutomationProperties.SetAutomationId(table, "AuditHistoryTable");
        AutomationProperties.SetName(table, ViewModel.AuditTabTitle);
        AddColumn(nameof(AuditHistoryRow.Time), "AuditTimeColumn", new GridLength(74));
        AddColumn(nameof(AuditHistoryRow.Kind), "AuditActionColumn", new GridLength(132));
        AddColumn(nameof(AuditHistoryRow.Source), "AuditServiceColumn", new GridLength(100));
        AddColumn(nameof(AuditHistoryRow.Conversation), "AuditConversationColumn", new GridLength(1, GridUnitType.Star));
        table.ItemsSource = ViewModel.FilteredAuditEvents;
        table.SelectionChanged += (sender, _) => ViewModel.SelectedAuditRow = sender.SelectedItem as AuditHistoryRow;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.FilteredAuditEvents))
                table.ItemsSource = ViewModel.FilteredAuditEvents;
        };
        AuditHistoryList.Visibility = Visibility.Collapsed;
        AuditBrowser.Children.Insert(0, table);

        void AddColumn(string property, string resource, GridLength width) => table.Columns.Add(new TableViewTextColumn
        {
            Binding = new Binding { Path = new PropertyPath(property) },
            Header = DesktopText.Get(resource),
            Width = width,
            MinWidth = 48,
            CanResize = true
        });
    }
#endif
}
