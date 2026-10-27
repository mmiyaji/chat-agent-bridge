using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.AgentSettings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private NavigationView? _connectionScopeNavigation;
    private ScrollViewer? _connectionSettingsScroll;
    private TextBlock? _connectionScopeHeading;
    private StackPanel? _connectionScopeHeader;
    private Border? _connectionScopeConnectedBadge;
    private Grid? _connectionScopeStatusRow;
    private FontIcon? _connectionScopeStatusIcon;
    private TextBlock? _connectionScopeStatusText;
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
        var originalContent = MattermostSettingsCard.Parent as Panel;
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

        _connectionScopeConnectedBadge = new Border
        {
            Style = (Style)Application.Current.Resources["ConnectionScopeBadgeStyle"],
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = DesktopText.Get("ConnectionScopeConnectedBadge"),
                Style = (Style)Application.Current.Resources["ConnectionScopeBadgeTextStyle"]
            }
        };
        AutomationProperties.SetAutomationId(_connectionScopeConnectedBadge, "ConnectionScopeConnectedBadge");
        AutomationProperties.SetAccessibilityView(_connectionScopeConnectedBadge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        _connectionScopeStatusIcon = new FontIcon();
        AutomationProperties.SetAccessibilityView(_connectionScopeStatusIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _connectionScopeStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(_connectionScopeStatusText, 1);
        _connectionScopeStatusRow = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _connectionScopeStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _connectionScopeStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _connectionScopeStatusRow.Children.Add(_connectionScopeStatusIcon);
        _connectionScopeStatusRow.Children.Add(_connectionScopeStatusText);
        AutomationProperties.SetAutomationId(_connectionScopeStatusRow, "ConnectionScopeStatus");

        var headingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _connectionScopeHeading.VerticalAlignment = VerticalAlignment.Center;
        _connectionScopeHeading.Margin = new Thickness(0);
        headingRow.Children.Add(_connectionScopeHeading);
        headingRow.Children.Add(_connectionScopeConnectedBadge);
        _connectionScopeHeader = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        _connectionScopeHeader.Children.Add(headingRow);
        _connectionScopeHeader.Children.Add(_connectionScopeStatusRow);
        AutomationProperties.SetAutomationId(_connectionScopeHeader, "ConnectionScopeHeader");

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
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            ExpandedModeThresholdWidth = SettingsScopeNavigationBreakpoint,
            CompactModeThresholdWidth = 0,
            IsPaneOpen = true,
            OpenPaneLength = 200,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false
        };
        AutomationProperties.SetAutomationId(_connectionScopeNavigation, "ConnectionScopeList");
        _connectionScopeNavigation.RegisterPropertyChangedCallback(NavigationView.OpenPaneLengthProperty, (_, _) => RefreshConnectionScopeMarker());
        AutomationProperties.SetName(_connectionScopeNavigation, DesktopText.Get("AgentScopePicker"));
        AddConnectionScopeItem(_connectionScopeNavigation, "common", DesktopText.Get("AgentScopeCommon"), "ConnectionScopeCommon");
        AddConnectionScopeItem(_connectionScopeNavigation, "mattermost", MrtText.Get("MattermostSectionTitle.Text"), "ConnectionScopeMattermost");
        AddConnectionScopeItem(_connectionScopeNavigation, "slack", MrtText.Get("SlackSectionTitle.Text"), "ConnectionScopeSlack");
        AddConnectionScopeItem(_connectionScopeNavigation, "discord", MrtText.Get("DiscordSectionTitle.Text"), "ConnectionScopeDiscord");
        _connectionScopeNavigation.SelectionChanged += (_, args) =>
        {
            if (_syncingConnectionScopeSelection || args.SelectedItemContainer is not NavigationViewItem item) return;
            QueueConnectionScopeSelection(item.Tag as string ?? "common");
        };
        shell.Children.Add(_connectionScopeNavigation);
        AttachSettingsNavigationResizer(_connectionScopeNavigation, shell, "ConnectionScopeSizer");

        var page = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        _connectionScopeBreadcrumb = CreateScopeBreadcrumb("ConnectionScopeBreadcrumb", () => SetConnectionSettingsScope("common"));
        page.Children.Add(_connectionScopeBreadcrumb);
        page.Children.Add(_connectionScopeHeader);
        page.Children.Add(_connectionScopeCommonGroup);
        page.Children.Add(_connectionScopeMattermostGroup);
        page.Children.Add(_connectionScopeSlackGroup);
        page.Children.Add(_connectionScopeDiscordGroup);
        _connectionSettingsScroll = CreateSettingsDetailScroll(page, "ConnectionSettingsScroll");
        _connectionScopeNavigation.Content = _connectionSettingsScroll;
        originalContent.Children.Add(shell);

        _connectionScopeNavigation.SizeChanged += (_, args) => UpdateConnectionScopeNavigation(args.NewSize.Width);
        SetConnectionSettingsScope("common");
        RefreshConnectionScopeLabels(System.Globalization.CultureInfo.CurrentUICulture);
        UpdateConnectionScopeNavigation(_connectionScopeNavigation.ActualWidth);
        UpdateSettingsNavigationEnabledState();
    }

    private void QueueConnectionScopeSelection(string scope)
    {
        _pendingConnectionScope = scope;
        if (_connectionScopeDispatchScheduled) return;
        _connectionScopeDispatchScheduled = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _connectionScopeDispatchScheduled = false;
            if (_pendingConnectionScope is not { } next) return;
            _pendingConnectionScope = null;
            var timing = System.Diagnostics.Stopwatch.StartNew();
            var succeeded = false;
            try { SetConnectionSettingsScope(next); succeeded = true; }
            finally { ChatAgentBridge.Core.Diagnostics.SettingsNavigationDiagnostics.Record("chat_settings", timing.ElapsedMilliseconds, succeeded); }
        })) _connectionScopeDispatchScheduled = false;
    }

    private static T? FindLocalizedElement<T>(Panel panel, string key) where T : FrameworkElement =>
        panel.Children.OfType<T>().FirstOrDefault(element => LocalizedResourceKey.GetKey(element) == key);

    private void AddConnectionScopeItem(NavigationView navigation, string key, string label, string automationId)
    {
        var item = new SettingsScopeNavigationItem(navigation, () => QueueConnectionScopeSelection(key)) { Content = ScopeNavigationLabel(key, label), Tag = key };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, label);
        navigation.MenuItems.Add(item);
    }

    private void UpdateConnectionScopeNavigation(double availableWidth)
    {
        if (_connectionScopeNavigation is null || availableWidth <= 0) return;
        var showNavigation = availableWidth >= SettingsScopeNavigationBreakpoint;
        var mode = showNavigation
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.Top;
        if (_connectionScopeNavigation.PaneDisplayMode == mode) return;
        _connectionScopeNavigation.PaneDisplayMode = mode;
        _connectionScopeNavigation.IsPaneOpen = showNavigation;
        RefreshConnectionScopeMarker();
    }

    private void SetConnectionSettingsScope(string scope)
    {
        if (_connectionScopeHeading is null || _connectionScopeCommonGroup is null ||
            _connectionScopeMattermostGroup is null || _connectionScopeSlackGroup is null || _connectionScopeDiscordGroup is null) return;
        var nextScope = scope is "mattermost" or "slack" or "discord" ? scope : "common";
        var scopeChanged = _connectionSettingsScope != nextScope;
        _connectionSettingsScope = nextScope;
        var isCommon = _connectionSettingsScope == "common";
        _connectionScopeHeading.Text = _connectionSettingsScope switch
        {
            "mattermost" => MrtText.Get("MattermostSectionTitle.Text"),
            "slack" => MrtText.Get("SlackSectionTitle.Text"),
            "discord" => MrtText.Get("DiscordSectionTitle.Text"),
            _ => DesktopText.Get("AgentScopeCommon")
        };
        RefreshConnectionScopeHeader();
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
        UpdateScopeBreadcrumb(_connectionScopeBreadcrumb, "connections", _connectionScopeNavigation, isCommon);
        if (scopeChanged) _connectionSettingsScroll?.ChangeView(null, 0, null, true);
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
        RefreshConnectionScopeMarker();
    }

    private void RefreshConnectionScopeMarker()
    {
        var inbox = InboxPage.ViewModel;
        var provider = inbox.IsConnectionConfirmed && inbox.IsConnected && !inbox.HasConnectionError
            ? inbox.ConnectionProvider
            : null;
        foreach (var key in new[] { "common", "mattermost", "slack", "discord" })
        {
            var isCurrent = key != "common" && provider == key;
            var providerName = key switch { "mattermost" => "Mattermost", "slack" => "Slack", "discord" => "Discord", _ => string.Empty };
            SetScopeNavigationMarker(_connectionScopeNavigation, key, isCurrent,
                DesktopText.Get("CurrentConnectionMarkerTooltip", providerName),
                DesktopText.Get("CurrentConnectionMarkerName"));
        }
        RefreshConnectionScopeHeader();
    }

    /// <summary>Shows the selected service's connection state under its title, matching the agent header.</summary>
    private void RefreshConnectionScopeHeader()
    {
        if (_connectionScopeHeader is null || _connectionScopeHeading is null || _connectionScopeConnectedBadge is null ||
            _connectionScopeStatusRow is null || _connectionScopeStatusIcon is null || _connectionScopeStatusText is null) return;
        var isCommon = _connectionSettingsScope == "common";
        _connectionScopeHeader.Visibility = Visibility.Visible;
        _connectionScopeConnectedBadge.Visibility = Visibility.Collapsed;
        _connectionScopeStatusRow.Visibility = isCommon ? Visibility.Collapsed : Visibility.Visible;
        if (isCommon)
        {
            AutomationProperties.SetName(_connectionScopeHeader, _connectionScopeHeading.Text);
            return;
        }

        var inbox = InboxPage.ViewModel;
        var isLive = inbox.IsConnectionConfirmed && inbox.IsConnected && !inbox.HasConnectionError &&
            inbox.ConnectionProvider == _connectionSettingsScope;
        var hasError = !isLive && inbox.ConnectionProvider == _connectionSettingsScope && inbox.HasConnectionError;
        var (glyph, styleKey, statusKey) = isLive
            ? ("\uE930", "ConnectionStatusConnectedIconStyle", "ConnectionStatusConnected")
            : hasError
                ? ("\uE783", "ConnectionStatusErrorIconStyle", "ConnectionStatusError")
                : ("\uE946", "ConnectionStatusNeutralIconStyle", "ConnectionStatusDisconnected");
        var status = DesktopText.Get(statusKey);
        if (ViewModel.PersistedSettings?.AutoConnectProvider == _connectionSettingsScope)
            status += " · " + DesktopText.Get("ConnectionStatusAutoConnect");

        _connectionScopeConnectedBadge.Visibility = isLive ? Visibility.Visible : Visibility.Collapsed;
        _connectionScopeStatusIcon.Glyph = glyph;
        _connectionScopeStatusIcon.Style = (Style)Application.Current.Resources[styleKey];
        _connectionScopeStatusText.Text = status;
        AutomationProperties.SetName(_connectionScopeHeader, $"{_connectionScopeHeading.Text}, {status}");
    }

}
