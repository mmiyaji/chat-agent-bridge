using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private const double ConnectionScopeNavigationBreakpoint = 850;
    private NavigationView? _connectionScopeNavigation;
    private TextBlock? _connectionScopeHeading;
    private TextBlock? _connectionScopeDescription;
    private StackPanel? _connectionScopeCommonGroup;
    private StackPanel? _connectionScopeMattermostGroup;
    private StackPanel? _connectionScopeSlackGroup;
    private StackPanel? _connectionScopeDiscordGroup;
    private string _connectionSettingsScope = "common";
    private bool _syncingConnectionScopeSelection;
    private string? _pendingConnectionScope;
    private bool _connectionScopeDispatchScheduled;

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

        _connectionScopeDiscordGroup = new StackPanel { Spacing = 16 };
        _connectionScopeDiscordGroup.Children.Add(DiscordSettingsCard);

        // Detach the original panel's remaining elements so no XAML control is
        // recreated; all named fields and event handlers continue to point at
        // the original instances.
        originalContent.Spacing = 0;
        foreach (var element in originalChildren)
        {
            if (ReferenceEquals(element, guide) || ReferenceEquals(element, overview) ||
                ReferenceEquals(element, ChatApprovalSettingsCard) || ReferenceEquals(element, MattermostSettingsCard) ||
                ReferenceEquals(element, SlackSettingsCard) || ReferenceEquals(element, DiscordSettingsCard) || ReferenceEquals(element, FileAttachmentSettings) ||
                ReferenceEquals(element, CodexChatSharingSettings))
                continue;
            originalContent.Children.Add(element);
        }

        var shell = new Grid();

        _connectionScopeNavigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneOpen = true,
            OpenPaneLength = 200,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false
        };
        AutomationProperties.SetAutomationId(_connectionScopeNavigation, "ConnectionScopeList");
        AutomationProperties.SetName(_connectionScopeNavigation, DesktopText.Get("AgentScopePicker"));
        AddConnectionScopeItem(_connectionScopeNavigation, "common", DesktopText.Get("AgentScopeCommon"), "ConnectionScopeCommon");
        AddConnectionScopeItem(_connectionScopeNavigation, "mattermost", MrtText.Get("MattermostSectionTitle.Text"), "ConnectionScopeMattermost");
        AddConnectionScopeItem(_connectionScopeNavigation, "slack", MrtText.Get("SlackSectionTitle.Text"), "ConnectionScopeSlack");
        AddConnectionScopeItem(_connectionScopeNavigation, "discord", MrtText.Get("DiscordSectionTitle.Text"), "ConnectionScopeDiscord");
        _connectionScopeNavigation.SelectionChanged += (_, args) =>
        {
            if (_syncingConnectionScopeSelection || args.SelectedItemContainer is not NavigationViewItem item) return;
            _pendingConnectionScope = item.Tag as string ?? "common";
            if (_connectionScopeDispatchScheduled) return;
            _connectionScopeDispatchScheduled = true;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _connectionScopeDispatchScheduled = false;
                if (_pendingConnectionScope is not { } next) return;
                _pendingConnectionScope = null;
                SetConnectionSettingsScope(next);
            })) _connectionScopeDispatchScheduled = false;
        };
        shell.Children.Add(_connectionScopeNavigation);

        var page = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        page.Children.Add(_connectionScopeHeading);
        page.Children.Add(_connectionScopeCommonGroup);
        page.Children.Add(_connectionScopeMattermostGroup);
        page.Children.Add(_connectionScopeSlackGroup);
        page.Children.Add(_connectionScopeDiscordGroup);
        _connectionScopeNavigation.Content = page;
        originalContent.Children.Add(shell);

        _connectionScopeNavigation.SizeChanged += (_, args) => UpdateConnectionScopeNavigation(args.NewSize.Width);
        SetConnectionSettingsScope("common");
        RefreshConnectionScopeLabels(System.Globalization.CultureInfo.CurrentUICulture);
        UpdateConnectionScopeNavigation(_connectionScopeNavigation.ActualWidth);
    }

    private static T? FindLocalizedElement<T>(Panel panel, string key) where T : FrameworkElement =>
        panel.Children.OfType<T>().FirstOrDefault(element => LocalizedResourceKey.GetKey(element) == key);

    private static void AddConnectionScopeItem(NavigationView navigation, string key, string label, string automationId)
    {
        var item = new NavigationViewItem { Content = ScopeNavigationLabel(key, label), Tag = key };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, label);
        navigation.MenuItems.Add(item);
    }

    private void UpdateConnectionScopeNavigation(double availableWidth)
    {
        if (_connectionScopeNavigation is null || availableWidth <= 0) return;
        var showNavigation = availableWidth >= ConnectionScopeNavigationBreakpoint;
        var mode = showNavigation
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.Top;
        if (_connectionScopeNavigation.PaneDisplayMode == mode) return;
        _connectionScopeNavigation.PaneDisplayMode = mode;
        _connectionScopeNavigation.IsPaneOpen = showNavigation;
    }

    private void SetConnectionSettingsScope(string scope)
    {
        if (_connectionScopeHeading is null || _connectionScopeCommonGroup is null ||
            _connectionScopeMattermostGroup is null || _connectionScopeSlackGroup is null || _connectionScopeDiscordGroup is null) return;
        _connectionSettingsScope = scope is "mattermost" or "slack" or "discord" ? scope : "common";
        var isCommon = _connectionSettingsScope == "common";
        _connectionScopeHeading.Text = _connectionSettingsScope switch
        {
            "mattermost" => MrtText.Get("MattermostSectionTitle.Text"),
            "slack" => MrtText.Get("SlackSectionTitle.Text"),
            "discord" => MrtText.Get("DiscordSectionTitle.Text"),
            _ => DesktopText.Get("AgentScopeCommon")
        };
        _connectionScopeHeading.Visibility = isCommon ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeCommonGroup.Visibility = isCommon ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeMattermostGroup.Visibility = _connectionSettingsScope == "mattermost" ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeSlackGroup.Visibility = _connectionSettingsScope == "slack" ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeDiscordGroup.Visibility = _connectionSettingsScope == "discord" ? Visibility.Visible : Visibility.Collapsed;
        ChatApprovalSettingsCard.IsExpanded = isCommon;
        MattermostSettingsCard.IsExpanded = _connectionSettingsScope == "mattermost";
        SlackSettingsCard.IsExpanded = _connectionSettingsScope == "slack";
        DiscordSettingsCard.IsExpanded = _connectionSettingsScope == "discord";

        _syncingConnectionScopeSelection = true;
        if (_connectionScopeNavigation is not null)
        {
            var index = _connectionSettingsScope switch { "mattermost" => 1, "slack" => 2, "discord" => 3, _ => 0 };
            _connectionScopeNavigation.SelectedItem = _connectionScopeNavigation.MenuItems[index];
        }
        _syncingConnectionScopeSelection = false;
    }

    private void RefreshConnectionScopeLabels(System.Globalization.CultureInfo culture)
    {
        if (_connectionScopeNavigation is null) return;
        string[] iconKeys = ["common", "mattermost", "slack", "discord"];
        var labels = new[]
        {
            DesktopText.Get("AgentScopeCommon"),
            MrtText.Get("MattermostSectionTitle.Text", culture),
            MrtText.Get("SlackSectionTitle.Text", culture),
            MrtText.Get("DiscordSectionTitle.Text", culture)
        };
        for (var index = 0; index < labels.Length; index++)
        {
            if (_connectionScopeNavigation.MenuItems[index] is NavigationViewItem navigationItem)
            {
                navigationItem.Content = ScopeNavigationLabel(iconKeys[index], labels[index]);
                AutomationProperties.SetName(navigationItem, labels[index]);
            }
        }
        AutomationProperties.SetName(_connectionScopeNavigation, DesktopText.Get("AgentScopePicker"));
        if (_connectionScopeDescription is not null) _connectionScopeDescription.Text = DesktopText.Get("ConnectionScopeCommonHelp");
        SetConnectionSettingsScope(_connectionSettingsScope);
    }

}
