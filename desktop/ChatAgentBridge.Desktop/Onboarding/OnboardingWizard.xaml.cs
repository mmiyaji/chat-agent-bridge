using ChatAgentBridge_Desktop.Inbox;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.Windows.Storage.Pickers;
using System.Globalization;

namespace ChatAgentBridge_Desktop.Onboarding;

public sealed partial class OnboardingWizard : UserControl
{
    public MainPageViewModel ViewModel { get; }
    public ChatInboxViewModel Inbox { get; }
    private readonly Action _reloadProcessEnvironment;

    public OnboardingWizard(MainPageViewModel viewModel, ChatInboxViewModel inbox, Action reloadProcessEnvironment)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _reloadProcessEnvironment = reloadProcessEnvironment ?? throw new ArgumentNullException(nameof(reloadProcessEnvironment));
        InitializeComponent();
        IntroHost.Content = new OnboardingIntro(ViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        SyncSecrets();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        LanguageSettings.LanguageChanged -= OnLanguageChanged;
        LanguageSettings.LanguageChanged += OnLanguageChanged;
        RefreshLanguage(CultureInfo.CurrentUICulture);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        LanguageSettings.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(CultureInfo culture) => RefreshLanguage(culture);

    /// <summary>Refreshes bound wizard text and existing x:Uid labels after an in-process language change.</summary>
    public void RefreshLanguage(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
        MrtText.Refresh(this, culture);
        if (IntroHost.Content is OnboardingIntro intro)
            intro.RefreshLanguage(culture);
    }

    public void ResetScroll()
    {
        WizardScrollViewer.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() => WizardScrollViewer.ChangeView(null, 0, null, true));
    }

    public void SyncSecrets()
    {
        SlackBotTokenBox.Password = ViewModel.SlackBotToken;
        SlackAppTokenBox.Password = ViewModel.SlackAppToken;
        MattermostTokenBox.Password = ViewModel.MattermostToken;
    }

    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static bool Not(bool value) => !value;
    public static bool SupportsOperationApproval(bool isCodex, bool useAppServer) => isCodex && useAppServer;
    public static bool EnvironmentReloadAllowed(bool isLoading, bool isRunning, bool isCheckingCli,
        bool isCatalogLoading, bool isOnboardingBusy, bool isInboxExecuting, bool isSaving) =>
        MainPage.EnvironmentReloadAllowed(isLoading, isRunning, isCheckingCli, isCatalogLoading, isOnboardingBusy, isInboxExecuting, isSaving);

    private void ReloadProcessEnvironment_Click(object sender, RoutedEventArgs e) => _reloadProcessEnvironment();

    private async void ReviewCliTrust_Click(object sender, RoutedEventArgs e) =>
        await CliTrustDialog.ShowAsync(ViewModel, XamlRoot, recheck: !ViewModel.IsWizardAgentStep);

    private async void BrowseAgentExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.XamlRoot is null) return;
        button.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                CommitButtonText = DesktopText.Get("ChooseExecutable")
            };
            MainPage.ConfigureExecutablePicker(picker, ViewModel.AgentProvider);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) ViewModel.AgentExecutable = file.Path;
        }
        catch
        {
            SetWizardStatus(DesktopText.Get("ExecutablePickerFailedTitle"), DesktopText.Get("ExecutablePickerFailedMessage"), "Error");
        }
        finally { button.IsEnabled = true; }
    }

    public static InfoBarSeverity ToInfoBarSeverity(string? severity) => severity switch
    {
        "Success" => InfoBarSeverity.Success,
        "Warning" => InfoBarSeverity.Warning,
        "Error" => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private async void PickWorkspaceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.XamlRoot is null) return;

        button.IsEnabled = false;
        try
        {
            var picker = new FolderPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                CommitButtonText = DesktopText.Get("ChooseThisFolder")
            };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                ViewModel.WorkspaceRoot = folder.Path;
        }
        catch (Exception ex)
        {
            SetWizardStatus(DesktopText.Get("FolderPickFailed"), ex.Message, "Error");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SlackBotTokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.SlackBotToken = SlackBotTokenBox.Password;

    private void SlackAppTokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.SlackAppToken = SlackAppTokenBox.Password;

    private void MattermostTokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.SetMattermostToken(MattermostTokenBox.Password);

    private async void ConnectSlack_Click(object sender, RoutedEventArgs e) =>
        await ConnectSelectedProviderAsync("slack");

    private async void ConnectMattermost_Click(object sender, RoutedEventArgs e) =>
        await ConnectSelectedProviderAsync("mattermost");

    private async Task ConnectSelectedProviderAsync(string provider)
    {
        if (ViewModel.IsOnboardingBusy) return;
        if (Inbox.IsExecuting)
        {
            SetWizardStatus(DesktopText.Get("CannotConnect"), DesktopText.Get("WaitForExecutionToFinish"), "Warning");
            return;
        }

        ViewModel.IsOnboardingBusy = true;
        try
        {
            if (!await ViewModel.SaveWizardConnectionSettingsAsync()) return;
            if (Inbox.IsConnected)
                await Inbox.DisconnectCommand.ExecuteAsync(null);
            if (provider == "slack")
                await Inbox.ConnectSlackCommand.ExecuteAsync(null);
            else
                await Inbox.ConnectMattermostCommand.ExecuteAsync(null);
            if (Inbox.IsConnected) ViewModel.RememberWizardConnectionSettings(provider);
        }
        catch
        {
            SetWizardStatus(DesktopText.Get("ConnectionFailed"), DesktopText.Get("CheckConnectionAndNetwork"), "Error");
        }
        finally { ViewModel.IsOnboardingBusy = false; }
    }

    private void SetWizardStatus(string title, string message, string severity)
    {
        ViewModel.StatusTitle = title;
        ViewModel.StatusMessage = message;
        ViewModel.StatusSeverity = severity;
        ViewModel.IsStatusOpen = true;
    }
}
