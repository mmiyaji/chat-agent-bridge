using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadSettingsCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial bool IsSettingsLoaded { get; set; }

    [ObservableProperty] public partial string StatusTitle { get; set; } = "設定を読み込んでいます";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "保存済みの設定を確認しています。";
    [ObservableProperty] public partial string StatusSeverity { get; set; } = "Informational";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; } = true;
    [ObservableProperty] public partial string WorkspaceRoot { get; set; } = string.Empty;
    [ObservableProperty] public partial string AgentExecutable { get; set; } = "codex";
    [ObservableProperty] public partial string AgentArguments { get; set; } = "exec --json -";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExecMode))]
    public partial bool UseAppServer { get; set; }
    public bool IsExecMode => !UseAppServer;
    [ObservableProperty] public partial string MattermostServerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string MattermostChannelId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackBotToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string SlackAppToken { get; set; } = string.Empty;
    [ObservableProperty] public partial bool RequireApproval { get; set; } = true;
    [ObservableProperty] public partial string Prompt { get; set; } = string.Empty;
    [ObservableProperty] public partial string Output { get; set; } = "実行結果はここに表示されます。";
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
                finalStatus = result.Success ? "completed" : "failed";
                Output = result.Success || string.IsNullOrWhiteSpace(result.Error)
                    ? result.Output
                    : $"{result.Output}{(string.IsNullOrEmpty(result.Output) ? string.Empty : Environment.NewLine)}{result.Error}";
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
            SessionId = sessionId
        };
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
        : $"{AgentExecutable} {(UseAppServer ? "app-server" : AgentArguments)}".Trim();

    private void SetStatus(string title, string message, string severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
