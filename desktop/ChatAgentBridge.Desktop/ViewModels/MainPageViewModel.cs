using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge_Desktop.ModelSelection;
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
    private bool _savedWorkspaceExists;

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
            if (e.PropertyName is nameof(IsLoading) or nameof(IsSettingsLoaded) or nameof(WorkspaceRoot) or
                nameof(AgentExecutable) or nameof(AgentArguments) or nameof(AgentProvider) or nameof(AgentModel) or
                nameof(AgentReasoningEffort) or nameof(UseAppServer) or nameof(AutoConnectProvider) or nameof(RequireApproval) or
                nameof(MattermostServerUrl) or nameof(MattermostChannelId) or nameof(MattermostToken) or
                nameof(SlackBotToken) or nameof(SlackAppToken))
                RefreshSetupStepper();
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

    [ObservableProperty] public partial string StatusTitle { get; set; } = "設定を読み込んでいます";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "保存済みの設定を確認しています。";
    [ObservableProperty] public partial string StatusSeverity { get; set; } = "Informational";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; } = true;
    [ObservableProperty] public partial string WorkspaceRoot { get; set; } = string.Empty;
    partial void OnWorkspaceRootChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty] public partial string AgentExecutable { get; set; } = "codex";
    partial void OnAgentExecutableChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty] public partial string AgentArguments { get; set; } = "exec --json -";
    partial void OnAgentArgumentsChanged(string value) => InvalidateModelCatalog();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCodexProvider)), NotifyPropertyChangedFor(nameof(IsCustomProvider)), NotifyPropertyChangedFor(nameof(SupportsModel)), NotifyPropertyChangedFor(nameof(CanFetchModelCandidates))]
    public partial string AgentProvider { get; set; } = "codex";
    public bool IsCodexProvider => AgentProvider == "codex";
    public bool IsCustomProvider => AgentProvider == "custom";
    public bool SupportsModel => !IsCustomProvider;
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
    [ObservableProperty] public partial string AutoConnectProvider { get; set; } = "none";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool CanChangeStartup { get; set; }
    [ObservableProperty] public partial string StartupStatus { get; set; } = "Windowsの起動設定を確認しています…";
    private readonly Startup.StartupTaskService _startup = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExecMode))]
    public partial bool UseAppServer { get; set; }
    partial void OnUseAppServerChanged(bool value) => InvalidateModelCatalog();
    public bool IsExecMode => !UseAppServer;
    [ObservableProperty] public partial string MattermostServerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string MattermostChannelId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackBotToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackAppToken { get; set; } = string.Empty;
    [ObservableProperty] public partial bool RequireApproval { get; set; } = true;
    [ObservableProperty] public partial string Prompt { get; set; } = string.Empty;
    [ObservableProperty] public partial string Output { get; set; } = "実行結果はここに表示されます。";
    [ObservableProperty] public partial string UsageSummary { get; set; } = "トークン利用量は実行後に表示します。未取得の値は0として扱いません。";
    [ObservableProperty] public partial string SettingsLocation { get; set; } = string.Empty;
    [ObservableProperty] public partial string SavedAgentSummary { get; set; } = "未読み込み";
    [ObservableProperty] public partial double SetupProgressValue { get; set; }
    [ObservableProperty] public partial string SetupStepOneStatus { get; set; } = "設定を読み込んでいます。";
    [ObservableProperty] public partial string SetupStepTwoStatus { get; set; } = "設定を読み込んでいます。";
    [ObservableProperty] public partial string SetupStepThreeStatus { get; set; } = "接続状態を確認しています。";
    [ObservableProperty] public partial string SetupCompletionSummary { get; set; } = "設定を確認しています。";
    [ObservableProperty] public partial string SetupNextAction { get; set; } = "保存済みの設定を確認しています。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSetupDraftNotice))]
    public partial string SetupDraftNotice { get; set; } = "";
    public bool HasSetupDraftNotice => !string.IsNullOrEmpty(SetupDraftNotice);

    public void SetChatConnectionState(bool connected, bool connecting)
    {
        _chatConnectionConfirmed = connected;
        _chatConnectionPreparing = connecting;
        RefreshSetupStepper();
    }

    public Func<BridgeSettings, string, Task<bool>>? ConfirmExecutionAsync { get; set; }
    public Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? ServerApprovalHandler { get; set; }
    public Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? ServerInputHandler { get; set; }
    public string MattermostToken { get; private set; } = string.Empty;

    public async Task LoadAsync()
    {
        IsLoading = true;
        IsSettingsLoaded = false;
        StatusTitle = "設定を読み込んでいます";
        StatusMessage = "保存済みの設定を確認しています。";
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
                ? $"保存済みの推論レベル: {savedEffort}（モデル候補を取得するまで対応状況は未確認です）"
                : "CLIの既定値を使用します。";
            _initializingSettings = false;
            AutoConnectProvider = _settings.AutoConnectProvider;
            UseAppServer = _settings.AgentMode == "app-server";
            MattermostServerUrl = _settings.MattermostServerUrl;
            MattermostChannelId = _settings.MattermostChannelId;
            MattermostToken = _settings.MattermostToken;
            SlackBotToken = _settings.SlackBotToken;
            SlackAppToken = _settings.SlackAppToken;
            RequireApproval = _settings.RequireApproval;
            SettingsLocation = _settingsStore.DirectoryPath;
            UpdateSavedAgentSummary();
            RefreshSetupStepper();
            StatusTitle = "設定を読み込みました";
            StatusMessage = "ワークスペースとエージェントを設定して実行できます。";
            StatusSeverity = "Success";
            IsSettingsLoaded = true;
        }
        catch (Exception ex)
        {
            _settings = null;
            StatusTitle = "設定を読み込めませんでした";
            StatusMessage = $"設定ファイルを確認してから再読み込みしてください。{ex.Message}";
            StatusSeverity = "Error";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReloadSettings))]
    public Task ReloadSettingsAsync() => LoadAsync();

    partial void OnAgentProviderChanged(string value)
    {
        InvalidateModelCatalog();
        if (value == "custom")
            ModelCatalog.Status = "このCLIでは候補を取得できません。モデル名を直接入力してください。";
        if (IsLoading) return;
        UseAppServer = false;
        AgentModel = "";
        AgentReasoningEffort = null;
        AgentExecutable = value == "claude" ? "claude" : value == "codex" ? "codex" : AgentExecutable;
        AgentArguments = value == "claude" ? "--print --output-format stream-json --verbose" : value == "codex" ? "exec --json -" : AgentArguments;
    }

    public async Task RefreshStartupAsync()
    {
        CanChangeStartup = false;
        var result = await _startup.GetStateAsync();
        StartWithWindows = result.IsEnabled;
        StartupStatus = result.Message;
        CanChangeStartup = result.State is Startup.StartupState.Enabled or Startup.StartupState.Disabled;
    }

    public async Task ChangeStartupAsync(bool enabled)
    {
        if (!CanChangeStartup || enabled == StartWithWindows) return;
        CanChangeStartup = false;
        var result = await _startup.SetEnabledAsync(enabled);
        StartWithWindows = result.IsEnabled;
        StartupStatus = result.Message;
        CanChangeStartup = result.State is Startup.StartupState.Enabled or Startup.StartupState.Disabled;
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveSettingsAsync()
    {
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null || _unsupportedSavedEffort) return;
        try
        {
            var snapshot = CreateSettingsSnapshot();
            await _settingsStore.SaveAsync(snapshot);
            _settings = snapshot;
            _savedWorkspaceExists = Directory.Exists(_settings.WorkspaceRoot);
            UpdateSavedAgentSummary();
            RefreshSetupStepper();
            SetStatus("設定を保存しました", "「チャット受信」から接続してください。接続中の場合は再接続すると反映されます。", "Success");
        }
        catch (Exception ex)
        {
            SetStatus("設定を保存できませんでした", ex.Message, "Error");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null || _unsupportedSavedEffort) return;
        var promptSnapshot = Prompt;
        var settingsSnapshot = CreateSettingsSnapshot();
        if (string.IsNullOrWhiteSpace(promptSnapshot))
        {
            SetStatus("プロンプトを入力してください", "実行する内容を入力してから開始してください。", "Warning");
            return;
        }

        IsRunning = true;
        if (RequireApproval)
        {
            if (ConfirmExecutionAsync is null)
            {
                SetStatus("実行を開始できません", "確認ダイアログを表示できないため、安全のため実行を停止しました。", "Error");
                IsRunning = false;
                return;
            }
            try
            {
                if (!await ConfirmExecutionAsync(settingsSnapshot, promptSnapshot))
                {
                    SetStatus("実行を取り消しました", "確認ダイアログでキャンセルされました。", "Informational");
                    IsRunning = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus("確認ダイアログを表示できませんでした", ex.Message, "Error");
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
            Output = "実行は開始されませんでした。実行履歴を保存できません。";
            SetStatus("履歴を保存できず実行を停止しました", "データ保存先の空き容量とアクセス権を確認してください。", "Error");
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
            SetStatus("実行を取り消しました", _closing
                ? "ウィンドウが閉じられたため、エージェントを開始しませんでした。"
                : "実行開始前にキャンセルされました。", "Warning");
        }
        else
        {
            Output = "エージェントを実行しています…";
            UsageSummary = "実行中。利用量は完了後に表示します。";
            SetStatus("実行中", "キャンセルするには「キャンセル」を選択してください。", "Informational");
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
                if (result.HasOutputTruncated) Output += "\n（出力ログの表示上限を超えたため、一部を省略しています。）";
                if (result.Success)
                    SetStatus("実行が完了しました", $"終了コード: {result.ExitCode}", "Success");
                else
                    SetStatus("実行に失敗しました", $"終了コード: {result.ExitCode}", "Error");
            }
            catch (OperationCanceledException)
            {
                finalStatus = "cancelled";
                Output = "実行をキャンセルしました。";
                SetStatus("実行をキャンセルしました", "エージェントプロセスを停止しました。", "Warning");
            }
            catch (Exception ex)
            {
                finalStatus = "failed";
                Output = ex.ToString();
                SetStatus("実行に失敗しました", ex.Message, "Error");
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
            StatusMessage = $"{StatusMessage} 実行は終了しましたが、履歴の終了状態を保存できませんでした。";
            StatusSeverity = "Warning";
        }
        auditWriteFailed |= !await TryAppendAuditAsync(finalizedRecord, finalStatus);
        if (auditWriteFailed)
        {
            StatusMessage = $"{StatusMessage} 監査記録を保存できませんでした。";
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
        ModelCatalog.Dispose();
    }

    public void CancelModelCatalogLookup() => ModelCatalog.CancelLookup();

    private void InvalidateModelCatalog()
    {
        if (ModelCatalog is not null)
            ModelCatalog.Invalidate();
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
            SetupStepOneStatus = SetupStepTwoStatus = "設定を読み込んでいます。";
            SetupStepThreeStatus = "接続状態を確認しています。";
            SetupCompletionSummary = "設定を読み込んでいます。";
            SetupNextAction = "保存済みの設定を確認しています。";
            SetupDraftNotice = "";
            return;
        }
        if (!IsSettingsLoaded || _settings is null)
        {
            SetupProgressValue = 0;
            SetupStepOneStatus = "設定を読み込めません。再読み込みしてください。";
            SetupStepTwoStatus = SetupStepThreeStatus = "設定を確認できません。";
            SetupCompletionSummary = "設定を確認できません。";
            SetupNextAction = "設定ファイルを確認してから再読み込みしてください。";
            SetupDraftNotice = "";
            return;
        }

        var agentReady = !string.IsNullOrWhiteSpace(_settings.AgentExecutable) && !string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) && _savedWorkspaceExists;
        var connectionReady = (!string.IsNullOrWhiteSpace(_settings.MattermostServerUrl) && !string.IsNullOrWhiteSpace(_settings.MattermostChannelId) && !string.IsNullOrWhiteSpace(_settings.MattermostToken)) ||
            (!string.IsNullOrWhiteSpace(_settings.SlackBotToken) && !string.IsNullOrWhiteSpace(_settings.SlackAppToken));
        SetupStepOneStatus = agentReady ? "完了：保存済み。CLIやログイン状態はここでは確認していません。"
            : string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) ? "次のステップ：エージェントと作業フォルダーを設定して保存します。"
            : !_savedWorkspaceExists ? "次のステップ：保存された作業フォルダーが見つかりません。既存のフォルダーを指定して保存します。"
            : "次のステップ：エージェントの実行ファイルを設定して保存します。";
        SetupStepTwoStatus = connectionReady ? "完了：接続情報を保存済み。"
            : agentReady ? "次のステップ：SlackまたはMattermostの接続情報を保存します。"
            : "未設定：ステップ1の後に接続情報を保存します。";
        SetupStepThreeStatus = _chatConnectionConfirmed ? "完了：チャットからの依頼を待っています。"
            : _chatConnectionPreparing ? "接続中：接続先の応答を待っています。"
            : agentReady && connectionReady ? "次のステップ：保存済みの接続先へ接続します。"
            : "未設定：接続情報を保存した後に接続します。";
        var completed = (agentReady ? 1 : 0) + (connectionReady ? 1 : 0) + (_chatConnectionConfirmed ? 1 : 0);
        SetupCompletionSummary = $"{completed} / 3 完了";
        SetupProgressValue = completed / 3d * 100;
        SetupNextAction = !agentReady ? "次の操作：エージェントと既存の作業フォルダーを設定して保存します。"
            : !connectionReady ? "次の操作：接続設定を保存します。"
            : !_chatConnectionConfirmed ? "次の操作：チャット受信で保存済みの接続先へ接続します。"
            : "セットアップ完了。別の端末からチャットで依頼できます。";
        var draft = CreateSettingsSnapshot();
        SetupDraftNotice = _settings != draft ? "未保存の変更があります。ステップには保存済みの設定だけを反映しています。" : "";
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
        RequireApproval = RequireApproval
    };

    private void UpdateSavedAgentSummary() => SavedAgentSummary = string.IsNullOrWhiteSpace(AgentExecutable)
        ? "エージェントは未設定です"
        : $"{AgentExecutable} / モデル: {(string.IsNullOrWhiteSpace(AgentModel) ? "CLIの既定値" : AgentModel)} / 推論レベル: {AgentReasoningEffort ?? "CLIの既定値"} / {(UseAppServer ? "対話的な操作確認" : "完了まで実行")}";

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
            ModelEffortStatus = "CLIの既定値を使用します。";
        }
        else if (!_hasCatalogForCurrentModel)
        {
            _unsupportedSavedEffort = false;
            SelectedReasoningEffort = null;
            ModelEffortStatus = $"保存済みの推論レベル: {AgentReasoningEffort}（モデル候補を取得するまで対応状況は未確認です）";
        }
        else
        {
            var selected = ReasoningEffortCandidates.FirstOrDefault(item => string.Equals(item.Id, AgentReasoningEffort, StringComparison.Ordinal));
            SelectedReasoningEffort = selected;
            _unsupportedSavedEffort = selected is null;
            ModelEffortStatus = selected is null
                ? $"保存済みの推論レベル「{AgentReasoningEffort}」は、このモデルのCLI候補にありません。CLIの既定値を選ぶか、対応候補を選択してください。"
                : $"保存済みの推論レベル: {selected.SelectionLabel}";
        }
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private static string? NormalizeModel(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
    public static ReasoningEffortChoice Default { get; } = new(null, "CLIの既定値");
    public static ReasoningEffortChoice InheritDefault { get; } = new(null, "エージェント設定に従う");
}
