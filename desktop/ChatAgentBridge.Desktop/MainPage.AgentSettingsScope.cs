using ChatAgentBridge_Desktop.AgentSettings;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private readonly string[] _agentScopeKeys = ["common", "codex", "claude", "gemini", "github-copilot", "custom"];
    private AgentProfileEditorControl? _agentProfileDetails;
    private NavigationView? _agentScopeNavigation;
    private StackPanel? _agentScopeCommonGroup;
    private TextBlock? _agentScopeCommonHeading;
    private TextBlock? _agentScopeCommonHelp;
    private string _agentSettingsScope = "common";
    private bool _syncingAgentScope;
    private bool _changingAgentScope;
    private bool _agentScopeInitialized;

    private void InitializeAgentSettingsScope()
    {
        if (AgentBasicSettings.Parent is not StackPanel original ||
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
        _agentProfileDetails.PropertyChanged += (_, _) => UpdateAgentScopeEnabled();

        _agentScopeNavigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneOpen = true,
            OpenPaneLength = 200,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false
        };
        AutomationProperties.SetAutomationId(_agentScopeNavigation, "AgentScopeList");
        AutomationProperties.SetName(_agentScopeNavigation, DesktopText.Get("AgentScopePicker"));
        for (var i = 0; i < _agentScopeKeys.Length; i++)
        {
            var key = _agentScopeKeys[i];
            var item = new NavigationViewItem { Tag = key };
            AutomationProperties.SetAutomationId(item, "AgentScope" + (key switch { "common" => "Common", "codex" => "Codex", "claude" => "Claude", "gemini" => "Gemini", "github-copilot" => "Copilot", _ => "Custom" }));
            _agentScopeNavigation.MenuItems.Add(item);
        }
        _agentScopeNavigation.SelectionChanged += async (_, args) =>
        {
            if (!_syncingAgentScope && args.SelectedItemContainer is NavigationViewItem { Tag: string scope })
                await SelectAgentSettingsScopeAsync(scope);
        };
        var page = new StackPanel { Spacing = 16, Margin = new Thickness(16) };
        page.Children.Add(_agentScopeCommonGroup);
        page.Children.Add(_agentProfileDetails);
        _agentScopeNavigation.Content = page;
        original.Children.Add(_agentScopeNavigation);
        original.Spacing = 0;
        _agentScopeNavigation.SizeChanged += (_, args) => UpdateAgentScopeLayout(args.NewSize.Width);
        Loaded += (_, _) => RefreshAgentScopeLabels();
        ApplyAgentScopeSelection();
        RefreshAgentScopeLabels();
        UpdateAgentScopeLayout(_agentScopeNavigation.ActualWidth);
    }

    private void UpdateAgentScopeLayout(double width)
    {
        if (_agentScopeNavigation is null) return;
        _agentScopeNavigation.PaneDisplayMode = width >= 1000
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.Top;
        _agentScopeNavigation.IsPaneOpen = width >= 1000;
    }

    private void UpdateAgentScopeEnabled()
    {
        var enabled = !_changingAgentScope && (_agentProfileDetails?.CanChangeSelection ?? true);
        // Disable navigation alone, not NavigationView.Content: the running
        // check's cancel button and its feedback must remain usable.
        if (_agentScopeNavigation is not null)
            foreach (var item in _agentScopeNavigation.MenuItems.OfType<NavigationViewItem>())
                item.IsEnabled = enabled;
    }

    private void RefreshAgentScopeLabels()
    {
        if (_agentScopeNavigation is null) return;
        string[] labels = [DesktopText.Get("AgentScopeCommon"), "Codex", "Claude Code", "Gemini CLI", "GitHub Copilot", DesktopText.Get("AgentScopeCustom")];
        string[] iconKeys = ["common", "codex", "claude", "gemini", "github-copilot", "custom"];
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
    }

    private void ApplyAgentScopeSelection()
    {
        _syncingAgentScope = true;
        var index = Array.IndexOf(_agentScopeKeys, _agentSettingsScope);
        if (_agentScopeNavigation is not null && index >= 0 && index < _agentScopeNavigation.MenuItems.Count)
            _agentScopeNavigation.SelectedItem = _agentScopeNavigation.MenuItems[index];
        if (_agentScopeCommonGroup is not null) _agentScopeCommonGroup.Visibility = ToVisibility(_agentSettingsScope == "common");
        if (_agentProfileDetails is not null) _agentProfileDetails.Visibility = ToHiddenVisibility(_agentSettingsScope == "common");
        _syncingAgentScope = false;
    }

    private async Task<bool> SelectAgentSettingsScopeAsync(string scope)
    {
        if (_changingAgentScope || _agentProfileDetails is null || !_agentScopeKeys.Contains(scope)) { ApplyAgentScopeSelection(); return false; }
        _changingAgentScope = true;
        UpdateAgentScopeEnabled();
        try
        {
            if (!await ViewModel.SavePendingSettingsAsync() || !await _agentProfileDetails.TryFlushAsync()) return false;
            if (scope != "common" && !await _agentProfileDetails.SelectProviderAsync(scope)) return false;
            _agentSettingsScope = scope;
            _agentScopeInitialized = true;
            MainContentScroll.ChangeView(null, 0, null, true);
            return true;
        }
        finally
        {
            ApplyAgentScopeSelection();
            _changingAgentScope = false;
            UpdateAgentScopeEnabled();
        }
    }

    private async Task EnsureAgentSettingsScopeAsync()
    {
        if (!_agentScopeInitialized && ViewModel.IsSettingsLoaded)
            await SelectAgentSettingsScopeAsync(ViewModel.AgentProvider);
    }
}
