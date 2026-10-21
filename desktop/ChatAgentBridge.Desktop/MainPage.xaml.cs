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
    public static bool CanUseForm(bool isGeneralSelected, bool isSettingsLoaded, bool isLoading) => !isLoading && (isGeneralSelected || isSettingsLoaded);

    public MainPage()
    {
        InitializeComponent();
        PasswordRevealControl.Attach(MattermostTokenRevealButton, MattermostTokenBox);
        PasswordRevealControl.Attach(SlackBotTokenRevealButton, SlackBotTokenBox);
        PasswordRevealControl.Attach(SlackAppTokenRevealButton, SlackAppTokenBox);
        InitializeSettingsAutoSave();
        InitializeLanguageRestart();
        InitializePortableUpdateBadge();
        UpdateNavigationContentOffset();
        _wizard = new Onboarding.OnboardingWizard(ViewModel, InboxPage.ViewModel, ReloadProcessEnvironment);
        WizardHost.Content = _wizard;
        ViewModel.PropertyChanged += (_, args) =>
        {
            // PasswordBox cannot use a two-way x:Bind. Reflect rejected edits back
            // into its editor as well as the view model, without persisting them.
            if (args.PropertyName == nameof(ViewModel.MattermostToken))
                MattermostTokenBox.Password = ViewModel.MattermostToken;
            if (args.PropertyName == nameof(ViewModel.SlackBotToken))
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
            if (args.PropertyName == nameof(ViewModel.SlackAppToken))
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
            if (ViewModel.IsOnboardingActive && ViewModel.IsStatusOpen && args.PropertyName is nameof(ViewModel.IsStatusOpen) or nameof(ViewModel.StatusTitle) or nameof(ViewModel.StatusMessage))
                _wizard.ResetScroll();
            if (args.PropertyName == nameof(ViewModel.IsSettingsLoaded) && ViewModel.IsSettingsLoaded)
            {
                MattermostTokenBox.Password = ViewModel.MattermostToken;
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
                _wizard.SyncSecrets();
            }
            if (args.PropertyName == nameof(ViewModel.IsOnboardingActive))
            {
                MattermostTokenBox.Password = ViewModel.MattermostToken;
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
                _wizard.SyncSecrets();
                if (!ViewModel.IsOnboardingActive) ScheduleDeferredLanguageRefresh();
            }
        };
        InboxPage.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatInboxViewModel.IsConnectionConfirmed) or nameof(ChatInboxViewModel.IsConnected) or nameof(ChatInboxViewModel.HasConnectionError))
                ViewModel.SetChatConnectionState(InboxPage.ViewModel.IsConnectionConfirmed, InboxPage.ViewModel.IsConnected, InboxPage.ViewModel.HasConnectionError);
        };
        ViewModel.SettingsSaved += () => _ = InboxPage.ViewModel.RefreshAccessAsync();
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
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }

    private void RefreshLanguage(System.Globalization.CultureInfo culture)
    {
        _inProcessLanguage = culture;
        ViewModel.RefreshLocalizedText();
        InboxPage.ViewModel.RefreshLocalizedText();
        HistoryPage.ViewModel.RefreshLocalizedText();
        Bindings.Update();
        MrtText.Refresh(this, culture);
        RefreshAutoConnectSelectionBox();
        RefreshPortableUpdateBadge();
        if (!ViewModel.IsOnboardingActive) ScheduleDeferredLanguageRefresh();
    }

    private void ScheduleDeferredLanguageRefresh()
    {
        if (_inProcessLanguage is not { } culture || ViewModel.IsOnboardingActive) return;
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

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    internal async Task ShutdownAsync()
    {
        ViewModel.CancelForWindowClose();
        InboxPage.ViewModel.Close();
        var tasks = new[]
        {
            ViewModel.RunCommand.ExecutionTask,
            InboxPage.ViewModel.ApproveCommand.ExecutionTask,
            InboxPage.ViewModel.SendAttachmentsCommand.ExecutionTask,
            InboxPage.ViewModel.ConnectMattermostCommand.ExecutionTask,
            InboxPage.ViewModel.ConnectSlackCommand.ExecutionTask,
            InboxPage.ViewModel.DisconnectCommand.ExecutionTask,
            InboxPage.ViewModel.WaitForConnectionShutdownAsync()
        }.OfType<Task>();
        await Task.WhenAll(tasks);
    }
    public static Style OverviewIconStyle(OverviewIconState state) => (Style)Application.Current.Resources[state switch
    {
        OverviewIconState.Ready => "OverviewReadyIconStyle",
        OverviewIconState.Attention => "OverviewAttentionIconStyle",
        OverviewIconState.Error => "OverviewErrorIconStyle",
        _ => "OverviewNeutralIconStyle"
    }];

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
        if (_hasLoaded) return;
        _hasLoaded = true;
        await ViewModel.LoadAsync();
        var startupProvider = ViewModel.AutoConnectProvider;
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        await ViewModel.RefreshStartupAsync();
        await ViewModel.InitializeOnboardingAsync();
        if (!App.IsNotificationOnlyLaunch && ViewModel.IsSettingsLoaded && !ViewModel.IsOnboardingActive && !ViewModel.IsLoading &&
            !ViewModel.HasUnsavedSettings && !ViewModel.IsSensitiveConfirmationPending &&
            ViewModel.AutoConnectProvider == startupProvider)
        {
            if (startupProvider is "mattermost" or "slack")
                MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");
            if (startupProvider == "mattermost") await InboxPage.ViewModel.ConnectMattermostCommand.ExecuteAsync(null);
            else if (startupProvider == "slack") await InboxPage.ViewModel.ConnectSlackCommand.ExecuteAsync(null);
        }
    }

    private async void ReloadSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryResolveUnsavedSettingsAsync()) return;
        await ViewModel.ReloadSettingsAsync();
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
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
        Bindings.Update();
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
                });
            }
        }
        if (IsHistorySelected) _ = HistoryPage.ViewModel.RefreshAsync();
        if (pageChanged && IsOverviewSelected && _hasLoaded) _ = ViewModel.RefreshStartupAsync();
        if (pageChanged && IsAboutSelected) AboutPage.ResetScroll();
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
