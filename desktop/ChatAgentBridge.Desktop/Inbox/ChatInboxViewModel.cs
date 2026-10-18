using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem(ChatRequest request, IChatTransport transport, BridgeSettings settings) : ObservableObject
{
    public ChatRequest Request { get; } = request;
    public IChatTransport Transport { get; } = transport;
    public BridgeSettings Settings { get; } = settings;
    public string Source => $"{Request.Provider} / {Request.ChannelId} / {Request.UserId}";
    public string Prompt => Request.Text;
    public string Workspace => Settings.WorkspaceRoot;
    [ObservableProperty] public partial string State { get; set; } = "承認待ち";
    [ObservableProperty] public partial string SessionLabel { get; set; } = "実行前にセッションを確認します";
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    public Guid? ExecutionId { get; set; }
}

public sealed partial class ChatInboxViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly SessionStore _sessions = new();
    private readonly SessionAgentRunner _runner;
    private readonly AppServerSessionRunner _serverRunner;
    private readonly ChatApprovalBroker _approvalBroker = new();
    private readonly ExecutionHistoryStore _history = new();
    private readonly AuditStore _audit = new();
    private CancellationTokenSource? _connection;
    private CancellationTokenSource? _execution;
    private Task? _receiveTask;
    private IChatTransport? _transport;
    private BridgeSettings? _accessSettings;
    private InboxItem? _activeItem;
    private Task _connectionNotice = Task.CompletedTask;
    private bool _disconnecting;
    private bool _closing;
    private InboxItem? _notificationApprovedItem;
    private int _droppedCount;

    public ObservableCollection<InboxItem> Requests { get; } = [];
    public event Action<InboxItem>? RequestReceived;
    public event Action? ConnectionEnded;
    [ObservableProperty] public partial string NotificationStatus { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "切断中。接続設定を保存してから接続してください。";
    [ObservableProperty] public partial string Output { get; set; } = "受信した依頼は自動実行されません。選択して内容を確認し、承認してください。";
    [ObservableProperty] public partial string UsageSummary { get; set; } = "利用量は実行後に表示します。";
    [ObservableProperty] public partial string OverflowNotice { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectMattermostCommand)), NotifyCanExecuteChangedFor(nameof(ConnectSlackCommand)), NotifyCanExecuteChangedFor(nameof(DisconnectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand))]
    public partial bool IsConnected { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand))]
    public partial bool IsConnectionConfirmed { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand))]
    public partial bool IsExecuting { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand))]
    public partial InboxItem? Selected { get; set; }
    public Func<InboxItem, Task<bool>>? ConfirmAsync { get; set; }
    public Func<InboxItem, string, Task<bool>>? ConfirmReplyAsync { get; set; }
    public Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? ServerApprovalHandler { get; set; }
    public Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? ServerInputHandler { get; set; }

    public ChatInboxViewModel()
    {
        _runner = new SessionAgentRunner(_sessions);
        _serverRunner = new AppServerSessionRunner(_sessions);
    }

    private bool CanConnect() => !IsConnected && !IsExecuting;
    private bool CanApprove() => !IsExecuting && IsConnectionConfirmed && Selected is { State: "承認待ち" } item && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectMattermostAsync() => ConnectAsync(new MattermostWebSocketTransport());

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectSlackAsync() => ConnectAsync(new SlackSocketModeTransport());

    private async Task ConnectAsync(IChatTransport transport)
    {
        if (IsConnected || _closing) { await transport.DisposeAsync(); return; }
        IsConnected = true;
        _disconnecting = false;
        IsConnectionConfirmed = false;
        Status = "接続準備中…";
        try
        {
            var settings = await _store.LoadAsync();
            var allowed = transport is MattermostWebSocketTransport ? settings.MattermostAllowedUserIds : settings.SlackAllowedUserIds;
            if (string.IsNullOrWhiteSpace(allowed))
            {
                await transport.DisposeAsync();
                IsConnected = false;
                Status = "接続設定で許可するユーザーIDを登録し、保存してください。空欄では依頼を受け付けません。";
                return;
            }
            Volatile.Write(ref _accessSettings, settings);
            await _history.ListAsync();
            await _audit.ListAsync();
            await _sessions.ListAsync();
            if (_closing) { await transport.DisposeAsync(); IsConnected = false; return; }
            if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot) || !Directory.Exists(settings.WorkspaceRoot))
                throw new InvalidOperationException("作業フォルダーを設定し、保存してください。");
            // Reconnection invalidates pending work from the previous connection.
            Requests.Clear();
            Selected = null;
            _transport = transport;
            var connection = new CancellationTokenSource();
            _connection = connection;
            if (transport is IChatConnectionEvents events)
                events.Connected += () => App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_transport, transport) && !connection.IsCancellationRequested && !_closing)
                    {
                        IsConnectionConfirmed = true;
                        Status = "接続しました。許可ユーザーの依頼を待っています。切断中の依頼は再投稿してください。";
                        _connectionNotice = NotifyConnectionAsync(transport, settings,
                            "Chat Agent Bridge: 接続しました。許可されたユーザーの依頼をPCで確認してから実行します。切断中に投稿した依頼は取り込まれないため、必要な依頼は再投稿してください。");
                    }
                });
            Status = "接続処理中／受信待ち。失敗時はここに表示します。";
            _receiveTask = ReceiveAsync(transport, settings, connection);
        }
        catch (Exception)
        {
            await transport.DisposeAsync();
            Status = "接続できません。保存済みの作業フォルダー・接続設定を確認してください。";
            IsConnectionConfirmed = false;
            IsConnected = false;
        }
    }

    private async Task ReceiveAsync(IChatTransport transport, BridgeSettings settings, CancellationTokenSource connection)
    {
        var cancellationToken = connection.Token;
        try
        {
            await transport.RunAsync(settings, async (request, token) =>
            {
                var accessSettings = Volatile.Read(ref _accessSettings);
                if (_disconnecting || accessSettings is null || !HasAccess(accessSettings, request, settings)) return;
                await AuditAsync("received", request, null, token);
                if (_approvalBroker.TryHandle(request)) return;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested && !_closing)
                    {
                        if (Requests.Count >= 100)
                        {
                            var completed = Requests.FirstOrDefault(item => item.State is not ("承認待ち" or "実行中"));
                            if (completed is null)
                            {
                                _droppedCount++;
                                OverflowNotice = $"上限により未受理: {_droppedCount}件。最後: {request.Provider} / {request.ChannelId} / {request.MessageId}。処理後にチャットから再送してください。";
                                completion.TrySetResult();
                                return;
                            }
                            Requests.Remove(completed);
                        }
                        var item = new InboxItem(request, transport, settings);
                        Requests.Add(item);
                        RequestReceived?.Invoke(item);
                        Status = $"依頼を受信しました。{request.Provider} の承認待ちを確認してください。";
                    }
                    completion.TrySetResult();
                })) completion.TrySetCanceled();
                await completion.Task;
            }, cancellationToken);
            Status = "接続が終了しました。再接続後、未実行の依頼を再投稿してください。";
        }
        catch (OperationCanceledException) { Status = "切断しました。切断中の依頼は受信しません。再接続後、未実行の依頼を再投稿してください。"; }
        catch (Exception)
        {
            // Provider error payloads can contain tokens or socket URLs.
            Status = "接続に失敗しました。認証情報・接続先・ネットワークを確認し、再接続後に未実行の依頼を再投稿してください。";
        }
        finally
        {
            IsConnectionConfirmed = false;
            ConnectionEnded?.Invoke();
            connection.Cancel();
            foreach (var item in Requests.Where(item => item.State == "承認待ち")) item.State = "接続終了・再受信が必要";
            ApproveCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
            SendResultCommand.NotifyCanExecuteChanged();
            // Keep reconnect disabled until old execution and transport cleanup finish.
            if (ApproveCommand.ExecutionTask is { IsCompleted: false } execution) await execution;
            if (SendResultCommand.ExecutionTask is { IsCompleted: false } reply) await reply;
            await _connectionNotice;
            await transport.DisposeAsync();
            if (ReferenceEquals(_transport, transport)) _transport = null;
            if (ReferenceEquals(_connection, connection)) _connection = null;
            connection.Dispose();
            IsConnectionConfirmed = false;
            IsConnected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        var connection = _connection;
        var receive = _receiveTask;
        _disconnecting = true;
        var wasConfirmed = IsConnectionConfirmed;
        IsConnectionConfirmed = false;
        _execution?.Cancel();
        if (wasConfirmed && _transport is { } transport && Volatile.Read(ref _accessSettings) is { } settings)
        {
            await _connectionNotice;
            await NotifyConnectionAsync(transport, settings,
                "Chat Agent Bridge: 切断します。未実行の依頼・切断中に投稿する依頼は実行されません。再接続の通知後に必要な依頼を再投稿してください。");
        }
        connection?.Cancel();
        if (receive is not null) await receive;
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var item = Selected;
        if (item is null || !CanApprove()) return;
        IsExecuting = true;
        _activeItem = item;
        var approvedByNotification = ReferenceEquals(_notificationApprovedItem, item);
        ExecutionRecord? record = null;
        try
        {
            var connection = _connection;
            if (connection is null || connection.IsCancellationRequested) return;
            _execution = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var token = _execution.Token;
            if (!await CheckAccessAsync(item, token)) return;
            var key = CreateSessionKey(item);
            var binding = await _sessions.FindAsync(key, token);
            var requestedModel = binding?.Model ?? item.Settings.AgentModel;
            var requestedEffort = binding?.ReasoningEffort ??
                (binding?.Model is null || string.Equals(binding.Model, item.Settings.AgentModel, StringComparison.Ordinal)
                    ? item.Settings.AgentReasoningEffort : null);
            item.SessionLabel = binding is null ? "新規会話（Codex・Claude Codeでは継続先を保存します）" : $"継続セッション: {binding.SessionId}";
            item.SessionLabel += $"\n実行モデル: {requestedModel ?? "CLIの既定値"} / 推論レベル: {requestedEffort ?? "CLIの既定値"}";
            item.SessionLabel += $"\n会話の所有者: {item.Request.UserId}";
            if (!approvedByNotification && (ConfirmAsync is null || !await ConfirmAsync(item))) return;
            if (item.State != "承認待ち" || token.IsCancellationRequested) return;
            if (!await CheckAccessAsync(item, token)) return;
            record = new ExecutionRecord
            {
                StartedAt = DateTimeOffset.UtcNow, Source = item.Request.Provider,
                ChannelId = item.Request.ChannelId, ThreadId = item.Request.ThreadId,
                UserId = item.Request.UserId, WorkspaceRoot = item.Workspace,
                SessionId = binding?.SessionId, Status = "running",
                AgentModel = requestedModel,
                AgentReasoningEffort = requestedEffort
            };
            await _history.RecordAsync(record, _execution.Token);
            item.ExecutionId = record.Id;
            await AuditAsync("approved", item.Request, record.Id, _execution.Token);
            await AuditAsync("started", item.Request, record.Id, _execution.Token);
            item.State = "実行中";
            Output = "エージェントを実行しています…";
            UsageSummary = "実行中。利用量は完了後に表示します。";
            var result = item.Settings.AgentMode == "app-server"
                ? await _serverRunner.RunAsync(key, item.Settings, item.Request.Text,
                    new AppServerRunOptions(ApprovalHandler: (approval, ct) => HandleApprovalAsync(item, approval, ct), InputHandler: ServerInputHandler), _execution.Token)
                : await _runner.RunAsync(key, item.Settings, item.Request.Text, _execution.Token);
            Output = (result.Claude?.FinalMessage ?? result.Codex?.FinalMessage ?? result.Output) + (string.IsNullOrEmpty(result.Error) ? "" : "\n" + result.Error);
            var needsLogin = !result.Success && item.Settings.AgentProvider == AgentProfile.Claude &&
                (Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                 Output.Contains("Please run /login", StringComparison.OrdinalIgnoreCase));
            if (needsLogin)
                Output = "Claude Codeが未ログインです。このWindowsユーザーでターミナルからClaude Codeを起動し、/login を完了してから再度依頼してください。Slackへの再ログインは不要です。\n\n" + Output;
            UsageSummary = UsageUi.UsageSummaryFormatter.Format(result.Usage, result.Metrics);
            if (result.HasOutputTruncated) Output += "\n（出力ログの表示上限を超えたため、一部を省略しています。）";
            // Only a bounded preview can be shared. Retaining full outputs for 100 inbox
            // items otherwise holds up to 100 million characters until eviction.
            var reply = ExtractReply(result);
            item.ResultText = reply.Length > 4001 ? reply[..4001] : reply;
            item.State = result.Success ? "完了" : $"失敗 ({result.ExitCode})";
            var updated = await _sessions.FindAsync(key);
            if (updated is not null) item.SessionLabel = $"継続セッション: {updated.SessionId}\n実行モデル: {requestedModel ?? "CLIの既定値"} / 推論レベル: {requestedEffort ?? "CLIの既定値"}";
            record = record with { FinishedAt = DateTimeOffset.UtcNow, Status = result.Success ? "completed" : "failed", ExitCode = result.ExitCode, SessionId = updated?.SessionId, Usage = result.Usage, Metrics = result.Metrics };
            await _history.RecordAsync(record);
            await AuditAsync(record.Status, item.Request, record.Id);
            try
            {
                await item.Transport.ReplyAsync(item.Request,
                    $"Chat Agent Bridge: {item.State}。結果はローカルのWinUI画面で確認してください。", _execution.Token);
                Status = needsLogin ? "Claude Codeが未ログインのため実行できませんでした。実行結果欄のログイン手順を確認してください。"
                    : result.Success ? "実行結果を確認できます。本文を共有する場合は「結果をスレッドに送信」を選択してください。"
                    : "実行に失敗しました。実行結果欄のエラーを確認してから再度依頼してください。";
            }
            catch (Exception)
            {
                Status = "実行は終了しましたが、チャットへの状態通知に失敗しました。ローカル結果は保持しています。";
                await AuditAsync("reply_failed", item.Request, record.Id);
            }
        }
        catch (OperationCanceledException)
        {
            item.State = "キャンセル"; Status = "実行をキャンセルしました。";
            UsageSummary = UsageUi.UsageSummaryFormatter.Format(null, null);
            if (record is not null) await FinishFailureAsync(record, "cancelled", item.Request);
            await NotifyRequestAsync(item, "Chat Agent Bridge: PCで実行をキャンセルしました。結果本文は送信していません。");
        }
        catch (Exception)
        {
            if (record?.Status == "running" || record is null) item.State = "実行／記録エラー";
            UsageSummary = UsageUi.UsageSummaryFormatter.Format(null, null);
            Status = "実行または履歴の記録に失敗しました。設定を変更した場合は、履歴画面で保存セッションを切り離してから再度依頼してください。ローカル結果も確認してください。";
            if (record is not null && record.Status == "running") await FinishFailureAsync(record, "failed", item.Request);
        }
        finally
        {
            if (record is not null) _history.EndTracking(record.Id);
            _execution?.Dispose();
            _execution = null;
            _activeItem = null;
            IsExecuting = false;
            ConnectMattermostCommand.NotifyCanExecuteChanged();
            ConnectSlackCommand.NotifyCanExecuteChanged();
            SendResultCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task RejectAsync()
    {
        var item = Selected;
        if (item is null || !CanApprove()) return;
        IsExecuting = true;
        try
        {
            await AuditAsync("rejected", item.Request, null);
            item.State = "拒否";
            Status = "依頼を拒否しました。実行していません。";
            await NotifyRequestAsync(item, "Chat Agent Bridge: PCで依頼を拒否しました。実行していません。");
        }
        catch (Exception) { Status = "拒否の記録に失敗しました。依頼は実行していません。"; }
        finally { IsExecuting = false; }
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    public async Task ApproveNotificationAsync(InboxItem item)
    {
        if (!Requests.Contains(item) || IsExecuting || !IsConnectionConfirmed || item.State != "承認待ち") return;
        Selected = item;
        if (!CanApprove()) return;
        _notificationApprovedItem = item;
        try { await ApproveCommand.ExecuteAsync(null); }
        finally { _notificationApprovedItem = null; }
    }

    public async Task RejectNotificationAsync(InboxItem item)
    {
        if (!Requests.Contains(item) || IsExecuting || !IsConnectionConfirmed || item.State != "承認待ち") return;
        Selected = item;
        if (CanApprove()) await RejectCommand.ExecuteAsync(null);
    }

    private bool CanSendResult() => !IsExecuting && IsConnectionConfirmed && Selected is { ResultText.Length: > 0 } item && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanSendResult))]
    private async Task SendResultAsync()
    {
        var item = Selected;
        if (item is null || !CanSendResult()) return;
        var text = item.ResultText.Length > 4000 ? item.ResultText[..4000] + "\n（長いため省略。全文はPC上で確認してください。）" : item.ResultText;
        IsExecuting = true;
        _activeItem = item;
        try
        {
            _execution = CancellationTokenSource.CreateLinkedTokenSource(_connection!.Token);
            var token = _execution.Token;
            if (!await CheckAccessAsync(item, token)) return;
            if (ConfirmReplyAsync is null || !await ConfirmReplyAsync(item, text) || token.IsCancellationRequested) return;
            if (!await CheckAccessAsync(item, token)) return;
            await item.Transport.ReplyAsync(item.Request, text, token);
            await AuditAsync("reply_sent", item.Request, item.ExecutionId);
            Status = "結果本文を元のスレッドへ送信しました。";
        }
        catch (OperationCanceledException) { Status = "結果の送信をキャンセルしました。"; }
        catch (Exception) { Status = "結果の送信または監査記録に失敗しました。再送前にスレッドを確認してください。"; }
        finally { _execution?.Dispose(); _execution = null; _activeItem = null; IsExecuting = false; }
    }

    private Task AuditAsync(string kind, ChatRequest request, Guid? executionId, CancellationToken token = default) =>
        _audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, executionId, request.Provider, request.ChannelId, request.ThreadId), token);

    private async Task<AppServerDecision> HandleApprovalAsync(InboxItem item, AppServerApproval approval, CancellationToken token)
    {
        if (!await CheckAccessAsync(item, token)) return AppServerDecision.Decline;
        var settings = Volatile.Read(ref _accessSettings);
        var decision = await _approvalBroker.RequestAsync(item.Transport, item.Request, approval, ServerApprovalHandler, token,
            publishToThread: settings?.ShareApprovalsInChat == true);
        if (decision == AppServerDecision.Accept && !await CheckAccessAsync(item, token)) return AppServerDecision.Decline;
        try { await AuditAsync(decision == AppServerDecision.Accept ? "approved" : "rejected", item.Request, item.ExecutionId, token); }
        catch (Exception) { return AppServerDecision.Decline; }
        return decision;
    }

    private static SessionKey CreateSessionKey(InboxItem item) => ChatSessionScope.Create(
        item.Request.Provider, item.Request.ChannelId, item.Request.ThreadId, item.Request.UserId,
        item.Request.Provider == "mattermost" ? item.Settings.MattermostServerUrl : null);

    private static bool HasAccess(BridgeSettings current, ChatRequest request, BridgeSettings received)
    {
        if (!ChatAccessPolicy.IsAllowed(current, request.Provider, request.UserId)) return false;
        if (request.Provider == "mattermost")
            return current.MattermostChannelId == request.ChannelId && received.MattermostChannelId == request.ChannelId &&
                current.MattermostToken == received.MattermostToken &&
                ChatSessionScope.Create(request.Provider, request.ChannelId, request.ThreadId, request.UserId, current.MattermostServerUrl).Origin ==
                ChatSessionScope.Create(request.Provider, request.ChannelId, request.ThreadId, request.UserId, received.MattermostServerUrl).Origin;
        return request.Provider == "slack" && current.SlackBotToken == received.SlackBotToken && current.SlackAppToken == received.SlackAppToken;
    }

    public async Task RefreshAccessAsync()
    {
        try
        {
            var settings = await _store.LoadAsync();
            Volatile.Write(ref _accessSettings, settings);
            foreach (var item in Requests.Where(item => !HasAccess(settings, item.Request, item.Settings)))
            {
                if (item.State == "承認待ち") item.State = "許可取り消し・実行不可";
                item.ResultText = "";
            }
            if (_activeItem is { } active && !HasAccess(settings, active.Request, active.Settings)) _execution?.Cancel();
        }
        catch (Exception)
        {
            Volatile.Write(ref _accessSettings, null);
            _execution?.Cancel();
            foreach (var item in Requests) { if (item.State == "承認待ち") item.State = "許可設定を確認できず実行不可"; item.ResultText = ""; }
            Status = "許可設定を読み込めません。依頼の実行・結果共有を停止しました。設定を修復して再接続してください。";
        }
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        SendResultCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> CheckAccessAsync(InboxItem item, CancellationToken token)
    {
        var settings = await _store.LoadAsync(token);
        Volatile.Write(ref _accessSettings, settings);
        if (HasAccess(settings, item.Request, item.Settings)) return true;
        App.DispatcherQueue.TryEnqueue(() =>
        {
            item.State = "許可取り消し・実行不可";
            item.ResultText = "";
            Status = "許可ユーザーまたは接続設定が変更されたため、実行・結果共有を停止しました。";
        });
        return false;
    }

    private async Task NotifyRequestAsync(InboxItem item, string message)
    {
        if (_closing || !IsConnectionConfirmed || !ReferenceEquals(item.Transport, _transport)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await item.Transport.ReplyAsync(item.Request, message, timeout.Token);
        }
        catch (Exception) { Status += " チャットへの状態通知に失敗しました。"; }
    }

    private async Task NotifyConnectionAsync(IChatTransport transport, BridgeSettings settings, string message)
    {
        if (transport is not IChatConnectionNotifications notifications) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await notifications.NotifyConnectionAsync(settings, message, timeout.Token);
        }
        catch (Exception) { if (!_closing) Status += " 接続状態のチャット通知に失敗しました。"; }
    }

    private async Task FinishFailureAsync(ExecutionRecord record, string status, ChatRequest request)
    {
        try
        {
            await _history.RecordAsync(record with { FinishedAt = DateTimeOffset.UtcNow, Status = status });
            await AuditAsync(status, request, record.Id);
        }
        catch (Exception) { Status += " 履歴・監査の保存にも失敗しました。"; }
    }

    private static string ExtractReply(AgentRunResult result)
    {
        if (result.Claude is { } claude) return claude.FinalMessage ?? "";
        var parsed = result.Codex ?? CodexOutputParser.Parse(result.Output);
        return parsed.FinalMessage ?? (parsed.HasEvents ? "" : result.Output);
    }

    [RelayCommand(CanExecute = nameof(IsExecuting))]
    private void Cancel() => _execution?.Cancel();

    public void Close()
    {
        _closing = true;
        _connection?.Cancel();
        _execution?.Cancel();
    }
}
