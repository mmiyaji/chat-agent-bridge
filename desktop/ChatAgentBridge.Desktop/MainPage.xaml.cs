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
    private readonly Onboarding.OnboardingWizard _wizard;

    public MainPageViewModel ViewModel { get; } = new();
    public bool IsOverviewSelected { get; private set; } = true;
    public bool IsWorkbenchSelected { get; private set; }
    public bool IsAgentSelected { get; private set; }
    public bool IsConnectionsSelected { get; private set; }
    public bool IsAppearanceSelected { get; private set; }
    public bool IsChatSelected { get; private set; }
    public bool IsHistorySelected { get; private set; }
    public bool IsAboutSelected { get; private set; }
    public bool IsFormSelected => !IsChatSelected && !IsHistorySelected && !IsAboutSelected;
    public static bool CanUseForm(bool isAppearanceSelected, bool isSettingsLoaded) => isAppearanceSelected || isSettingsLoaded;

    public MainPage()
    {
        InitializeComponent();
        InitializeLanguageRestart();
        UpdateNavigationContentOffset();
        _wizard = new Onboarding.OnboardingWizard(ViewModel, InboxPage.ViewModel);
        WizardHost.Content = _wizard;
        ViewModel.PropertyChanged += (_, args) =>
        {
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
        ViewModel.OnboardingNavigateRequested += _ => _wizard.ResetScroll();
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }

    private void RefreshLanguage(System.Globalization.CultureInfo culture)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
        MrtText.Refresh(this, culture);
    }

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
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
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        await ViewModel.RefreshStartupAsync();
        await ViewModel.InitializeOnboardingAsync();
        if (ViewModel.IsSettingsLoaded && !ViewModel.IsOnboardingActive)
        {
            if (ViewModel.AutoConnectProvider is "mattermost" or "slack")
                MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");
            if (ViewModel.AutoConnectProvider == "mattermost") await InboxPage.ViewModel.ConnectMattermostCommand.ExecuteAsync(null);
            else if (ViewModel.AutoConnectProvider == "slack") await InboxPage.ViewModel.ConnectSlackCommand.ExecuteAsync(null);
        }
    }

    private async void ReloadSettings_Click(object sender, RoutedEventArgs e)
    {
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

    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        var pageChanged = !string.Equals(_selectedNavigationTag, tag, StringComparison.Ordinal);
        _selectedNavigationTag = tag;
        IsOverviewSelected = tag == "overview";
        IsWorkbenchSelected = tag == "workbench";
        IsAgentSelected = tag == "agent";
        IsConnectionsSelected = tag == "connections";
        IsAppearanceSelected = tag == "appearance";
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
        if (pageChanged && IsAboutSelected) AboutPage.ResetScroll();
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
        if (sender is ToggleSwitch toggle && ViewModel.CanChangeStartup)
            await ViewModel.ChangeStartupAsync(toggle.IsOn);
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
