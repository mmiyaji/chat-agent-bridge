using ChatAgentBridge_Desktop.ViewModels;
using ChatAgentBridge_Desktop.Inbox;
using ChatAgentBridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage : Page
{
    private bool _hasLoaded;

    public MainPageViewModel ViewModel { get; } = new();
    public bool IsOverviewSelected { get; private set; } = true;
    public bool IsWorkbenchSelected { get; private set; }
    public bool IsAgentSelected { get; private set; }
    public bool IsConnectionsSelected { get; private set; }
    public bool IsChatSelected { get; private set; }
    public bool IsHistorySelected { get; private set; }
    public bool IsFormSelected => !IsChatSelected && !IsHistorySelected;

    public MainPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.IsSettingsLoaded) && ViewModel.IsSettingsLoaded)
            {
                MattermostTokenBox.Password = ViewModel.MattermostToken;
                SlackBotTokenBox.Password = ViewModel.SlackBotToken;
                SlackAppTokenBox.Password = ViewModel.SlackAppToken;
            }
        };
        InboxPage.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatInboxViewModel.IsConnectionConfirmed) or nameof(ChatInboxViewModel.IsConnected))
                ViewModel.SetChatConnectionState(InboxPage.ViewModel.IsConnectionConfirmed, InboxPage.ViewModel.IsConnected);
        };
        Loaded += MainPage_Loaded;
        ViewModel.ConfirmExecutionAsync = ConfirmExecutionAsync;
        var serverDialogs = new AppServerUi.AppServerDialogs(() => XamlRoot);
        ViewModel.ServerApprovalHandler = serverDialogs.ApproveAsync;
        ViewModel.ServerInputHandler = serverDialogs.InputAsync;
        HistoryPage.AgentSettingsRequested += () => OpenAgentSettings_Click(this, new RoutedEventArgs());
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ToHiddenVisibility(bool visible) => visible ? Visibility.Collapsed : Visibility.Visible;
    public static bool CanReloadSettings(bool isLoading, bool isRunning) => !isLoading && !isRunning;
    public static InfoBarSeverity ToInfoBarSeverity(string severity) => severity switch
    {
        "Success" => InfoBarSeverity.Success,
        "Warning" => InfoBarSeverity.Warning,
        "Error" => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoaded) return;
        _hasLoaded = true;
        await ViewModel.LoadAsync();
        MattermostTokenBox.Password = ViewModel.MattermostToken;
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        await ViewModel.RefreshStartupAsync();
        if (ViewModel.IsSettingsLoaded)
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

    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        IsOverviewSelected = tag == "overview";
        IsWorkbenchSelected = tag == "workbench";
        IsAgentSelected = tag == "agent";
        IsConnectionsSelected = tag == "connections";
        IsChatSelected = tag == "chat";
        IsHistorySelected = tag == "history";
        if (!IsAgentSelected) ViewModel.CancelModelCatalogLookup();
        ViewModel.IsStatusOpen = false;
        Bindings.Update();
        if (IsHistorySelected) _ = HistoryPage.ViewModel.RefreshAsync();
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
            XamlRoot = XamlRoot,
            Title = "エージェントを実行しますか？",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "次の内容をローカルエージェントに渡します。", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "ワークスペース", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = settings.WorkspaceRoot, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "実行ファイルと引数", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = $"{settings.AgentExecutable} {(settings.AgentMode == "app-server" ? "app-server" : settings.AgentArguments)}".Trim(), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "モデルと推論レベル", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = $"{settings.AgentModel ?? "CLIの既定値"} / {settings.AgentReasoningEffort ?? "CLIの既定値"}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "プロンプト", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = "実行",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "RunConfirmation");
        AutomationProperties.SetName(dialog, "エージェント実行の確認");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
