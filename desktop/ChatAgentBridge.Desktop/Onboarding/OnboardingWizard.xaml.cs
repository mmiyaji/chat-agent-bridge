using ChatAgentBridge.Core;
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
        PasswordRevealControl.Attach(SlackBotTokenRevealButton, SlackBotTokenBox);
        PasswordRevealControl.Attach(SlackAppTokenRevealButton, SlackAppTokenBox);
        PasswordRevealControl.Attach(MattermostTokenRevealButton, MattermostTokenBox);
        IntroHost.Content = new OnboardingIntro(ViewModel);
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.OnboardingChatProvider) or nameof(ViewModel.OnboardingStep) or nameof(ViewModel.IsOnboardingActive))
                PasswordRevealControl.Hide(this);
            if (args.PropertyName is nameof(ViewModel.OnboardingChatProvider) or nameof(ViewModel.OnboardingStep))
                ClearFieldHighlights();
        };
        ViewModel.OnboardingFieldsFocusRequested += FocusMissingFields;
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
        PasswordRevealControl.Hide(this);
        WizardScrollViewer.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() => WizardScrollViewer.ChangeView(null, 0, null, true));
    }

    private readonly List<FrameworkElement> _markedFields = [];

    private void ClearFieldHighlights()
    {
        foreach (var field in _markedFields) FieldHighlight.Clear(field);
        _markedFields.Clear();
    }

    private (FrameworkElement Target, FrameworkElement Input) Resolve(WizardField field) => field switch
    {
        WizardField.WorkspaceRoot => (WorkspaceRootBox, WorkspaceRootBox),
        WizardField.AgentProvider => (AgentProviderCombo, AgentProviderCombo),
        WizardField.AgentExecutable => (AgentExecutableBox, AgentExecutableBox),
        WizardField.CliCheck => (CheckCliButton, CheckCliButton),
        // A password box shares a grid with its reveal button; outline the whole row.
        WizardField.SlackBotToken => ((FrameworkElement)SlackBotTokenBox.Parent, SlackBotTokenBox),
        WizardField.SlackAppToken => ((FrameworkElement)SlackAppTokenBox.Parent, SlackAppTokenBox),
        WizardField.SlackAllowedUsers => (SlackAllowedUsersBox, SlackAllowedUsersBox),
        WizardField.MattermostServerUrl => (MattermostServerUrlBox, MattermostServerUrlBox),
        WizardField.MattermostChannel => (MattermostChannelBox, MattermostChannelBox),
        WizardField.MattermostToken => ((FrameworkElement)MattermostTokenBox.Parent, MattermostTokenBox),
        WizardField.MattermostAllowedUsers => (MattermostAllowedUsersBox, MattermostAllowedUsersBox),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };

    /// <summary>Outlines every missing required input and moves focus to the first one.</summary>
    private void FocusMissingFields(IReadOnlyList<WizardField> fields)
    {
        if (fields.Contains(WizardField.Terms) || fields.Contains(WizardField.Privacy)) { FocusMissingConsent(); return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearFieldHighlights();
            FrameworkElement? first = null;
            foreach (var field in fields)
            {
                var (target, input) = Resolve(field);
                FieldHighlight.Mark(target, input);
                _markedFields.Add(target);
                first ??= input;
            }
            if (first is null) return;
            // Wrapping, the status bar opening and focus each change the layout, so reveal the field once the
            // layout has settled and again afterwards; a single early request lands on stale positions.
            RevealField(first);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RevealField(first));
        });
    }

    private void RevealField(FrameworkElement field)
    {
        if (!ViewModel.IsOnboardingActive || field.XamlRoot is null) return;
        WizardScrollViewer.UpdateLayout();
        if (field is Control control) control.Focus(FocusState.Programmatic);
        WizardScrollViewer.UpdateLayout();
        if (WizardScrollViewer.Content is not UIElement content) return;
        var top = field.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        var bottom = top + field.ActualHeight;
        var viewTop = WizardScrollViewer.VerticalOffset;
        var viewBottom = viewTop + WizardScrollViewer.ViewportHeight;
        // A field that is already fully visible stays put, so the status message above it is not scrolled away.
        if (top - 16 >= viewTop && bottom + 16 <= viewBottom) return;
        WizardScrollViewer.ChangeView(null, Math.Clamp(top - WizardScrollViewer.ViewportHeight / 3, 0, WizardScrollViewer.ScrollableHeight), null, true);
    }

    private void FocusMissingConsent()
    {
        if (IntroHost.Content is not OnboardingIntro intro) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            intro.FocusMissingConsent();
            // Focus can queue its own minimal BringIntoView request. Scroll
            // after it so both choices and the consent hint are fully visible.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!ViewModel.IsWizardIntro || !ViewModel.IsOnboardingActive) return;
                WizardScrollViewer.UpdateLayout();
                WizardScrollViewer.ChangeView(null, WizardScrollViewer.ScrollableHeight, null, true);
            });
        });
    }

    public void SyncSecrets()
    {
        PasswordRevealControl.Hide(this);
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
