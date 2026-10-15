using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge_Desktop.ModelSelection;
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

    public ModelCatalogViewModel ModelCatalog { get; }

    public MainPageViewModel()
    {
        ModelCatalog = new ModelCatalogViewModel(CreateSettingsSnapshot, option => AgentModel = option.Id);
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
            WorkspaceRoot = _settings.WorkspaceRoot;
            AgentExecutable = _settings.AgentExecutable;
            AgentArguments = _settings.AgentArguments;
            AgentProvider = _settings.AgentProvider;
            AgentModel = _settings.AgentModel ?? "";
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
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null) return;
        try
        {
            _settings = CreateSettingsSnapshot();
            await _settingsStore.SaveAsync(_settings);
            UpdateSavedAgentSummary();
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
        if (_closing || !IsSettingsLoaded || IsRunning || _settings is null) return;
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

    private bool CanRun() => IsSettingsLoaded && !IsRunning;
    private bool CanSaveSettings() => IsSettingsLoaded && !IsLoading && !IsRunning;
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
        if (_settings is not null) _settings = _settings with { MattermostToken = value };
    }

    private BridgeSettings CreateSettingsSnapshot() => _settings! with
    {
        WorkspaceRoot = WorkspaceRoot.Trim(),
        AgentExecutable = AgentExecutable.Trim(),
        AgentArguments = AgentArguments.Trim(),
        AgentProvider = AgentProvider,
        AgentModel = string.IsNullOrWhiteSpace(AgentModel) ? null : AgentModel.Trim(),
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
        : $"{AgentExecutable} / モデル: {(string.IsNullOrWhiteSpace(AgentModel) ? "CLIの既定値" : AgentModel)} / {(UseAppServer ? "対話的な操作確認" : "完了まで実行")}";

    private void SetStatus(string title, string message, string severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
