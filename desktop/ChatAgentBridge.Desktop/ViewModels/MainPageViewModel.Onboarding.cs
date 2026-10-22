using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatAgentBridge_Desktop.Localization;
namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly OnboardingProgressStore _onboardingProgressStore = new();
    private readonly OnboardingConsentStore _onboardingConsentStore = new();
    private bool _hasCurrentConsent;
    [ObservableProperty] public partial bool HasAcceptedTerms { get; set; }
    [ObservableProperty] public partial bool HasAcceptedPrivacy { get; set; }
    [ObservableProperty] public partial string ConsentStatus { get; set; } = ViewModelText.Get("ConsentInstructions");
    private BridgeSettings? _wizardConnectedSettings;
    private string? _wizardConnectedProvider;
    public void ClearWizardConnection() { _wizardConnectedSettings = null; _wizardConnectedProvider = null; }
    public void RememberWizardConnectionSettings(string provider)
    {
        _wizardConnectedSettings = _settings;
        _wizardConnectedProvider = provider;
    }
    partial void OnIsOnboardingBusyChanged(bool value) => CheckCliCommand.NotifyCanExecuteChanged();
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(NextOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(DeferOnboardingCommand))]
    public partial bool IsOnboardingBusy { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OnboardingTitle)), NotifyPropertyChangedFor(nameof(OnboardingDescription)), NotifyPropertyChangedFor(nameof(OnboardingNextLabel)), NotifyPropertyChangedFor(nameof(OnboardingProgress)), NotifyPropertyChangedFor(nameof(IsWizardIntro)), NotifyPropertyChangedFor(nameof(IsWizardAgentStep)), NotifyPropertyChangedFor(nameof(IsWizardCliStep)), NotifyPropertyChangedFor(nameof(IsWizardChatStep)), NotifyPropertyChangedFor(nameof(IsWizardSafetyStep)), NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand))]
    public partial int OnboardingStep { get; set; }
    [ObservableProperty] public partial bool IsOnboardingActive { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWizardSlack)), NotifyPropertyChangedFor(nameof(IsWizardMattermost)), NotifyPropertyChangedFor(nameof(WizardSummary)), NotifyPropertyChangedFor(nameof(WizardAutoConnect))]
    public partial string OnboardingChatProvider { get; set; } = "slack";
    public bool WizardAutoConnect
    {
        get => AutoConnectProvider == OnboardingChatProvider;
        set { AutoConnectProvider = value ? OnboardingChatProvider : "none"; OnPropertyChanged(); }
    }
    public bool IsWizardIntro => OnboardingStep == 0;
    public bool IsWizardAgentStep => OnboardingStep == 1;
    public bool IsWizardCliStep => OnboardingStep == 2;
    public bool IsWizardChatStep => OnboardingStep == 3;
    public bool IsWizardSafetyStep => OnboardingStep == 4;
    public bool IsWizardSlack => OnboardingChatProvider == "slack";
    public bool IsWizardMattermost => OnboardingChatProvider == "mattermost";
    public string OnboardingTitle => $"{OnboardingProgress} " + (OnboardingStep switch
    { 0 => ViewModelText.Get("WizardStepIntro"), 1 => ViewModelText.Get("WizardStepAgent"), 2 => ViewModelText.Get("WizardStepCli"), 3 => ViewModelText.Get("WizardStepChat"), _ => ViewModelText.Get("WizardStepSafety") });
    public string OnboardingDescription => OnboardingStep switch
    {
        0 => ViewModelText.Get("WizardIntroDescription"),
        1 => ViewModelText.Get("WizardAgentDescription"),
        2 => ViewModelText.Get("WizardCliDescription"),
        3 => ViewModelText.Get("WizardChatDescription"),
        _ => ViewModelText.Get("WizardSafetyDescription")
    };
    public string OnboardingNextLabel => OnboardingStep == 0 ? ViewModelText.Get("ConsentAndContinue") : OnboardingStep == 4 ? ViewModelText.Get("SaveAndFinish") : ViewModelText.Get("SaveAndContinue");
    public string OnboardingProgress => $"[{Math.Clamp(OnboardingStep, 0, 4) + 1}/5]";
    public string WizardSummary => ViewModelText.Get("WizardSummary", AgentProviderDisplayName, string.IsNullOrWhiteSpace(AgentModel) ? ViewModelText.Get("DefaultCliValue") : AgentModel, string.IsNullOrWhiteSpace(AgentReasoningEffort) ? ViewModelText.Get("DefaultCliValue") : AgentReasoningEffort, WorkspaceRoot, IsWizardSlack ? "Slack" : "Mattermost", ViewModelText.Get(WizardAutoConnect ? "Enabled" : "Disabled"), ChatApprovalLabel(ChatApprovalMode), ResultLabel(ChatResultMode), PermissionLabel(AgentPermissionMode));
    public event Action<int>? OnboardingNavigateRequested;
    /// <summary>Raised when the user tries to continue with required inputs missing, so the page can highlight and focus them.</summary>
    public event Action<IReadOnlyList<WizardField>>? OnboardingFieldsFocusRequested;
    private void RequestFieldFocus(IReadOnlyList<WizardField> fields) { if (fields.Count > 0) OnboardingFieldsFocusRequested?.Invoke(fields); }

    public async Task InitializeOnboardingAsync()
    {
        if (!IsSettingsLoaded) return;
        try
        {
            var progress = await _onboardingProgressStore.LoadAsync();
            if (!OnboardingProgressStore.ShouldStart(File.Exists(Path.Combine(_settingsStore.DirectoryPath, "settings.json")), progress)) return;
            _hasCurrentConsent = OnboardingConsentStore.IsCurrent(await _onboardingConsentStore.LoadAsync());
            HasAcceptedTerms = HasAcceptedPrivacy = _hasCurrentConsent;
            OnboardingStep = !_hasCurrentConsent ? 0 : progress?.Step > 1 ? 2 : progress?.Step ?? 0;
            OnboardingChatProvider = PreferredWizardProvider();
            await SaveOnboardingProgressAsync("active", OnboardingStep);
            IsOnboardingActive = true;
            IsStatusOpen = false;
            OnboardingNavigateRequested?.Invoke(OnboardingStep);
        }
        catch { SetStatus(ViewModelText.Get("WizardProgressLoadFailed"), ViewModelText.Get("WizardProgressLoadFailedDetails"), "Error"); }
    }
    private string PreferredWizardProvider() => AutoConnectProvider is "slack" or "mattermost" ? AutoConnectProvider : !string.IsNullOrWhiteSpace(MattermostToken) && string.IsNullOrWhiteSpace(SlackBotToken) ? "mattermost" : "slack";
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task StartOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        OnboardingChatProvider = PreferredWizardProvider();
        HasAcceptedTerms = HasAcceptedPrivacy = false;
        ConsentStatus = ViewModelText.Get("ConsentInstructionsWizard");
        await SaveOnboardingProgressAsync("active", 0);
        OnboardingStep = 0;
        IsOnboardingActive = true;
        IsStatusOpen = false;
        OnboardingNavigateRequested?.Invoke(0);
    });
    [RelayCommand(CanExecute = nameof(CanGoBackInOnboarding))]
    private Task PreviousOnboardingAsync() => RunOnboardingActionAsync(() => AdvanceOnboardingAsync(OnboardingStep - 1));
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task NextOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        IsStatusOpen = false;
        if (OnboardingStep == 0)
        {
            if (!HasAcceptedTerms || !HasAcceptedPrivacy)
            {
                SetStatus(ViewModelText.Get("ConsentRequired"), ViewModelText.Get("ConsentRequiredDetails"), "Warning");
                RequestFieldFocus(OnboardingFieldChecks.Consent(HasAcceptedTerms, HasAcceptedPrivacy));
                return;
            }
            await _onboardingConsentStore.SaveAsync(new OnboardingConsent(OnboardingConsentStore.CurrentPolicyVersion, DateTimeOffset.UtcNow));
            _hasCurrentConsent = true;
            ConsentStatus = ViewModelText.Get("ConsentSaved", OnboardingConsentStore.CurrentPolicyVersion);
            await AdvanceOnboardingAsync(1);
            return;
        }
        if (!_hasCurrentConsent || !HasAcceptedTerms || !HasAcceptedPrivacy) { SetStatus(ViewModelText.Get("ReviewConsent"), ViewModelText.Get("ReviewConsentDetails"), "Warning"); return; }
        if (OnboardingStep == 1)
        {
            var providerSelected = AgentProvider is "codex" or "claude" or "gemini" or "github-copilot" or "custom";
            var workspaceUsable = Path.IsPathFullyQualified(WorkspaceRoot) && Directory.Exists(WorkspaceRoot);
            var missingAgent = OnboardingFieldChecks.AgentStep(providerSelected, AgentExecutable, workspaceUsable);
            if (missingAgent.Contains(WizardField.AgentProvider) || missingAgent.Contains(WizardField.AgentExecutable)) { SetStatus(ViewModelText.Get("SelectAgent"), ViewModelText.Get("SelectAgentDetails"), "Warning"); RequestFieldFocus(missingAgent); return; }
            if (!workspaceUsable) { SetStatus(ViewModelText.Get("CheckWorkspace"), ViewModelText.Get("CheckWorkspaceDetails"), "Warning"); RequestFieldFocus(missingAgent); return; }
            if (!ExecutionPolicy.IsWorkspaceTrusted(CreateSettingsSnapshot(), WorkspaceRoot))
            {
                var trust = CliProbeService.DescribeUntrustedWorkspace(WorkspaceRoot);
                CliCheckFailed = true;
                CliCheckSummary = trust.Message;
                SetCliTrustRecovery(trust, CurrentCliSignature());
                RequestFieldFocus([WizardField.WorkspaceRoot]);
                return;
            }
        }
        if (OnboardingStep == 2 && !HasCurrentCliCheck()) { SetStatus(ViewModelText.Get("CheckCliAndModel"), CliCheckSucceeded ? ViewModelText.Get("SelectedModelUnavailable") : ViewModelText.Get("RunCliCheckDetails"), "Warning"); RequestFieldFocus(OnboardingFieldChecks.CliStep(false)); return; }
        if (OnboardingStep == 3 && !HasChosenChatConfiguration()) { SetStatus(ViewModelText.Get("CheckConnectionDetails"), ViewModelText.Get(IsWizardSlack ? "SlackConnectionFields" : "MattermostConnectionFields"), "Warning"); RequestFieldFocus(MissingChatFields()); return; }
        await SaveSettingsAsync();
        if (!_lastSaveSucceeded) return;
        if (OnboardingStep < 4) { await AdvanceOnboardingAsync(OnboardingStep + 1); return; }
        await SaveOnboardingProgressAsync("completed", 4);
        IsOnboardingActive = false;
        var skipped = SkippedWizardItems();
        if (skipped.Length > 0)
            SetStatus(ViewModelText.Get("WizardSkippedTitle"), ViewModelText.Get("WizardSkippedDetails", skipped), "Informational");
        else if (!_chatConnectionConfirmed || _wizardConnectedProvider != OnboardingChatProvider || _wizardConnectedSettings != CreateSettingsSnapshot())
            SetStatus(ViewModelText.Get("WizardConnectionDeferredTitle"), ViewModelText.Get("WizardConnectionDeferredDetails"), "Informational");
        else
            IsStatusOpen = false;
    });
    /// <summary>The steps that can be left for later; the wizard may finish without them.</summary>
    public bool CanSkipOnboardingStep => OnboardingStep is 2 or 3;
    public string OnboardingSkipLabel => ViewModelText.Get("WizardSkipStep");
    public bool HasWizardSkippedNotice => SkippedWizardItems().Length > 0;
    public string WizardSkippedNotice => SkippedWizardItems() is { Length: > 0 } items ? ViewModelText.Get("WizardSkippedNotice", items) : "";
    private string SkippedWizardItems()
    {
        var items = new List<string>();
        if (!HasCurrentCliCheck()) items.Add(ViewModelText.Get("WizardSkippedCli"));
        if (!HasChosenChatConfiguration()) items.Add(ViewModelText.Get("WizardSkippedChat"));
        return string.Join(ViewModelText.Get("WizardSkippedSeparator"), items);
    }
    partial void OnOnboardingStepChanged(int value) => RefreshWizardSkipState();
    private void RefreshWizardSkipState()
    {
        OnPropertyChanged(nameof(CanSkipOnboardingStep));
        OnPropertyChanged(nameof(HasWizardSkippedNotice));
        OnPropertyChanged(nameof(WizardSkippedNotice));
        OnPropertyChanged(nameof(OnboardingSkipLabel));
    }
    [RelayCommand]
    private Task SkipOnboardingStepAsync() => RunOnboardingActionAsync(async () =>
    {
        if (!CanSkipOnboardingStep) return;
        IsStatusOpen = false;
        // The entered values are kept; only the missing-input check is waived for this step.
        await SaveSettingsAsync();
        if (!_lastSaveSucceeded) return;
        await AdvanceOnboardingAsync(OnboardingStep + 1);
    });
    public async Task<bool> SaveWizardConnectionSettingsAsync()
    {
        if (!_hasCurrentConsent || !HasAcceptedTerms || !HasAcceptedPrivacy || !HasCurrentCliCheck() || !HasChosenChatConfiguration()) { SetStatus(ViewModelText.Get("ReviewSettings"), ViewModelText.Get("ReviewSettingsDetails"), "Warning"); return false; }
        await SaveSettingsAsync();
        return _lastSaveSucceeded;
    }
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task DeferOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        await SaveOnboardingProgressAsync("deferred", OnboardingStep);
        IsOnboardingActive = false;
        IsStatusOpen = false;
    });
    private bool CanOnboardingAction() => IsSettingsLoaded && !IsLoading && !IsRunning && !IsOnboardingBusy && !IsCheckingCli;
    private bool CanGoBackInOnboarding() => CanOnboardingAction() && OnboardingStep > 0;
    private bool HasChosenChatConfiguration() => (IsWizardSlack || IsWizardMattermost) && MissingChatFields().Count == 0;
    private IReadOnlyList<WizardField> MissingChatFields() => IsWizardSlack
        ? OnboardingFieldChecks.SlackChat(SlackBotToken, SlackAppToken, HasAllowedUsers(SlackAllowedUserIds))
        : IsWizardMattermost
            ? OnboardingFieldChecks.MattermostChat(IsWizardMattermostUrlValid(), MattermostChannelId, MattermostToken, HasAllowedUsers(MattermostAllowedUserIds))
            : [];
    private bool IsWizardMattermostUrlValid() => Uri.TryCreate(MattermostServerUrl.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private async Task AdvanceOnboardingAsync(int step)
    {
        await SaveOnboardingProgressAsync("active", step);
        OnboardingStep = step;
        IsStatusOpen = false;
        OnboardingNavigateRequested?.Invoke(step);
    }
    private Task SaveOnboardingProgressAsync(string status, int step) => _onboardingProgressStore.SaveAsync(new OnboardingProgress(step, status));
    private async Task RunOnboardingActionAsync(Func<Task> action)
    {
        if (!CanOnboardingAction()) return;
        IsOnboardingBusy = true;
        try { await action(); }
        catch { SetStatus(ViewModelText.Get("WizardSaveFailed"), ViewModelText.Get("WizardSaveFailedDetails"), "Error"); }
        finally { IsOnboardingBusy = false; }
    }
}
