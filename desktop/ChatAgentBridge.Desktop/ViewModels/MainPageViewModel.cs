using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge_Desktop.ModelSelection;
using ChatAgentBridge_Desktop.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AgentRunner _agentRunner = new();
    private readonly CodexAppServerRunner _appServerRunner = new();
    private readonly ExecutionHistoryStore _executionHistoryStore = new();
    private readonly AuditStore _auditStore = new();
    private CancellationTokenSource? _runCancellation;
    private BridgeSettings? _settings;
    private bool _closing;
    private bool _initializingSettings;
    private bool _applyingCatalogSelection;
    private string? _effortModel;
    private bool _hasCatalogForCurrentModel;
    private bool _unsupportedSavedEffort;
    private bool _chatConnectionConfirmed;
    private bool _chatConnectionPreparing;
    private bool _chatConnectionFailed;
    private bool _savedWorkspaceExists;
    private bool _lastSaveSucceeded;
    private bool _adjustingAgentPermissionMode;
    private bool _agentPermissionModeAdjusted;
    private Startup.StartupState? _startupState;
    private readonly Startup.StartupOperationGuard _startupOperations = new();

    public ModelCatalogViewModel ModelCatalog { get; }
    public ObservableCollection<ReasoningEffortChoice> ReasoningEffortCandidates { get; } = [];

    public MainPageViewModel()
    {
        ReasoningEffortCandidates.Add(ReasoningEffortChoice.Default);
        ModelCatalog = new ModelCatalogViewModel(CreateSettingsSnapshot, ApplyCatalogModelSelection);
        ModelCatalog.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(ModelCatalog.Status) || e.PropertyName == nameof(ModelCatalog.IsLoading)) && !ModelCatalog.IsLoading)
                PopulateGlobalEfforts();
        };
        PropertyChanged += (_, e) =>
        {
            WizardSettingChanged(e.PropertyName);
            if (e.PropertyName is nameof(CliCheckSucceeded) or nameof(CliCheckFailed) or nameof(IsCheckingCli) or nameof(CliVersion))
                RefreshOverviewStatus();
            if (e.PropertyName is nameof(IsSettingsLoaded) or nameof(IsLoading) or nameof(IsRunning))
            {
                StartOnboardingCommand.NotifyCanExecuteChanged();
                PreviousOnboardingCommand.NotifyCanExecuteChanged();
                NextOnboardingCommand.NotifyCanExecuteChanged();
                DeferOnboardingCommand.NotifyCanExecuteChanged();
                CheckCliCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName is nameof(IsLoading) or nameof(IsSettingsLoaded) or nameof(WorkspaceRoot) or
                nameof(AgentExecutable) or nameof(AgentArguments) or nameof(AgentProvider) or nameof(AgentModel) or
                nameof(AgentReasoningEffort) or nameof(UseAppServer) or nameof(AutoConnectProvider) or nameof(RequireApproval) or
                nameof(MattermostServerUrl) or nameof(MattermostChannelId) or nameof(MattermostToken) or nameof(MattermostAllowedUserIds) or
                nameof(SlackBotToken) or nameof(SlackAppToken) or nameof(SlackAllowedUserIds) or nameof(ShareApprovalsInChat) or
                nameof(AgentPermissionMode) or nameof(AllowAgentNetwork) or nameof(TrustedWorkspaceRoots) or nameof(ChatApprovalMode) or
                nameof(ChatResultMode) or nameof(SlackAllowedChannelIds) or nameof(SlackAllowedThreadIds) or nameof(MattermostAllowedThreadIds))
                RefreshSetupStepper();
            if (e.PropertyName is nameof(AgentPermissionMode) or nameof(AllowAgentNetwork) or nameof(TrustedWorkspaceRoots) or
                nameof(ChatApprovalMode) or nameof(ChatResultMode) or nameof(SlackAllowedChannelIds) or nameof(SlackAllowedThreadIds) or nameof(MattermostAllowedThreadIds) or nameof(AgentProvider) or nameof(UseAppServer))
            {
                OnPropertyChanged(nameof(ExecutionPolicySummary));
                OnPropertyChanged(nameof(CanConfigureAgentNetwork));
                OnPropertyChanged(nameof(CanConfigureAgentPermission));
                OnPropertyChanged(nameof(AgentPermissionStatus));
            }
        };
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadSettingsCommand))]
    [NotifyPropertyChangedFor(nameof(CanFetchModelCandidates))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyPropertyChangedFor(nameof(CanFetchModelCandidates))]
    public partial bool IsSettingsLoaded { get; set; }

    [ObservableProperty] public partial string StatusTitle { get; set; } = ViewModelText.Get("LoadingSettings");
    [ObservableProperty] public partial string StatusMessage { get; set; } = ViewModelText.Get("CheckingSavedSettings");
    [ObservableProperty] public partial string StatusSeverity { get; set; } = "Informational";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; } = true;
    [ObservableProperty] public partial string WorkspaceRoot { get; set; } = string.Empty;
    partial void OnWorkspaceRootChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty] public partial string AgentExecutable { get; set; } = "codex";
    partial void OnAgentExecutableChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty] public partial string AgentArguments { get; set; } = "exec --json -";
    partial void OnAgentArgumentsChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCodexProvider)), NotifyPropertyChangedFor(nameof(IsCustomProvider)), NotifyPropertyChangedFor(nameof(IsBasicProvider)), NotifyPropertyChangedFor(nameof(IsArgumentsEditable)), NotifyPropertyChangedFor(nameof(BasicProviderHelp)), NotifyPropertyChangedFor(nameof(AgentProviderDisplayName)), NotifyPropertyChangedFor(nameof(SupportsModel)), NotifyPropertyChangedFor(nameof(CanFetchModelCandidates)), NotifyPropertyChangedFor(nameof(CanShareApprovalsInChat)), NotifyPropertyChangedFor(nameof(CanConfigureAgentPermission)), NotifyPropertyChangedFor(nameof(AgentPermissionStatus))]
    public partial string AgentProvider { get; set; } = "codex";
    public bool IsCodexProvider => AgentProvider == "codex";
    public bool IsCustomProvider => AgentProvider == "custom";
    public bool IsBasicProvider => AgentProvider is "gemini" or "github-copilot" or "custom";
    public bool IsArgumentsEditable => IsBasicProvider;
    public bool SupportsModel => AgentProvider is "codex" or "claude";
    public string AgentProviderDisplayName => AgentProvider switch
    {
        "codex" => "Codex", "claude" => "Claude Code", "gemini" => "Gemini CLI",
        "github-copilot" => "GitHub Copilot CLI", _ => ViewModelText.Get("CustomCliName")
    };
    public string BasicProviderHelp => ViewModelText.Get("BasicProviderHelp");
    public bool CanFetchModelCandidates => SupportsModel && IsSettingsLoaded && !IsRunning && !_closing;
    [ObservableProperty] public partial string AgentModel { get; set; } = "";
    partial void OnAgentModelChanged(string value)
    {
        if (!_initializingSettings && !_applyingCatalogSelection && IsSettingsLoaded &&
            !string.Equals(_effortModel, NormalizeModel(value), StringComparison.Ordinal))
        {
            AgentReasoningEffort = null;
            _unsupportedSavedEffort = false;
            _effortModel = NormalizeModel(value);
        }
        if (!_applyingCatalogSelection) PopulateGlobalEfforts();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial ReasoningEffortChoice? SelectedReasoningEffort { get; set; }
    partial void OnSelectedReasoningEffortChanged(ReasoningEffortChoice? value)
    {
        if (value is null) return;
        AgentReasoningEffort = value.Id;
        _unsupportedSavedEffort = false;
        ModelEffortStatus = "";
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? AgentReasoningEffort { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasModelEffortStatus))]
    public partial string ModelEffortStatus { get; set; } = "";
    public bool HasModelEffortStatus => !string.IsNullOrWhiteSpace(ModelEffortStatus);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WizardAutoConnect))] public partial string AutoConnectProvider { get; set; } = "none";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool CanChangeStartup { get; set; }
    [ObservableProperty] public partial string StartupStatus { get; set; } = ViewModelText.Get("CheckingStartupSettings");
    private readonly Startup.StartupTaskService _startup = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExecMode)), NotifyPropertyChangedFor(nameof(CanShareApprovalsInChat)), NotifyPropertyChangedFor(nameof(CanConfigureAgentPermission)), NotifyPropertyChangedFor(nameof(AgentPermissionStatus))]
    public partial bool UseAppServer { get; set; }
    partial void OnUseAppServerChanged(bool value)
    {
        InvalidateModelCatalog();
        if (!value && !_initializingSettings && !IsLoading && AgentPermissionMode == "ask")
            SetAgentPermissionModeAfterCapabilityLoss();
    }
    public bool IsExecMode => !UseAppServer;
    public bool CanShareApprovalsInChat => IsCodexProvider && UseAppServer;
    [ObservableProperty] public partial string MattermostServerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string MattermostChannelId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackBotToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackAppToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string MattermostAllowedUserIds { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackAllowedUserIds { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShareApprovalsInChat { get; set; }
    [ObservableProperty] public partial bool RequireApproval { get; set; } = true;
    [ObservableProperty] public partial string AgentPermissionMode { get; set; } = "inherit";
    partial void OnAgentPermissionModeChanged(string value)
    {
        if (!_adjustingAgentPermissionMode) _agentPermissionModeAdjusted = false;
        OnPropertyChanged(nameof(CanConfigureAgentNetwork));
        OnPropertyChanged(nameof(AgentPermissionStatus));
        OnPropertyChanged(nameof(ExecutionPolicySummary));
    }
    [ObservableProperty] public partial bool AllowAgentNetwork { get; set; }
    [ObservableProperty] public partial string TrustedWorkspaceRoots { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChatApprovalMode { get; set; } = "always";
    [ObservableProperty] public partial string ChatResultMode { get; set; } = "manual";
    [ObservableProperty] public partial string SlackAllowedChannelIds { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackAllowedThreadIds { get; set; } = string.Empty;
    [ObservableProperty] public partial string MattermostAllowedThreadIds { get; set; } = string.Empty;
    public bool CanConfigureAgentNetwork => IsCodexProvider && AgentPermissionMode is "workspace-write" or "ask";
    public bool CanConfigureAgentPermission => !IsBasicProvider;
    public string AgentPermissionStatus => IsBasicProvider ? ViewModelText.Get("BasicPermissionManagedByCli")
        : _agentPermissionModeAdjusted ? ViewModelText.Get("AskPermissionChangedToReadOnly") : string.Empty;
    private string NetworkPolicyLabel => !IsCodexProvider ? ViewModelText.Get("NetworkManagedByCli")
        : AgentPermissionMode == "full-access" ? ViewModelText.Get("NetworkUnrestrictedFullAccess")
        : AgentPermissionMode == "inherit" && !UseAppServer ? ViewModelText.Get("UseCliNetworkSettings")
        : AgentPermissionMode is "inherit" or "read-only" ? ViewModelText.Get("NotAllowed")
        : AllowAgentNetwork ? ViewModelText.Get("Allowed") : ViewModelText.Get("NotAllowed");
    public string ExecutionPolicySummary => ViewModelText.Get("ExecutionPolicySummary",
        !string.IsNullOrWhiteSpace(SlackAllowedChannelIds) || !string.IsNullOrWhiteSpace(SlackAllowedThreadIds) || !string.IsNullOrWhiteSpace(MattermostAllowedThreadIds) ? ViewModelText.Get("SpecifiedConversationsOnly") : ViewModelText.Get("AllowedUsersOnly"),
        ChatApprovalLabel(ChatApprovalMode),
        string.IsNullOrWhiteSpace(TrustedWorkspaceRoots) ? ViewModelText.Get("CurrentWorkspaceOnly") : ViewModelText.Get("TrustedWorkspacesOnly"),
        PermissionLabel(AgentPermissionMode), NetworkPolicyLabel, ResultLabel(ChatResultMode));
    [ObservableProperty] public partial string Prompt { get; set; } = string.Empty;
    [ObservableProperty] public partial string Output { get; set; } = ViewModelText.Get("ExecutionOutputPlaceholder");
    [ObservableProperty] public partial string UsageSummary { get; set; } = ViewModelText.Get("UsagePlaceholder");
    [ObservableProperty] public partial string SettingsLocation { get; set; } = string.Empty;
    [ObservableProperty] public partial string SavedAgentSummary { get; set; } = ViewModelText.Get("NotLoaded");
    [ObservableProperty] public partial double SetupProgressValue { get; set; }
    [ObservableProperty] public partial string SetupStepOneStatus { get; set; } = ViewModelText.Get("LoadingSettingsPeriod");
    [ObservableProperty] public partial string SetupStepTwoStatus { get; set; } = ViewModelText.Get("LoadingSettingsPeriod");
    [ObservableProperty] public partial string SetupStepThreeStatus { get; set; } = ViewModelText.Get("CheckingConnectionState");
    [ObservableProperty] public partial string SetupCompletionSummary { get; set; } = ViewModelText.Get("CheckingSettings");
    [ObservableProperty] public partial string SetupNextAction { get; set; } = ViewModelText.Get("CheckingSavedSettings");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSetupDraftNotice))]
    public partial string SetupDraftNotice { get; set; } = "";
    public bool HasSetupDraftNotice => !string.IsNullOrEmpty(SetupDraftNotice);

    public void SetChatConnectionState(bool connected, bool connecting, bool failed = false)
    {
        _chatConnectionConfirmed = connected;
        if (!connected) ClearWizardConnection();
        _chatConnectionPreparing = connecting;
        _chatConnectionFailed = failed;
        RefreshSetupStepper();
    }

    public Func<BridgeSettings, string, Task<bool>>? ConfirmExecutionAsync { get; set; }
    public Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? ServerApprovalHandler { get; set; }
    public Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? ServerInputHandler { get; set; }
    public string MattermostToken { get; private set; } = string.Empty;
    public event Action? SettingsSaved;

    public async Task LoadAsync()
    {
        IsLoading = true;
        IsSettingsLoaded = false;
        StatusTitle = ViewModelText.Get("LoadingSettings");
        StatusMessage = ViewModelText.Get("CheckingSavedSettings");
        StatusSeverity = "Informational";
        IsStatusOpen = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            _savedWorkspaceExists = Directory.Exists(_settings.WorkspaceRoot);
            WorkspaceRoot = _settings.WorkspaceRoot;
            AgentExecutable = _settings.AgentExecutable;
            AgentArguments = _settings.AgentArguments;
            AgentProvider = _settings.AgentProvider;
            _initializingSettings = true;
            AgentModel = _settings.AgentModel ?? "";
            AgentReasoningEffort = _settings.AgentReasoningEffort;
            _effortModel = NormalizeModel(AgentModel);
            _unsupportedSavedEffort = false;
            _hasCatalogForCurrentModel = false;
            SelectedReasoningEffort = null;
            ModelEffortStatus = _settings.AgentReasoningEffort is { Length: > 0 } savedEffort
                ? ViewModelText.Get("SavedEffortPending", savedEffort)
                : ViewModelText.Get("UseDefaultCliValue");
            _initializingSettings = false;
            AutoConnectProvider = _settings.AutoConnectProvider;
            UseAppServer = _settings.AgentMode == "app-server";
            MattermostServerUrl = _settings.MattermostServerUrl;
            MattermostChannelId = _settings.MattermostChannelId;
            MattermostToken = _settings.MattermostToken;
            SlackBotToken = _settings.SlackBotToken;
            SlackAppToken = _settings.SlackAppToken;
            MattermostAllowedUserIds = _settings.MattermostAllowedUserIds;
            SlackAllowedUserIds = _settings.SlackAllowedUserIds;
            ShareApprovalsInChat = _settings.ShareApprovalsInChat;
            RequireApproval = _settings.RequireApproval;
            AgentPermissionMode = _settings.AgentPermissionMode;
            AllowAgentNetwork = _settings.AllowAgentNetwork;
            TrustedWorkspaceRoots = _settings.TrustedWorkspaceRoots;
            ChatApprovalMode = _settings.ChatApprovalMode;
            ChatResultMode = _settings.ChatResultMode;
            SlackAllowedChannelIds = _settings.SlackAllowedChannelIds;
            SlackAllowedThreadIds = _settings.SlackAllowedThreadIds;
            MattermostAllowedThreadIds = _settings.MattermostAllowedThreadIds;
            SettingsLocation = _settingsStore.DirectoryPath;
            UpdateSavedAgentSummary();
            RefreshSetupStepper();
            StatusTitle = ViewModelText.Get("SettingsLoaded");
            StatusMessage = ViewModelText.Get("SettingsLoadedDetails");
            StatusSeverity = "Success";
            IsStatusOpen = false;
            IsSettingsLoaded = true;
        }
        catch (Exception ex)
        {
            _settings = null;
            StatusTitle = ViewModelText.Get("SettingsLoadFailed");
            StatusMessage = ViewModelText.Get("SettingsLoadFailedDetails", ex.Message);
            StatusSeverity = "Error";
        }
        finally
        {
            IsLoading = false;
        }
        if (IsSettingsLoaded) SettingsSaved?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanReloadSettings))]
    public Task ReloadSettingsAsync() => LoadAsync();

    partial void OnAgentProviderChanged(string value)
    {
        InvalidateModelCatalog();
        if (IsLoading) return;
        UseAppServer = false;
        if (IsBasicProvider) SetAgentPermissionMode("inherit", adjusted: false);
        AgentModel = "";
        AgentReasoningEffort = null;
        AgentExecutable = value switch { "claude" => "claude", "codex" => "codex", "gemini" => "gemini", "github-copilot" => "copilot", _ => "" };
        AgentArguments = value switch
        {
            "claude" => "--print --output-format stream-json --verbose", "codex" => "exec --json -",
            "gemini" => AgentProfile.DefaultGeminiArguments, "github-copilot" => AgentProfile.DefaultGitHubCopilotArguments, _ => ""
        };
    }

    public async Task RefreshStartupAsync()
    {
        var operation = _startupOperations.TryEnter();
        if (operation is null) return;
        CanChangeStartup = false;
        _startupState = null;
        RefreshOverviewStatus();
        try
        {
            ApplyStartupResult(await _startup.GetStateAsync());
        }
        finally
        {
            FinishStartupOperation(operation);
        }
    }

    public async Task ChangeStartupAsync(bool enabled)
    {
        if (!CanChangeStartup || enabled == StartWithWindows) return;
        var operation = _startupOperations.TryEnter();
        if (operation is null) return;
        CanChangeStartup = false;
        _startupState = null;
        StartupStatus = ViewModelText.Get("ChangingStartupSettings");
        RefreshOverviewStatus();
        try
        {
            var result = await _startup.SetEnabledAsync(enabled);
            // A fast OS response must not turn a double-click into enable + disable.
            // Keep the shared guard and disabled control through the settling interval.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            ApplyStartupResult(result);
        }
        finally
        {
            FinishStartupOperation(operation);
        }
    }

    private void ApplyStartupResult(Startup.StartupTaskStatusResult result)
    {
        _startupState = result.State;
        StartWithWindows = result.IsEnabled;
        StartupStatus = result.Message;
        RefreshOverviewStatus();
    }

    private void FinishStartupOperation(IDisposable operation)
    {
        // A refused change may leave the model value unchanged. Still restore the
        // one-way-bound toggle from the actual result before accepting more input.
        OnPropertyChanged(nameof(StartWithWindows));
        operation.Dispose();
        CanChangeStartup = _startupState is Startup.StartupState.Enabled or Startup.StartupState.Disabled;
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveSettingsAsync()
    {
        _lastSaveSucceeded = false;
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null || _unsupportedSavedEffort) return;
        var saved = false;
        try
        {
            var snapshot = CreateSettingsSnapshot() with
            {
                MattermostAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(MattermostAllowedUserIds),
                SlackAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(SlackAllowedUserIds),
                SlackAllowedChannelIds = ChatAccessPolicy.NormalizeScopes(SlackAllowedChannelIds),
                SlackAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(SlackAllowedThreadIds),
                MattermostAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(MattermostAllowedThreadIds),
                TrustedWorkspaceRoots = ExecutionPolicy.NormalizeTrustedRoots(TrustedWorkspaceRoots),
                AgentPermissionMode = ExecutionPolicy.NormalizePermissionMode(AgentPermissionMode),
                ChatApprovalMode = ExecutionPolicy.NormalizeChatApprovalMode(ChatApprovalMode),
                ChatResultMode = ExecutionPolicy.NormalizeChatResultMode(ChatResultMode)
            };
            if (!string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot))
            {
                if (!Path.IsPathFullyQualified(snapshot.WorkspaceRoot) || !Directory.Exists(snapshot.WorkspaceRoot))
                    throw new InvalidOperationException(ViewModelText.Get("WorkspaceMustBeExistingAbsolutePath"));
                if (!ExecutionPolicy.IsWorkspaceTrusted(snapshot, snapshot.WorkspaceRoot))
                    throw new InvalidOperationException(ViewModelText.Get("WorkspaceOutsideTrustedRoots"));
            }
            if (snapshot.AgentProvider == AgentProfile.Codex && AgentProfile.ResolveProvider(snapshot) == AgentProfile.Custom &&
                (snapshot.AgentPermissionMode != "inherit" ||
                 snapshot.AgentModel is not null || snapshot.AgentReasoningEffort is not null))
                throw new InvalidOperationException(ViewModelText.Get("CodexExecutableRequiredForAgentSettings"));
            _ = AgentProfile.BuildInvocation(snapshot);
            await _settingsStore.SaveAsync(snapshot);
            _settings = snapshot;
            saved = true;
            _lastSaveSucceeded = true;
            MattermostAllowedUserIds = snapshot.MattermostAllowedUserIds;
            SlackAllowedUserIds = snapshot.SlackAllowedUserIds;
            SlackAllowedChannelIds = snapshot.SlackAllowedChannelIds;
            SlackAllowedThreadIds = snapshot.SlackAllowedThreadIds;
            MattermostAllowedThreadIds = snapshot.MattermostAllowedThreadIds;
            TrustedWorkspaceRoots = snapshot.TrustedWorkspaceRoots;
            AgentPermissionMode = snapshot.AgentPermissionMode;
            ChatApprovalMode = snapshot.ChatApprovalMode;
            ChatResultMode = snapshot.ChatResultMode;
            _savedWorkspaceExists = Directory.Exists(_settings.WorkspaceRoot);
            UpdateSavedAgentSummary();
            RefreshSetupStepper();
            SetStatus(ViewModelText.Get("SettingsSaved"), ViewModelText.Get("SettingsSavedReconnect"), "Success");
        }
        catch (Exception ex)
        {
            SetStatus(ViewModelText.Get("SettingsSaveFailed"), ex.Message, "Error");
        }
        if (saved) SettingsSaved?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null || _unsupportedSavedEffort) return;
        var promptSnapshot = Prompt;
        var settingsSnapshot = CreateSettingsSnapshot();
        if (string.IsNullOrWhiteSpace(promptSnapshot))
        {
            SetStatus(ViewModelText.Get("PromptRequired"), ViewModelText.Get("PromptRequiredDetails"), "Warning");
            return;
        }

        IsRunning = true;
        if (RequireApproval)
        {
            if (ConfirmExecutionAsync is null)
            {
                SetStatus(ViewModelText.Get("CannotStartExecution"), ViewModelText.Get("CannotStartExecutionDetails"), "Error");
                IsRunning = false;
                return;
            }
            try
            {
                if (!await ConfirmExecutionAsync(settingsSnapshot, promptSnapshot))
                {
                    SetStatus(ViewModelText.Get("ExecutionCancelled"), ViewModelText.Get("ExecutionCancelledAtDialog"), "Informational");
                    IsRunning = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus(ViewModelText.Get("ConfirmationDialogFailed"), ex.Message, "Error");
                IsRunning = false;
                return;
            }
        }

        if (_closing) { IsRunning = false; return; }
        var execution = new ExecutionRecord
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            Source = "local",
            WorkspaceRoot = settingsSnapshot.WorkspaceRoot,
            AgentModel = settingsSnapshot.AgentModel,
            AgentReasoningEffort = settingsSnapshot.AgentReasoningEffort,
            Status = "running"
        };
        var runCancellation = new CancellationTokenSource();
        _runCancellation = runCancellation;
        try
        {
            await _executionHistoryStore.RecordAsync(execution);
        }
        catch
        {
            _executionHistoryStore.EndTracking(execution.Id);
            _runCancellation?.Dispose();
            _runCancellation = null;
            Output = ViewModelText.Get("ExecutionNotStartedHistoryFailed");
            SetStatus(ViewModelText.Get("HistorySaveFailed"), ViewModelText.Get("HistorySaveFailedDetails"), "Error");
            IsRunning = false;
            return;
        }

        var finalStatus = "failed";
        int? exitCode = null;
        string? sessionId = null;
        AgentUsage? usage = null;
        AgentExecutionMetrics? metrics = null;
        var auditWriteFailed = !await TryAppendAuditAsync(execution, "started");
        if (_closing || runCancellation.IsCancellationRequested)
        {
            finalStatus = "cancelled";
            SetStatus(ViewModelText.Get("ExecutionCancelled"), _closing
                ? ViewModelText.Get("ExecutionNotStartedWindowClosed")
                : ViewModelText.Get("ExecutionCancelledBeforeStart"), "Warning");
        }
        else
        {
            Output = ViewModelText.Get("AgentRunning");
            UsageSummary = ViewModelText.Get("UsageWhileRunning");
            SetStatus(ViewModelText.Get("Running"), ViewModelText.Get("CancelExecutionInstruction"), "Informational");
            try
            {
                AgentRunResult result;
                if (settingsSnapshot.AgentMode == "app-server")
                {
                    var run = await _appServerRunner.RunAsync(settingsSnapshot, promptSnapshot,
                        new AppServerRunOptions(ApprovalHandler: ServerApprovalHandler, InputHandler: ServerInputHandler), runCancellation.Token);
                    result = run.Result;
                    sessionId = run.SessionId;
                }
                else result = await _agentRunner.RunAsync(settingsSnapshot, promptSnapshot, runCancellation.Token);
                exitCode = result.ExitCode;
                sessionId ??= result.Claude?.SessionId ?? result.Codex?.SessionId;
                usage = result.Usage;
                metrics = result.Metrics;
                UsageSummary = UsageUi.UsageSummaryFormatter.Format(usage, metrics);
                finalStatus = result.Success ? "completed" : "failed";
                var displayOutput = result.Claude?.FinalMessage ?? result.Codex?.FinalMessage ?? result.Output;
                Output = result.Success || string.IsNullOrWhiteSpace(result.Error)
                    ? displayOutput
                    : $"{displayOutput}{(string.IsNullOrEmpty(displayOutput) ? string.Empty : Environment.NewLine)}{result.Error}";
                if (result.HasOutputTruncated) Output += "\n" + ViewModelText.Get("OutputTruncated");
                if (result.Success)
                    SetStatus(ViewModelText.Get("ExecutionCompleted"), ViewModelText.Get("ExitCode", result.ExitCode), "Success");
                else
                    SetStatus(ViewModelText.Get("ExecutionFailed"), ViewModelText.Get("ExitCode", result.ExitCode), "Error");
            }
            catch (OperationCanceledException)
            {
                finalStatus = "cancelled";
                Output = ViewModelText.Get("ExecutionCancelled");
                SetStatus(ViewModelText.Get("ExecutionCancelled"), ViewModelText.Get("AgentProcessStopped"), "Warning");
            }
            catch (Exception ex)
            {
                finalStatus = "failed";
                Output = ex.ToString();
                SetStatus(ViewModelText.Get("ExecutionFailed"), ex.Message, "Error");
            }
        }

        var finalizedRecord = execution with
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = finalStatus,
            ExitCode = exitCode,
            SessionId = sessionId,
            Usage = usage,
            Metrics = metrics
        };
        UsageSummary = UsageUi.UsageSummaryFormatter.Format(usage, metrics);
        try
        {
            await _executionHistoryStore.RecordAsync(finalizedRecord, CancellationToken.None);
        }
        catch
        {
            StatusMessage = $"{StatusMessage} {ViewModelText.Get("ExecutionHistoryFinalStateSaveFailed")}";
            StatusSeverity = "Warning";
        }
        auditWriteFailed |= !await TryAppendAuditAsync(finalizedRecord, finalStatus);
        if (auditWriteFailed)
        {
            StatusMessage = $"{StatusMessage} {ViewModelText.Get("AuditRecordSaveFailed")}";
            StatusSeverity = "Warning";
        }
        _executionHistoryStore.EndTracking(execution.Id);
        _runCancellation?.Dispose();
        _runCancellation = null;
        IsRunning = false;
    }

    private async Task<bool> TryAppendAuditAsync(ExecutionRecord execution, string kind)
    {
        try
        {
            await _auditStore.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, execution.Id,
                execution.Source, execution.ChannelId, execution.ThreadId), CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CanRun() => IsSettingsLoaded && !IsRunning && !_unsupportedSavedEffort;
    private bool CanSaveSettings() => IsSettingsLoaded && !IsLoading && !IsRunning && !_unsupportedSavedEffort;
    private bool CanReloadSettings() => !IsLoading && !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelRun() => _runCancellation?.Cancel();

    public void CancelForWindowClose()
    {
        _closing = true;
        _runCancellation?.Cancel();
        CancelCliCheck();
        ModelCatalog.Dispose();
    }

    public void CancelModelCatalogLookup() => ModelCatalog.CancelLookup();

    private void InvalidateModelCatalog()
    {
        if (ModelCatalog is not null)
            ModelCatalog.Invalidate(IsBasicProvider ? ViewModelText.Get("BasicModelManagedByCli") : null);
    }

    public void SetMattermostToken(string value)
    {
        MattermostToken = value;
        RefreshSetupStepper();
    }

    private void RefreshSetupStepper()
    {
        if (IsLoading)
        {
            SetupProgressValue = 0;
            SetupStepOneStatus = SetupStepTwoStatus = ViewModelText.Get("LoadingSettingsPeriod");
            SetupStepThreeStatus = ViewModelText.Get("CheckingConnectionState");
            SetupCompletionSummary = ViewModelText.Get("LoadingSettingsPeriod");
            SetupNextAction = ViewModelText.Get("CheckingSavedSettings");
            SetupDraftNotice = "";
            RefreshOverviewStatus();
            return;
        }
        if (!IsSettingsLoaded || _settings is null)
        {
            SetupProgressValue = 0;
            SetupStepOneStatus = ViewModelText.Get("UnableToLoadSettingsReload");
            SetupStepTwoStatus = SetupStepThreeStatus = ViewModelText.Get("UnableToCheckSettings");
            SetupCompletionSummary = ViewModelText.Get("UnableToCheckSettings");
            SetupNextAction = ViewModelText.Get("CheckSettingsFileAndReload");
            SetupDraftNotice = "";
            RefreshOverviewStatus();
            return;
        }

        var agentReady = !string.IsNullOrWhiteSpace(_settings.AgentExecutable) && !string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) && _savedWorkspaceExists;
        var mattermostCredentials = !string.IsNullOrWhiteSpace(_settings.MattermostServerUrl) && !string.IsNullOrWhiteSpace(_settings.MattermostChannelId) && !string.IsNullOrWhiteSpace(_settings.MattermostToken);
        var slackCredentials = !string.IsNullOrWhiteSpace(_settings.SlackBotToken) && !string.IsNullOrWhiteSpace(_settings.SlackAppToken);
        var mattermostAllowed = HasAllowedUsers(_settings.MattermostAllowedUserIds);
        var slackAllowed = HasAllowedUsers(_settings.SlackAllowedUserIds);
        var connectionReady = (mattermostCredentials && mattermostAllowed) || (slackCredentials && slackAllowed);
        SetupStepOneStatus = agentReady ? ViewModelText.Get("SetupAgentReady")
            : string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) ? ViewModelText.Get("SetupAgentWorkspaceMissing")
            : !_savedWorkspaceExists ? ViewModelText.Get("SetupWorkspaceNotFound")
            : ViewModelText.Get("SetupExecutableMissing");
        SetupStepTwoStatus = connectionReady ? ViewModelText.Get("SetupChatConfigSaved")
            : mattermostCredentials && !mattermostAllowed || slackCredentials && !slackAllowed ? ViewModelText.Get("SetupAllowedUsersMissing")
            : agentReady ? ViewModelText.Get("SetupChatConfigMissing")
            : ViewModelText.Get("SetupChatAfterStepOne");
        SetupStepThreeStatus = _chatConnectionConfirmed ? ViewModelText.Get("SetupWaitingForChatRequest")
            : _chatConnectionPreparing ? ViewModelText.Get("SetupConnecting")
            : agentReady && connectionReady ? ViewModelText.Get("SetupConnectSavedDestination")
            : ViewModelText.Get("SetupConnectAfterSaving");
        var completed = (agentReady ? 1 : 0) + (connectionReady ? 1 : 0) + (_chatConnectionConfirmed ? 1 : 0);
        SetupCompletionSummary = ViewModelText.Get("SetupCompletionSummary", completed);
        SetupProgressValue = completed / 3d * 100;
        SetupNextAction = !agentReady ? ViewModelText.Get("NextConfigureAgentWorkspace")
            : !connectionReady ? ViewModelText.Get("NextSaveConnection")
            : !_chatConnectionConfirmed ? ViewModelText.Get("NextConnectToChat")
            : ViewModelText.Get("SetupCompleteReadyForChat");
        var draft = CreateSettingsSnapshot();
        SetupDraftNotice = _settings != draft ? ViewModelText.Get("UnsavedSetupChanges") : "";
        RefreshOverviewStatus();
    }

    private static bool HasAllowedUsers(string value)
    {
        try { return ChatAccessPolicy.NormalizeAllowedUsers(value).Length > 0; }
        catch (FormatException) { return false; }
    }

    private BridgeSettings CreateSettingsSnapshot() => _settings! with
    {
        WorkspaceRoot = WorkspaceRoot.Trim(),
        AgentExecutable = AgentExecutable.Trim(),
        AgentArguments = AgentArguments.Trim(),
        AgentProvider = AgentProvider,
        AgentModel = string.IsNullOrWhiteSpace(AgentModel) ? null : AgentModel.Trim(),
        AgentReasoningEffort = AgentReasoningEffort,
        AutoConnectProvider = AutoConnectProvider,
        AgentMode = UseAppServer ? "app-server" : "exec",
        MattermostServerUrl = MattermostServerUrl.Trim(),
        MattermostChannelId = MattermostChannelId.Trim(),
        MattermostToken = MattermostToken,
        SlackBotToken = SlackBotToken,
        SlackAppToken = SlackAppToken,
        MattermostAllowedUserIds = MattermostAllowedUserIds,
        SlackAllowedUserIds = SlackAllowedUserIds,
        ShareApprovalsInChat = ShareApprovalsInChat,
        RequireApproval = RequireApproval,
        AgentPermissionMode = AgentPermissionMode,
        AllowAgentNetwork = AllowAgentNetwork,
        TrustedWorkspaceRoots = TrustedWorkspaceRoots,
        ChatApprovalMode = ChatApprovalMode,
        ChatResultMode = ChatResultMode,
        SlackAllowedChannelIds = SlackAllowedChannelIds,
        SlackAllowedThreadIds = SlackAllowedThreadIds,
        MattermostAllowedThreadIds = MattermostAllowedThreadIds
    };

    private static string PermissionLabel(string value) => value switch
    {
        "read-only" => ViewModelText.Get("PermissionReadOnly"), "workspace-write" => ViewModelText.Get("PermissionWorkspaceWrite"),
        "full-access" => ViewModelText.Get("PermissionFullAccess"), "ask" => ViewModelText.Get("PermissionAsk"), _ => ViewModelText.Get("PermissionCliDefault")
    };
    private static string ChatApprovalLabel(string value) => value switch
    { "first" => ViewModelText.Get("ApprovalFirstOnly"), "auto" => ViewModelText.Get("ApprovalAutomatic"), _ => ViewModelText.Get("ApprovalEveryTime") };
    private static string ResultLabel(string value) => value switch
    { "auto" => ViewModelText.Get("ResultAutomatic"), "none" => ViewModelText.Get("ResultDoNotShare"), _ => ViewModelText.Get("ResultManual") };

    private void UpdateSavedAgentSummary() => SavedAgentSummary = string.IsNullOrWhiteSpace(AgentExecutable)
        ? ViewModelText.Get("AgentNotConfigured")
        : ViewModelText.Get("SavedAgentSummary", AgentExecutable, string.IsNullOrWhiteSpace(AgentModel) ? ViewModelText.Get("DefaultCliValue") : AgentModel, AgentReasoningEffort ?? ViewModelText.Get("DefaultCliValue"), ViewModelText.Get(UseAppServer ? "InteractiveExecution" : "RunToCompletion"));

    private void ApplyCatalogModelSelection(ModelOption option)
    {
        var previous = NormalizeModel(AgentModel);
        _applyingCatalogSelection = true;
        AgentModel = option.Id;
        _applyingCatalogSelection = false;
        if (!string.Equals(previous, NormalizeModel(option.Id), StringComparison.Ordinal))
        {
            AgentReasoningEffort = null;
            _effortModel = NormalizeModel(option.Id);
            _unsupportedSavedEffort = false;
        }
        PopulateGlobalEfforts(option);
    }

    private void PopulateGlobalEfforts(ModelOption? knownModel = null)
    {
        var modelId = NormalizeModel(AgentModel);
        var model = knownModel is not null && string.Equals(knownModel.Id, modelId, StringComparison.Ordinal)
            ? knownModel
            : ModelCatalog.Models.FirstOrDefault(option => string.Equals(option.Id, modelId, StringComparison.Ordinal));
        ReasoningEffortCandidates.Clear();
        ReasoningEffortCandidates.Add(ReasoningEffortChoice.Default);
        _hasCatalogForCurrentModel = model is not null && !ModelCatalog.IsLoading;
        if (model is not null)
            foreach (var effort in model.SupportedReasoningEfforts)
                ReasoningEffortCandidates.Add(new(effort.Id, effort.SelectionLabel));

        if (string.IsNullOrWhiteSpace(AgentReasoningEffort))
        {
            _unsupportedSavedEffort = false;
            SelectedReasoningEffort = ReasoningEffortCandidates[0];
            ModelEffortStatus = ViewModelText.Get("UseDefaultCliValue");
        }
        else if (!_hasCatalogForCurrentModel)
        {
            _unsupportedSavedEffort = false;
            SelectedReasoningEffort = null;
            ModelEffortStatus = ViewModelText.Get("SavedEffortPending", AgentReasoningEffort);
        }
        else
        {
            var selected = ReasoningEffortCandidates.FirstOrDefault(item => string.Equals(item.Id, AgentReasoningEffort, StringComparison.Ordinal));
            SelectedReasoningEffort = selected;
            _unsupportedSavedEffort = selected is null;
            ModelEffortStatus = selected is null
                ? ViewModelText.Get("SavedEffortUnsupported", AgentReasoningEffort)
                : ViewModelText.Get("SavedEffort", selected.SelectionLabel);
        }
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private static string? NormalizeModel(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetAgentPermissionModeAfterCapabilityLoss() => SetAgentPermissionMode("read-only", adjusted: true);

    private void SetAgentPermissionMode(string value, bool adjusted)
    {
        _agentPermissionModeAdjusted = adjusted;
        _adjustingAgentPermissionMode = true;
        AgentPermissionMode = value;
        _adjustingAgentPermissionMode = false;
        OnPropertyChanged(nameof(AgentPermissionStatus));
    }

    private void SetStatus(string title, string message, string severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}

public sealed record ReasoningEffortChoice(string? Id, string SelectionLabel)
{
    public static ReasoningEffortChoice Default { get; } = new(null, ViewModelText.Get("DefaultCliValue"));
    public static ReasoningEffortChoice InheritDefault { get; } = new(null, ViewModelText.Get("InheritAgentSettings"));
}
