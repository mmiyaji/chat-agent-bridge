using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private const double ConnectionScopeNavigationBreakpoint = 760;
    private ListView? _connectionScopeNavigation;
    private ComboBox? _connectionScopePicker;
    private Grid? _connectionScopeShell;
    private StackPanel? _connectionScopePage;
    private TextBlock? _connectionScopeHeading;
    private TextBlock? _connectionScopeDescription;
    private StackPanel? _connectionScopeCommonGroup;
    private StackPanel? _connectionScopeMattermostGroup;
    private StackPanel? _connectionScopeSlackGroup;
    private string _connectionSettingsScope = "common";
    private bool _syncingConnectionScopeSelection;

    /// <summary>Builds the local connection-settings navigation while retaining the XAML-owned controls.</summary>
    private void InitializeConnectionSettingsScope()
    {
        var originalContent = MattermostSettingsCard.Parent as StackPanel;
        if (originalContent is null) return;

        var guide = FindLocalizedElement<TextBlock>(originalContent, "ChatSettingsGuide");
        var overview = FindLocalizedElement<TextBlock>(originalContent, "ChatScopeOverview");
        var originalChildren = originalContent.Children.ToArray();
        originalContent.Children.Clear();

        _connectionScopeHeading = new TextBlock
        {
            Style = (Style)Application.Current.Resources["SettingsScopeTitleStyle"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        AutomationProperties.SetAutomationId(_connectionScopeHeading, "ConnectionScopeHeading");

        _connectionScopeCommonGroup = new StackPanel { Spacing = 16 };
        _connectionScopeDescription = new TextBlock { Text = DesktopText.Get("ConnectionScopeCommonHelp"), TextWrapping = TextWrapping.Wrap };
        _connectionScopeCommonGroup.Children.Add(_connectionScopeDescription);
        _connectionScopeCommonGroup.Children.Add(ChatApprovalSettingsCard);
        _connectionScopeCommonGroup.Children.Add(FileAttachmentSettings);
        _connectionScopeCommonGroup.Children.Add(CodexChatSharingSettings);

        _connectionScopeMattermostGroup = new StackPanel { Spacing = 16 };
        _connectionScopeMattermostGroup.Children.Add(MattermostSettingsCard);

        _connectionScopeSlackGroup = new StackPanel { Spacing = 16 };
        _connectionScopeSlackGroup.Children.Add(SlackSettingsCard);

        // Detach the original panel's remaining elements so no XAML control is
        // recreated; all named fields and event handlers continue to point at
        // the original instances.
        originalContent.Spacing = 0;
        foreach (var element in originalChildren)
        {
            if (ReferenceEquals(element, guide) || ReferenceEquals(element, overview) ||
                ReferenceEquals(element, ChatApprovalSettingsCard) || ReferenceEquals(element, MattermostSettingsCard) ||
                ReferenceEquals(element, SlackSettingsCard) || ReferenceEquals(element, FileAttachmentSettings) ||
                ReferenceEquals(element, CodexChatSharingSettings))
                continue;
            originalContent.Children.Add(element);
        }

        var shell = new Grid { ColumnSpacing = 20 };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _connectionScopeShell = shell;

        _connectionScopeNavigation = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(_connectionScopeNavigation, "ConnectionScopeList");
        AddConnectionScopeItem(_connectionScopeNavigation, "common", DesktopText.Get("AgentScopeCommon"), "ConnectionScopeCommon");
        AddConnectionScopeItem(_connectionScopeNavigation, "mattermost", MrtText.Get("MattermostSectionTitle.Text"), "ConnectionScopeMattermost");
        AddConnectionScopeItem(_connectionScopeNavigation, "slack", MrtText.Get("SlackSectionTitle.Text"), "ConnectionScopeSlack");
        _connectionScopeNavigation.SelectionChanged += (_, _) =>
        {
            if (_syncingConnectionScopeSelection || _connectionScopeNavigation.SelectedItem is not ListViewItem item) return;
            SetConnectionSettingsScope(item.Tag as string ?? "common");
        };
        Grid.SetColumn(_connectionScopeNavigation, 0);
        shell.Children.Add(_connectionScopeNavigation);

        _connectionScopePicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetAutomationId(_connectionScopePicker, "ConnectionScopePicker");
        AddConnectionScopeChoice(_connectionScopePicker, "common", DesktopText.Get("AgentScopeCommon"));
        AddConnectionScopeChoice(_connectionScopePicker, "mattermost", MrtText.Get("MattermostSectionTitle.Text"));
        AddConnectionScopeChoice(_connectionScopePicker, "slack", MrtText.Get("SlackSectionTitle.Text"));
        _connectionScopePicker.SelectionChanged += (_, _) =>
        {
            if (_syncingConnectionScopeSelection || _connectionScopePicker.SelectedItem is not ComboBoxItem item) return;
            SetConnectionSettingsScope(item.Tag as string ?? "common");
        };
        Grid.SetColumn(_connectionScopePicker, 0);
        shell.Children.Add(_connectionScopePicker);

        var page = new StackPanel { Spacing = 12 };
        _connectionScopePage = page;
        page.Children.Add(_connectionScopeHeading);
        page.Children.Add(_connectionScopeCommonGroup);
        page.Children.Add(_connectionScopeMattermostGroup);
        page.Children.Add(_connectionScopeSlackGroup);
        Grid.SetColumn(page, 1);
        shell.Children.Add(page);
        originalContent.Children.Add(shell);

        shell.SizeChanged += (_, _) => UpdateConnectionScopeNavigation(shell.ActualWidth);
        SetConnectionSettingsScope("common");
        RefreshConnectionScopeLabels(System.Globalization.CultureInfo.CurrentUICulture);
        UpdateConnectionScopeNavigation(originalContent.ActualWidth);
    }

    private static T? FindLocalizedElement<T>(Panel panel, string key) where T : FrameworkElement =>
        panel.Children.OfType<T>().FirstOrDefault(element => LocalizedResourceKey.GetKey(element) == key);

    private static void AddConnectionScopeItem(ListView navigation, string key, string label, string automationId)
    {
        var item = new ListViewItem { Content = ScopeNavigationLabel(key == "common" ? Symbol.Setting : Symbol.Link, label), Tag = key };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, label);
        navigation.Items.Add(item);
    }

    private static void AddConnectionScopeChoice(ComboBox picker, string key, string label)
    {
        var item = new ComboBoxItem { Content = label, Tag = key };
        AutomationProperties.SetName(item, label);
        picker.Items.Add(item);
    }

    private void UpdateConnectionScopeNavigation(double availableWidth)
    {
        if (_connectionScopeNavigation is null || _connectionScopePicker is null ||
            _connectionScopeShell is null || _connectionScopePage is null) return;
        var showNavigation = availableWidth >= ConnectionScopeNavigationBreakpoint;
        _connectionScopeNavigation.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopePicker.Visibility = showNavigation ? Visibility.Collapsed : Visibility.Visible;
        if (showNavigation)
        {
            Grid.SetRow(_connectionScopeNavigation, 0);
            Grid.SetColumn(_connectionScopeNavigation, 0);
            Grid.SetRow(_connectionScopePicker, 0);
            Grid.SetColumn(_connectionScopePicker, 0);
            Grid.SetColumnSpan(_connectionScopePicker, 1);
            Grid.SetRow(_connectionScopePage, 0);
            Grid.SetColumn(_connectionScopePage, 1);
            Grid.SetColumnSpan(_connectionScopePage, 1);
        }
        else
        {
            Grid.SetRow(_connectionScopePicker, 0);
            Grid.SetColumn(_connectionScopePicker, 0);
            Grid.SetColumnSpan(_connectionScopePicker, 2);
            Grid.SetRow(_connectionScopeNavigation, 1);
            Grid.SetColumn(_connectionScopeNavigation, 0);
            Grid.SetRow(_connectionScopePage, 1);
            Grid.SetColumn(_connectionScopePage, 0);
            Grid.SetColumnSpan(_connectionScopePage, 2);
        }
    }

    private void SetConnectionSettingsScope(string scope)
    {
        if (_connectionScopeHeading is null || _connectionScopeCommonGroup is null ||
            _connectionScopeMattermostGroup is null || _connectionScopeSlackGroup is null) return;
        _connectionSettingsScope = scope is "mattermost" or "slack" ? scope : "common";
        var isCommon = _connectionSettingsScope == "common";
        _connectionScopeHeading.Text = _connectionSettingsScope switch
        {
            "mattermost" => MrtText.Get("MattermostSectionTitle.Text"),
            "slack" => MrtText.Get("SlackSectionTitle.Text"),
            _ => DesktopText.Get("AgentScopeCommon")
        };
        _connectionScopeHeading.Visibility = isCommon ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeCommonGroup.Visibility = isCommon ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeMattermostGroup.Visibility = _connectionSettingsScope == "mattermost" ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeSlackGroup.Visibility = _connectionSettingsScope == "slack" ? Visibility.Visible : Visibility.Collapsed;
        ChatApprovalSettingsCard.IsExpanded = isCommon;
        MattermostSettingsCard.IsExpanded = _connectionSettingsScope == "mattermost";
        SlackSettingsCard.IsExpanded = _connectionSettingsScope == "slack";

        _syncingConnectionScopeSelection = true;
        if (_connectionScopeNavigation is not null)
            _connectionScopeNavigation.SelectedIndex = _connectionSettingsScope switch { "mattermost" => 1, "slack" => 2, _ => 0 };
        if (_connectionScopePicker is not null)
            _connectionScopePicker.SelectedIndex = _connectionSettingsScope switch { "mattermost" => 1, "slack" => 2, _ => 0 };
        _syncingConnectionScopeSelection = false;
    }

    private void RefreshConnectionScopeLabels(System.Globalization.CultureInfo culture)
    {
        if (_connectionScopeNavigation is null || _connectionScopePicker is null) return;
        var labels = new[]
        {
            DesktopText.Get("AgentScopeCommon"),
            MrtText.Get("MattermostSectionTitle.Text", culture),
            MrtText.Get("SlackSectionTitle.Text", culture)
        };
        for (var index = 0; index < labels.Length; index++)
        {
            if (_connectionScopeNavigation.Items[index] is ListViewItem navigationItem)
            {
                navigationItem.Content = ScopeNavigationLabel(index == 0 ? Symbol.Setting : Symbol.Link, labels[index]);
                AutomationProperties.SetName(navigationItem, labels[index]);
            }
            if (_connectionScopePicker.Items[index] is ComboBoxItem pickerItem)
            {
                pickerItem.Content = labels[index];
                AutomationProperties.SetName(pickerItem, labels[index]);
            }
        }
        AutomationProperties.SetName(_connectionScopeNavigation, DesktopText.Get("AgentScopePicker"));
        AutomationProperties.SetName(_connectionScopePicker, DesktopText.Get("AgentScopePicker"));
        _connectionScopePicker.Header = DesktopText.Get("AgentScopePicker");
        if (_connectionScopeDescription is not null) _connectionScopeDescription.Text = DesktopText.Get("ConnectionScopeCommonHelp");
        SetConnectionSettingsScope(_connectionSettingsScope);
    }

}
