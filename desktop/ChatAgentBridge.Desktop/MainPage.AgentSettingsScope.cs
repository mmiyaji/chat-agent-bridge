using ChatAgentBridge_Desktop.AgentSettings;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    // The same available-width threshold as the chat and history split panes.
    private const double SettingsScopeNavigationBreakpoint = 700;
    private readonly string[] _agentScopeKeys = ["common", "codex", "claude", "gemini", "github-copilot", "antigravity", "custom"];
    private AgentProfileEditorControl? _agentProfileDetails;
    private NavigationView? _agentScopeNavigation;
    private ScrollViewer? _agentSettingsScroll;
    private StackPanel? _agentScopeCommonGroup;
    private TextBlock? _agentScopeCommonHeading;
    private TextBlock? _agentScopeCommonHelp;
    private string _agentSettingsScope = "common";
    private bool _syncingAgentScope;
    private bool _changingAgentScope;
    private bool _agentScopeInitialized;
    private string? _pendingAgentScope;
    private bool _agentScopeDispatchScheduled;
    private (string Title, string Message)? _agentScopeLoadNotice;

    private void InitializeAgentSettingsScope()
    {
        if (AgentBasicSettings.Parent is not Panel original ||
            AgentBasicSettings.Content is not StackPanel basic || basic.Children.FirstOrDefault() is not StackPanel workspace) return;
        original.Children.Clear();
        basic.Children.Remove(workspace);
        _agentScopeCommonHeading = new TextBlock { Style = (Style)Application.Current.Resources["SettingsScopeTitleStyle"], TextWrapping = TextWrapping.Wrap };
        _agentScopeCommonHelp = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _agentScopeCommonGroup = new StackPanel { Spacing = 16 };
        _agentScopeCommonGroup.Children.Add(_agentScopeCommonHeading);
        _agentScopeCommonGroup.Children.Add(_agentScopeCommonHelp);
        _agentScopeCommonGroup.Children.Add(workspace);
        AgentCommonSettings.IsExpanded = true;
        _agentScopeCommonGroup.Children.Add(AgentCommonSettings);
        _agentScopeCommonGroup.Children.Add(AgentExecutionExplanation);
        AutomationProperties.SetAutomationId(_agentScopeCommonGroup, "AgentSharedSettings");

        _agentProfileDetails = new AgentProfileEditorControl();
        _agentProfileDetails.Configure(ViewModel, ConfirmSensitiveSettingsAsync, ReloadProcessEnvironment);
        _agentProfileDetails.OpenCommonSettings += async () => await SelectAgentSettingsScopeAsync("common");

        _agentScopeNavigation = new NavigationView
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            // Match the app's manual Left/Top breakpoint, including native item-template states.
            ExpandedModeThresholdWidth = SettingsScopeNavigationBreakpoint,
            CompactModeThresholdWidth = 0,
            IsPaneOpen = true,
            OpenPaneLength = 200,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false
        };
        AutomationProperties.SetAutomationId(_agentScopeNavigation, "AgentScopeList");
        _agentScopeNavigation.RegisterPropertyChangedCallback(NavigationView.OpenPaneLengthProperty, (_, _) => RefreshAgentScopeMarker());
        AutomationProperties.SetName(_agentScopeNavigation, DesktopText.Get("AgentScopePicker"));
        for (var i = 0; i < _agentScopeKeys.Length; i++)
        {
            var key = _agentScopeKeys[i];
            var item = new SettingsScopeNavigationItem(_agentScopeNavigation, () => QueueAgentScopeSelection(key)) { Tag = key, Content = key };
            AutomationProperties.SetAutomationId(item, "AgentScope" + (key switch { "common" => "Common", "codex" => "Codex", "claude" => "Claude", "gemini" => "Gemini", "github-copilot" => "Copilot", "antigravity" => "Antigravity", _ => "Custom" }));
            _agentScopeNavigation.MenuItems.Add(item);
        }
        _agentScopeNavigation.SelectionChanged += (_, args) =>
        {
            if (!_syncingAgentScope && args.SelectedItemContainer is NavigationViewItem { Tag: string scope })
                QueueAgentScopeSelection(scope);
        };
        var page = new StackPanel { Spacing = 16, Margin = new Thickness(16) };
        _agentScopeBreadcrumb = CreateScopeBreadcrumb("AgentScopeBreadcrumb", () => QueueAgentScopeSelection("common"));
        page.Children.Add(_agentScopeBreadcrumb);
        page.Children.Add(_agentScopeCommonGroup);
        page.Children.Add(_agentProfileDetails);
        _agentSettingsScroll = CreateSettingsDetailScroll(page, "AgentSettingsScroll");
        _agentScopeNavigation.Content = _agentSettingsScroll;
        var shell = new Grid();
        shell.Children.Add(_agentScopeNavigation);
        AttachSettingsNavigationResizer(_agentScopeNavigation, shell, "AgentScopeSizer");
        original.Children.Add(shell);
        _agentScopeNavigation.SizeChanged += (_, args) => UpdateAgentScopeLayout(args.NewSize.Width);
        Loaded += (_, _) => RefreshAgentScopeLabels();
        ApplyAgentScopeSelection();
        RefreshAgentScopeLabels();
        UpdateAgentScopeLayout(_agentScopeNavigation.ActualWidth);
        UpdateSettingsNavigationEnabledState();
    }

    private void UpdateAgentScopeLayout(double width)
    {
        if (_agentScopeNavigation is null || width <= 0) return;
        var mode = width >= SettingsScopeNavigationBreakpoint
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.Top;
        if (_agentScopeNavigation.PaneDisplayMode == mode) return;
        _agentScopeNavigation.PaneDisplayMode = mode;
        _agentScopeNavigation.IsPaneOpen = width >= SettingsScopeNavigationBreakpoint;
        RefreshAgentScopeMarker();
    }

    private void QueueAgentScopeSelection(string scope)
    {
        _pendingAgentScope = scope;
        if (_agentScopeDispatchScheduled) return;
        _agentScopeDispatchScheduled = true;
        // Top navigation moves the clicked overflow container into its primary
        // host after SelectionChanged returns. Do not disable or reselect those
        // containers from inside that event (including before an async await).
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                while (_pendingAgentScope is { } next)
                {
                    _pendingAgentScope = null;
                    await SelectAgentSettingsScopeAsync(next);
                }
            }
            finally { _agentScopeDispatchScheduled = false; }
        })) _agentScopeDispatchScheduled = false;
    }

    private void RefreshAgentScopeLabels()
    {
        if (_agentScopeNavigation is null) return;
        string[] labels = [DesktopText.Get("AgentScopeCommon"), "Codex", "Claude Code", "Gemini CLI", "GitHub Copilot", "Antigravity CLI", DesktopText.Get("AgentScopeCustom")];
        string[] iconKeys = ["common", "codex", "claude", "gemini", "github-copilot", "antigravity", "custom"];
        for (var i = 0; i < labels.Length; i++)
        {
            var item = (NavigationViewItem)_agentScopeNavigation.MenuItems[i];
            item.Content = ScopeNavigationLabel(iconKeys[i], labels[i]);
            AutomationProperties.SetName(item, labels[i]);
        }
        AutomationProperties.SetName(_agentScopeNavigation, DesktopText.Get("AgentScopePicker"));
        _agentScopeCommonHeading!.Text = DesktopText.Get("AgentScopeCommon");
        _agentScopeCommonHelp!.Text = DesktopText.Get("AgentScopeCommonHelp");
        _agentProfileDetails?.RefreshLocalizedText();
        UpdateScopeBreadcrumb(_agentScopeBreadcrumb, "agent", _agentScopeNavigation, _agentSettingsScope == "common");
        RefreshAgentScopeMarker();
    }

    private void RefreshAgentScopeMarker()
    {
        var provider = ViewModel.PersistedSettings?.AgentProvider;
        foreach (var key in _agentScopeKeys)
        {
            var isCurrent = key != "common" && provider == key;
            var providerName = key switch
            {
                "codex" => "Codex",
                "claude" => "Claude Code",
                "gemini" => "Gemini CLI",
                "github-copilot" => "GitHub Copilot",
                "antigravity" => "Antigravity CLI",
                "custom" => DesktopText.Get("AgentScopeCustom"),
                _ => string.Empty
            };
            SetScopeNavigationMarker(_agentScopeNavigation, key, isCurrent,
                DesktopText.Get("CurrentAgentMarkerTooltip", providerName),
                DesktopText.Get("CurrentAgentMarkerName"));
        }
    }

    private void ApplyAgentScopeSelection()
    {
        _syncingAgentScope = true;
        var index = Array.IndexOf(_agentScopeKeys, _agentSettingsScope);
        if (_agentScopeNavigation is not null && index >= 0 && index < _agentScopeNavigation.MenuItems.Count)
            _agentScopeNavigation.SelectedItem = _agentScopeNavigation.MenuItems[index];
        if (_agentScopeCommonGroup is not null) _agentScopeCommonGroup.Visibility = ToVisibility(_agentSettingsScope == "common");
        if (_agentProfileDetails is not null) _agentProfileDetails.Visibility = ToHiddenVisibility(_agentSettingsScope == "common");
        UpdateScopeBreadcrumb(_agentScopeBreadcrumb, "agent", _agentScopeNavigation, _agentSettingsScope == "common");
        _syncingAgentScope = false;
    }

    private async Task<bool> SelectAgentSettingsScopeAsync(string scope)
    {
        if (_changingAgentScope || _agentProfileDetails is null || !_agentScopeKeys.Contains(scope)) return false;
        _changingAgentScope = true;
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var succeeded = false;
        try
        {
            if (!await ViewModel.SavePendingSettingsAsync() || !await _agentProfileDetails.TryFlushAsync()) return false;
            if (scope != "common" && !await _agentProfileDetails.SelectProviderAsync(scope))
            {
                var editor = _agentProfileDetails.Editor;
                if (!editor.IsSettingsLoaded && editor.IsStatusOpen && editor.StatusSeverity == "Error")
                {
                    var title = DesktopText.Get("AgentScopeReadFailedTitle");
                    var message = DesktopText.Get("AgentScopeReadFailedDetails");
                    _agentScopeLoadNotice = (title, message);
                    ViewModel.StatusTitle = title;
                    ViewModel.StatusMessage = message;
                    ViewModel.StatusSeverity = "Error";
                    ViewModel.IsStatusOpen = true;
                }
                return false;
            }
            if (_agentScopeLoadNotice is { } notice)
            {
                if (ViewModel.StatusTitle == notice.Title && ViewModel.StatusMessage == notice.Message)
                    ViewModel.IsStatusOpen = false;
                _agentScopeLoadNotice = null;
            }
            _agentSettingsScope = scope;
            _agentScopeInitialized = true;
            _agentSettingsScroll?.ChangeView(null, 0, null, true);
            succeeded = true;
            return true;
        }
        finally
        {
            ChatAgentBridge.Core.Diagnostics.SettingsNavigationDiagnostics.Record("agent_settings", timing.ElapsedMilliseconds, succeeded);
            if (_pendingAgentScope is null) ApplyAgentScopeSelection();
            _changingAgentScope = false;
        }
    }

    private async Task EnsureAgentSettingsScopeAsync()
    {
        if (!_agentScopeInitialized && ViewModel.IsSettingsLoaded)
            await SelectAgentSettingsScopeAsync(ViewModel.AgentProvider);
    }
}
