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
    private ListView? _agentScopeNavigation;
    private ComboBox? _agentScopePicker;
    private Grid? _agentScopeShell;
    private StackPanel? _agentScopePage;
    private StackPanel? _agentScopeCommonGroup;
    private TextBlock? _agentScopeCommonHeading;
    private TextBlock? _agentScopeCommonHelp;
    private string _agentSettingsScope = "common";
    private bool _syncingAgentScope;
    private bool _changingAgentScope;
    private bool _agentScopeInitialized;

    private static StackPanel ScopeNavigationLabel(Symbol symbol, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var icon = new SymbolIcon(symbol) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

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

        _agentScopeNavigation = new ListView { SelectionMode = ListViewSelectionMode.Single, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(_agentScopeNavigation, "AgentScopeList");
        _agentScopePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 16) };
        AutomationProperties.SetAutomationId(_agentScopePicker, "AgentScopePicker");
        for (var i = 0; i < _agentScopeKeys.Length; i++)
        {
            var key = _agentScopeKeys[i];
            var item = new ListViewItem { Tag = key };
            AutomationProperties.SetAutomationId(item, "AgentScope" + (key switch { "common" => "Common", "codex" => "Codex", "claude" => "Claude", "gemini" => "Gemini", "github-copilot" => "Copilot", _ => "Custom" }));
            _agentScopeNavigation.Items.Add(item);
            _agentScopePicker.Items.Add(new ComboBoxItem { Tag = key });
        }
        _agentScopeNavigation.SelectionChanged += async (_, _) =>
        {
            if (!_syncingAgentScope && _agentScopeNavigation.SelectedItem is ListViewItem { Tag: string scope })
                await SelectAgentSettingsScopeAsync(scope);
        };
        _agentScopePicker.SelectionChanged += async (_, _) =>
        {
            if (!_syncingAgentScope && _agentScopePicker.SelectedItem is ComboBoxItem { Tag: string scope })
                await SelectAgentSettingsScopeAsync(scope);
        };
        _agentScopePage = new StackPanel { Spacing = 16 };
        _agentScopePage.Children.Add(_agentScopeCommonGroup);
        _agentScopePage.Children.Add(_agentProfileDetails);
        _agentScopeShell = new Grid { ColumnSpacing = 24 };
        _agentScopeShell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        _agentScopeShell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _agentScopeShell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _agentScopeShell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _agentScopeShell.Children.Add(_agentScopeNavigation);
        _agentScopeShell.Children.Add(_agentScopePicker);
        _agentScopeShell.Children.Add(_agentScopePage);
        original.Children.Add(_agentScopeShell);
        original.Spacing = 0;
        original.SizeChanged += (_, args) => UpdateAgentScopeLayout(args.NewSize.Width);
        Loaded += (_, _) => RefreshAgentScopeLabels();
        ApplyAgentScopeSelection();
        RefreshAgentScopeLabels();
        UpdateAgentScopeLayout(original.ActualWidth);
    }

    private void UpdateAgentScopeLayout(double width)
    {
        if (_agentScopeShell is null || _agentScopeNavigation is null || _agentScopePicker is null || _agentScopePage is null) return;
        var wide = width >= 760;
        _agentScopeNavigation.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _agentScopePicker.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(_agentScopePicker, 2);
        Grid.SetRow(_agentScopePage, wide ? 0 : 1);
        Grid.SetColumn(_agentScopePage, wide ? 1 : 0);
        Grid.SetColumnSpan(_agentScopePage, wide ? 1 : 2);
    }

    private void UpdateAgentScopeEnabled()
    {
        var enabled = !_changingAgentScope && (_agentProfileDetails?.CanChangeSelection ?? true);
        if (_agentScopeNavigation is not null) _agentScopeNavigation.IsEnabled = enabled;
        if (_agentScopePicker is not null) _agentScopePicker.IsEnabled = enabled;
    }

    private void RefreshAgentScopeLabels()
    {
        if (_agentScopeNavigation is null || _agentScopePicker is null) return;
        string[] labels = [DesktopText.Get("AgentScopeCommon"), "Codex", "Claude Code", "Gemini CLI", "GitHub Copilot", DesktopText.Get("AgentScopeCustom")];
        for (var i = 0; i < labels.Length; i++)
        {
            var item = (ListViewItem)_agentScopeNavigation.Items[i];
            item.Content = ScopeNavigationLabel(i == 0 ? Symbol.Folder : Symbol.Library, labels[i]);
            AutomationProperties.SetName(item, labels[i]);
            var choice = (ComboBoxItem)_agentScopePicker.Items[i];
            choice.Content = labels[i];
            AutomationProperties.SetName(choice, labels[i]);
        }
        AutomationProperties.SetName(_agentScopeNavigation, DesktopText.Get("AgentScopePicker"));
        AutomationProperties.SetName(_agentScopePicker, DesktopText.Get("AgentScopePicker"));
        _agentScopePicker.Header = DesktopText.Get("AgentScopePicker");
        _agentScopeCommonHeading!.Text = DesktopText.Get("AgentScopeCommon");
        _agentScopeCommonHelp!.Text = DesktopText.Get("AgentScopeCommonHelp");
        _agentProfileDetails?.RefreshLocalizedText();
    }

    private void ApplyAgentScopeSelection()
    {
        _syncingAgentScope = true;
        var index = Array.IndexOf(_agentScopeKeys, _agentSettingsScope);
        if (_agentScopeNavigation is not null) _agentScopeNavigation.SelectedIndex = index;
        if (_agentScopePicker is not null) _agentScopePicker.SelectedIndex = index;
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
