using ChatAgentBridge_Desktop.ViewModels;
using ChatAgentBridge_Desktop.Inbox;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage : Page
{
    private bool _hasLoaded;
    private string? _selectedNavigationTag;
    private int _scrollResetGeneration;
    private int _languageRefreshGeneration;
    private System.Globalization.CultureInfo? _inProcessLanguage;
    // Pages whose layout has already been re-localized for _inProcessLanguage.
    private readonly HashSet<string> _languageRefreshedTags = new(StringComparer.Ordinal);
    private bool _handlingStartupToggle;
    private bool _refreshingAutoConnectSelection;
    private readonly Onboarding.OnboardingWizard _wizard;

    public MainPageViewModel ViewModel { get; } = new();
    public bool IsOverviewSelected { get; private set; } = true;
    public bool IsWorkbenchSelected { get; private set; }
    public bool IsAgentSelected { get; private set; }
    public bool IsConnectionsSelected { get; private set; }
    public bool IsChatSelected { get; private set; }
    public bool IsHistorySelected { get; private set; }
    public bool IsAboutSelected { get; private set; }
    public bool IsFormSelected => !IsChatSelected && !IsHistorySelected && !IsAboutSelected;
    public bool IsScrollableFormSelected => IsOverviewSelected || IsWorkbenchSelected;
    public bool IsScopedSettingsSelected => IsAgentSelected || IsConnectionsSelected;
    public static bool CanUseForm(bool isGeneralSelected, bool isSettingsLoaded, bool isLoading) => !isLoading && (isGeneralSelected || isSettingsLoaded);

    public MainPage()
    {
        InitializeComponent();
        PasswordRevealControl.Attach(MattermostTokenRevealButton, MattermostTokenBox);
        PasswordRevealControl.Attach(SlackBotTokenRevealButton, SlackBotTokenBox);
        PasswordRevealControl.Attach(SlackAppTokenRevealButton, SlackAppTokenBox);
        PasswordRevealControl.Attach(DiscordBotTokenRevealButton, DiscordBotTokenBox);
        InitializeSettingsAutoSave();
        InitializeLanguageRestart();
        InitializePortableUpdateBadge();
        InitializePortableUpdateRecoveryProbe();
        InitializePendingBadge();
        UpdateNavigationContentOffset();
        _wizard = new Onboarding.OnboardingWizard(ViewModel, InboxPage.ViewModel, ReloadProcessEnvironment);
        WizardHost.Content = _wizard;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsSettingsLoaded) or nameof(ViewModel.IsLoading))
                UpdateSettingsNavigationEnabledState();
            if (args.PropertyName == nameof(ViewModel.PersistedSettings))
            {
                RefreshAgentScopeMarker();
                RefreshConnectionScopeHeader();
            }
            // PasswordBox cannot use a two-way x:Bind. Reflect rejected edits back
            // into its editor as well as the view model, without persisting them.
            if (args.PropertyName == nameof(ViewModel.MattermostToken))
                MattermostTokenBox.Password = ViewModel.MattermostToken;
            if (args.PropertyName == nameof(ViewModel.SlackBotToken))
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
            if (args.PropertyName == nameof(ViewModel.SlackAppToken))
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
            if (args.PropertyName == nameof(ViewModel.DiscordBotToken))
                DiscordBotTokenBox.Password = ViewModel.DiscordBotToken;
            if (ViewModel.IsOnboardingActive && ViewModel.IsStatusOpen && args.PropertyName is nameof(ViewModel.IsStatusOpen) or nameof(ViewModel.StatusTitle) or nameof(ViewModel.StatusMessage))
                _wizard.ResetScroll();
            if (args.PropertyName == nameof(ViewModel.IsSettingsLoaded) && ViewModel.IsSettingsLoaded)
            {
                MattermostTokenBox.Password = ViewModel.MattermostToken;
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
                DiscordBotTokenBox.Password = ViewModel.DiscordBotToken;
                _wizard.SyncSecrets();
            }
            if (args.PropertyName == nameof(ViewModel.IsOnboardingActive))
            {
                MattermostTokenBox.Password = ViewModel.MattermostToken;
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
                DiscordBotTokenBox.Password = ViewModel.DiscordBotToken;
                _wizard.SyncSecrets();
                if (!ViewModel.IsOnboardingActive) ScheduleDeferredLanguageRefresh();
            }
        };
        InboxPage.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatInboxViewModel.IsConnectionConfirmed) or nameof(ChatInboxViewModel.IsConnected) or nameof(ChatInboxViewModel.HasConnectionError) or nameof(ChatInboxViewModel.ConnectionProvider))
            {
                ViewModel.SetChatConnectionState(InboxPage.ViewModel.IsConnectionConfirmed, InboxPage.ViewModel.IsConnected, InboxPage.ViewModel.HasConnectionError, InboxPage.ViewModel.ConnectionOverviewTarget);
                RefreshConnectionScopeMarker();
            }
        };
        ViewModel.SettingsSaved += () =>
        {
            _ = InboxPage.ViewModel.RefreshAccessAsync();
            if (IsAgentSelected) _ = EnsureAgentSettingsScopeAsync();
        };
        InboxPage.OpenRequested += () => MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .First(item => item.Tag as string == "chat");
        Loaded += MainPage_Loaded;
        Loaded += (_, _) =>
        {
            LanguageSettings.LanguageChanged -= RefreshLanguage;
            LanguageSettings.LanguageChanged += RefreshLanguage;
        };
        Unloaded += (_, _) => LanguageSettings.LanguageChanged -= RefreshLanguage;
        ViewModel.ConfirmExecutionAsync = ConfirmExecutionAsync;
        var serverDialogs = new AppServerUi.AppServerDialogs(() => XamlRoot);
        ViewModel.ServerApprovalHandler = serverDialogs.ApproveAsync;
        ViewModel.ServerInputHandler = serverDialogs.InputAsync;
        HistoryPage.AgentSettingsRequested += () => OpenAgentSettings_Click(this, new RoutedEventArgs());
        InboxPage.ConnectionSettingsRequested += () => OpenConnections_Click(this, new RoutedEventArgs());
        ViewModel.OnboardingNavigateRequested += _ => _wizard.ResetScroll();
        // Search is a command row, so keep General as the initial selected page.
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .First(item => item.Tag as string == "overview");
    }

    private void RefreshLanguage(System.Globalization.CultureInfo culture)
    {
        _inProcessLanguage = culture;
        _languageRefreshedTags.Clear();
        ViewModel.RefreshLocalizedText();
        InboxPage.ViewModel.RefreshLocalizedText();
        HistoryPage.ViewModel.RefreshLocalizedText();
        Bindings.Update();
        MrtText.Refresh(this, culture);
        RefreshAgentScopeLabels();
        RefreshConnectionScopeLabels(culture);
        RefreshAutoConnectSelectionBox();
        RefreshPortableUpdateBadge();
        if (!ViewModel.IsOnboardingActive) ScheduleDeferredLanguageRefresh();
    }

    private void ScheduleDeferredLanguageRefresh()
    {
        if (_inProcessLanguage is not { } culture || ViewModel.IsOnboardingActive) return;
        // Each page needs the deferred pass once per language change, not on every visit.
        var tag = _selectedNavigationTag;
        if (tag is not null && _languageRefreshedTags.Contains(tag)) return;
        var generation = ++_languageRefreshGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _languageRefreshGeneration || ViewModel.IsOnboardingActive) return;
            UpdateLayout();
            MainNavigation.UpdateLayout();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _languageRefreshGeneration || ViewModel.IsOnboardingActive) return;
                UpdateLayout();
                MrtText.Refresh(this, culture);
                RefreshAutoConnectSelectionBox();
                if (tag is not null) _languageRefreshedTags.Add(tag);
            });
        });
    }

    private void RefreshAutoConnectSelectionBox()
    {
        if (AutoConnectProviderPicker.SelectedValue is not string selectedValue) return;
        _refreshingAutoConnectSelection = true;
        try
        {
            AutoConnectProviderPicker.SelectedIndex = -1;
            AutoConnectProviderPicker.SelectedValue = selectedValue;
        }
        finally
        {
            _refreshingAutoConnectSelection = false;
        }
    }

    private void AutoConnectProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAutoConnectSelection || sender is not ComboBox { SelectedValue: string value }) return;
        ViewModel.AutoConnectProvider = value;
    }

    private bool _followUpDialogOpen;

    private async void ChatFollowUpMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Programmatic changes (loading, discarding a draft, reverting) must not ask again.
        if (sender is not ComboBox { SelectedValue: ChatFollowUpPolicy.Steer } || _followUpDialogOpen ||
            ViewModel.IsApplyingSettings || e.RemovedItems.Count == 0 || XamlRoot is null) return;
        _followUpDialogOpen = true;
        // The selection is already in the draft; hold auto-save so it cannot open its own dialog behind this one.
        _settingsPromptOpen = true;
        try
        {
            if (!await FollowUpSteerDialog.ShowAsync(ViewModel, XamlRoot)) ViewModel.RevertFollowUpToQueue();
        }
        finally
        {
            _settingsPromptOpen = false;
            _followUpDialogOpen = false;
            ScheduleSettingsAutoSave();
        }
    }

    private bool _switchApprovalDialogOpen;

    private async void AutoApproveSwitchCommands_Toggled(object sender, RoutedEventArgs e)
    {
        // Warn only when the user turns it on; loading, discarding, and reverting stay silent.
        if (sender is not ToggleSwitch { IsOn: true } || _switchApprovalDialogOpen ||
            ViewModel.IsApplyingSettings || XamlRoot is null) return;
        _switchApprovalDialogOpen = true;
        _settingsPromptOpen = true;
        try
        {
            if (!await SwitchApprovalWarningDialog.ShowAsync(XamlRoot)) ViewModel.RevertAutoApproveSwitchCommands();
        }
        finally
        {
            _settingsPromptOpen = false;
            _switchApprovalDialogOpen = false;
            ScheduleSettingsAutoSave();
        }
    }

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility SearchTriggerVisibility(bool isSettingsLoaded, bool isOnboardingActive) =>
        isSettingsLoaded && !isOnboardingActive ? Visibility.Visible : Visibility.Collapsed;

    internal async Task ShutdownAsync()
    {
        if (_agentProfileDetails is not null) await _agentProfileDetails.ShutdownAsync();
        ViewModel.CancelForWindowClose();
        InboxPage.ViewModel.Close();
        var tasks = new[]
        {
            ViewModel.RunCommand.ExecutionTask,
            InboxPage.ViewModel.ApproveCommand.ExecutionTask,
            InboxPage.ViewModel.SendAttachmentsCommand.ExecutionTask,
            InboxPage.ViewModel.ConnectMattermostCommand.ExecutionTask,
            InboxPage.ViewModel.ConnectSlackCommand.ExecutionTask,
            InboxPage.ViewModel.ConnectDiscordCommand.ExecutionTask,
            InboxPage.ViewModel.DisconnectCommand.ExecutionTask,
            InboxPage.ViewModel.WaitForConnectionShutdownAsync()
        }.OfType<Task>();
        try { await Task.WhenAll(tasks); }
        finally { await InboxPage.ViewModel.FlushConversationPersistenceAsync(); }
    }
    public static Style OverviewIconStyle(OverviewIconState state) => (Style)Application.Current.Resources[state switch
    {
        OverviewIconState.Ready => "OverviewReadyIconStyle",
        OverviewIconState.Attention => "OverviewAttentionIconStyle",
        OverviewIconState.Error => "OverviewErrorIconStyle",
        _ => "OverviewNeutralIconStyle"
    }];

    private bool _sidebarResizerAttached;

    private void InitializeSidebarResizer()
    {
        if (_sidebarResizerAttached || MainNavigation.Parent is not Panel host) return;
        _sidebarResizerAttached = true;
        Appearance.NavigationPaneResizer.Attach(MainNavigation, host,
            ChatAgentBridge.Core.Appearance.LayoutPreferences.MinSidebarWidth, ChatAgentBridge.Core.Appearance.LayoutPreferences.MaxSidebarWidth,
            App.Layout.Current.SidebarWidth,
            width => App.Layout.Update(layout => layout with { SidebarWidth = width }),
            "SidebarSizer", DesktopText.Get("PaneSizerSidebar"), collapsible: true);
        foreach (var item in MainNavigation.MenuItems.Concat(MainNavigation.FooterMenuItems).OfType<NavigationViewItem>())
            TrimSidebarLabel(item);
    }

    // A narrowed sidebar keeps each label on one line and trims it with an ellipsis. The label text block is
    // generated by the item's template (a ContentTemplate would hide the icon), so it is adjusted once realized.
    private void TrimSidebarLabel(NavigationViewItem item)
    {
        void Apply()
        {
            var pending = new Stack<DependencyObject>([item]);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node is TextBlock text)
                {
                    text.TextWrapping = TextWrapping.NoWrap;
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    text.MaxLines = 1;
                }
                // The template keeps a 14px gap right of the label for a chevron the sidebar never shows.
                else if (node is Grid { Name: "ContentGrid" } grid && grid.Margin.Right != 0)
                    grid.Margin = new Thickness(grid.Margin.Left, grid.Margin.Top, 0, grid.Margin.Bottom);
                for (var index = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node) - 1; index >= 0; index--)
                    pending.Push(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, index));
            }
        }
        item.Loaded += (_, _) => Apply();
        // The connection status label changes at run time and may regenerate its text block.
        item.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => DispatcherQueue.TryEnqueue(Apply));
        Apply();
    }

    // Both settings pages share one menu width, so resizing either keeps them aligned.
    private void AttachSettingsNavigationResizer(NavigationView navigation, Panel host, string automationId)
    {
        Appearance.NavigationPaneResizer.Attach(navigation, host,
            ChatAgentBridge.Core.Appearance.LayoutPreferences.MinSettingsNavigationWidth, ChatAgentBridge.Core.Appearance.LayoutPreferences.MaxSettingsNavigationWidth,
            App.Layout.Current.SettingsNavigationWidth,
            width =>
            {
                App.Layout.Update(layout => layout with { SettingsNavigationWidth = width });
                foreach (var other in new[] { _agentScopeNavigation, _connectionScopeNavigation })
                    if (other is not null && !ReferenceEquals(other, navigation) && other.OpenPaneLength != width)
                        other.OpenPaneLength = width;
            },
            automationId, DesktopText.Get("PaneSizerSettings"));
    }

    private void MainNavigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        UpdateNavigationContentOffset();

    private void UpdateNavigationContentOffset()
    {
        if (MainContentGrid is null || MainNavigation is null) return;

        MainContentGrid.Margin = MainNavigation.DisplayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(0, 48, 0, 0)
            : new Thickness(0);
    }

    private async void ShowTerms_Click(object sender, RoutedEventArgs e) => await Onboarding.PolicyDialog.ShowAsync(XamlRoot, privacy: false);
    private async void ShowPrivacy_Click(object sender, RoutedEventArgs e) => await Onboarding.PolicyDialog.ShowAsync(XamlRoot, privacy: true);
    public static Visibility ToHiddenVisibility(bool visible) => visible ? Visibility.Collapsed : Visibility.Visible;
    public static bool CanReloadSettings(bool isLoading, bool isRunning) => !isLoading && !isRunning;
    public static InfoBarSeverity ToInfoBarSeverity(string severity) => severity switch
    {
        "Success" => InfoBarSeverity.Success,
        "Warning" => InfoBarSeverity.Warning,
        "Error" => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    public static bool SupportsOperationApproval(bool isCodexProvider, bool useAppServer) => isCodexProvider && useAppServer;

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeSidebarResizer();
        InitializeNavigationShortcuts();
        if (_hasLoaded) return;
        _hasLoaded = true;
        // The Windows startup-task query does not depend on saved settings, so it
        // overlaps with the settings load. Onboarding needs the loaded settings.
        var startupRefresh = ViewModel.RefreshStartupAsync();
        await ViewModel.LoadAsync();
        InitializeAgentSettingsScope();
        InitializeConnectionSettingsScope();
        if (IsAgentSelected) await EnsureAgentSettingsScopeAsync();
        var startupProvider = ViewModel.AutoConnectProvider;
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        DiscordBotTokenBox.Password = ViewModel.DiscordBotToken;
        await ViewModel.InitializeOnboardingAsync();
        await startupRefresh;
        ViewModel.RefreshOverviewStatus();
        if (!App.IsNotificationOnlyLaunch && ViewModel.IsSettingsLoaded && !ViewModel.IsOnboardingActive && !ViewModel.IsLoading &&
            !ViewModel.HasUnsavedSettings && !ViewModel.IsSensitiveConfirmationPending &&
            ViewModel.AutoConnectProvider == startupProvider)
        {
            if (startupProvider is "mattermost" or "slack" or "discord")
                MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");
            if (startupProvider == "mattermost") await InboxPage.ViewModel.ConnectMattermostCommand.ExecuteAsync(null);
            else if (startupProvider == "slack") await InboxPage.ViewModel.ConnectSlackCommand.ExecuteAsync(null);
            else if (startupProvider == "discord") await InboxPage.ViewModel.ConnectDiscordCommand.ExecuteAsync(null);
        }
        InitializeFeatureTips();
    }

    private async void ReloadSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryResolveUnsavedSettingsAsync()) return;
        await ViewModel.ReloadSettingsAsync();
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        DiscordBotTokenBox.Password = ViewModel.DiscordBotToken;
    }

    private async void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            if (result is not null) ViewModel.WorkspaceRoot = result.Path;
        }
        catch (Exception)
        {
            ViewModel.StatusTitle = ViewModelText.Get("FolderPickerFailedTitle");
            ViewModel.StatusMessage = ViewModelText.Get("FolderPickerFailedMessage");
            ViewModel.StatusSeverity = "Error";
            ViewModel.IsStatusOpen = true;
        }
    }

    private void ApplyNavigationSelection(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        var pageChanged = !string.Equals(_selectedNavigationTag, tag, StringComparison.Ordinal);
        if (pageChanged) PasswordRevealControl.Hide(this);
        _selectedNavigationTag = tag;
        IsOverviewSelected = tag == "overview";
        IsWorkbenchSelected = tag == "workbench";
        IsAgentSelected = tag == "agent";
        IsConnectionsSelected = tag == "connections";
        IsChatSelected = tag == "chat";
        IsHistorySelected = tag == "history";
        IsAboutSelected = tag == "about";
        if (!IsAgentSelected) ViewModel.CancelModelCatalogLookup();
        ViewModel.IsStatusOpen = false;
        if (pageChanged) Bindings.Update();
        if (pageChanged && _hasLoaded) PlayPageEntrance();
        if (pageChanged && _hasLoaded)
        {
            var generation = ++_scrollResetGeneration;
            if (IsFormSelected)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _scrollResetGeneration || !IsFormSelected) return;
                    MainNavigation.UpdateLayout();
                    MainContentScroll.UpdateLayout();
                    MainContentScroll.ChangeView(0, 0, null, true);
                    var scroll = ScrollViewerForPage(tag);
                    scroll?.UpdateLayout();
                    scroll?.ChangeView(0, 0, null, true);
                });
            }
        }
        if (IsHistorySelected) _ = HistoryPage.ViewModel.RefreshIfStaleAsync();
        if (pageChanged && IsOverviewSelected && _hasLoaded) _ = ViewModel.RefreshStartupAsync();
        if (pageChanged && IsAboutSelected) AboutPage.ResetScroll();
        if (IsAgentSelected) _ = EnsureAgentSettingsScopeAsync();
        if (pageChanged) ScheduleDeferredLanguageRefresh();
    }

    private void OpenWorkbench_Click(object sender, RoutedEventArgs e)
    {
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "workbench");
    }

    private void OpenAgentSettings_Click(object sender, RoutedEventArgs e) =>
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "agent");

    private void OpenConnections_Click(object sender, RoutedEventArgs e) =>
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "connections");

    private void OpenChat_Click(object sender, RoutedEventArgs e) =>
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_handlingStartupToggle || sender is not ToggleSwitch toggle || !ViewModel.CanChangeStartup) return;
        _handlingStartupToggle = true;
        try
        {
            await ViewModel.ChangeStartupAsync(toggle.IsOn);
        }
        finally
        {
            // Updating IsOn can itself raise Toggled; keep the handler guarded.
            toggle.IsOn = ViewModel.StartWithWindows;
            _handlingStartupToggle = false;
        }
    }

    private void MattermostTokenBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.SetMattermostToken(MattermostTokenBox.Password);
    private void SlackBotTokenBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.SlackBotToken = SlackBotTokenBox.Password;
    private void SlackAppTokenBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.SlackAppToken = SlackAppTokenBox.Password;
    private void DiscordBotTokenBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.DiscordBotToken = DiscordBotTokenBox.Password;

    private async Task<bool> ConfirmExecutionAsync(BridgeSettings settings, string prompt)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = ViewModelText.Get("ExecutionConfirmTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = ViewModelText.Get("ExecutionConfirmIntro"), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = ViewModelText.Get("Workspace"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = settings.WorkspaceRoot, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = ViewModelText.Get("ExecutableAndArguments"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = $"{settings.AgentExecutable} {(settings.AgentMode == "app-server" ? "app-server" : settings.AgentArguments)}".Trim(), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = ViewModelText.Get("ModelAndReasoning"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = $"{settings.AgentModel ?? ViewModelText.Get("DefaultCliValue")} / {settings.AgentReasoningEffort ?? ViewModelText.Get("DefaultCliValue")}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = ViewModelText.Get("Prompt"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = ViewModelText.Get("Run"),
            CloseButtonText = ViewModelText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "RunConfirmation");
        AutomationProperties.SetName(dialog, ViewModelText.Get("ExecutionConfirmAutomationName"));
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
