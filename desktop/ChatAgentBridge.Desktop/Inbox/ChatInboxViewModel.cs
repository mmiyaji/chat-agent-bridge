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
    private bool _closing;
    private int _droppedCount;

    public ObservableCollection<InboxItem> Requests { get; } = [];
    [ObservableProperty] public partial string Status { get; set; } = "切断中。接続設定を保存してから接続してください。";
    [ObservableProperty] public partial string Output { get; set; } = "受信した依頼は自動実行されません。選択して内容を確認し、承認してください。";
    [ObservableProperty] public partial string OverflowNotice { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectMattermostCommand)), NotifyCanExecuteChangedFor(nameof(ConnectSlackCommand)), NotifyCanExecuteChangedFor(nameof(DisconnectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand))]
    public partial bool IsConnected { get; set; }
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
    private bool CanApprove() => !IsExecuting && Selected?.State == "承認待ち";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectMattermostAsync() => ConnectAsync(new MattermostWebSocketTransport());

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectSlackAsync() => ConnectAsync(new SlackSocketModeTransport());

    private async Task ConnectAsync(IChatTransport transport)
    {
        if (IsConnected || _closing) { await transport.DisposeAsync(); return; }
        IsConnected = true;
        Status = "接続準備中…";
        try
        {
            var settings = await _store.LoadAsync();
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
            _connection = new CancellationTokenSource();
            if (transport is IChatConnectionEvents events)
                events.Connected += () => App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_transport, transport)) Status = "接続しました。チャットからの依頼を待っています。";
                });
            Status = "接続処理中／受信待ち。失敗時はここに表示します。";
            _receiveTask = ReceiveAsync(transport, settings, _connection);
        }
        catch (Exception)
        {
            await transport.DisposeAsync();
            Status = "接続できません。保存済みの作業フォルダー・接続設定を確認してください。";
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
                        Requests.Add(new InboxItem(request, transport, settings));
                        Status = $"依頼を受信しました。{request.Provider} の承認待ちを確認してください。";
                    }
                    completion.TrySetResult();
                })) completion.TrySetCanceled();
                await completion.Task;
            }, cancellationToken);
            Status = "接続が終了しました。再接続してください。";
        }
        catch (OperationCanceledException) { Status = "切断しました。"; }
        catch (Exception)
        {
            // Provider error payloads can contain tokens or socket URLs.
            Status = "接続に失敗しました。認証情報、接続先、ネットワークを確認し、再接続してください。";
        }
        finally
        {
            connection.Cancel();
            foreach (var item in Requests.Where(item => item.State == "承認待ち")) item.State = "接続終了・再受信が必要";
            ApproveCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
            SendResultCommand.NotifyCanExecuteChanged();
            // Keep reconnect disabled until old execution and transport cleanup finish.
            if (ApproveCommand.ExecutionTask is { IsCompleted: false } execution) await execution;
            if (SendResultCommand.ExecutionTask is { IsCompleted: false } reply) await reply;
            await transport.DisposeAsync();
            if (ReferenceEquals(_transport, transport)) _transport = null;
            if (ReferenceEquals(_connection, connection)) _connection = null;
            connection.Dispose();
            IsConnected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        var connection = _connection;
        var receive = _receiveTask;
        connection?.Cancel();
        _execution?.Cancel();
        if (receive is not null) await receive;
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var item = Selected;
        if (item is null || !CanApprove()) return;
        IsExecuting = true;
        ExecutionRecord? record = null;
        try
        {
            var connection = _connection;
            if (connection is null || connection.IsCancellationRequested) return;
            _execution = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var token = _execution.Token;
            var key = new SessionKey(item.Request.Provider, item.Request.ChannelId, item.Request.ThreadId);
            var binding = await _sessions.FindAsync(key, token);
            item.SessionLabel = binding is null ? "新規セッション（Codex標準exec / App Serverで継続可能）" : $"継続セッション: {binding.SessionId}";
            item.SessionLabel += $"\n実行モデル: {binding?.Model ?? item.Settings.AgentModel ?? "CLIの既定値"}";
            if (ConfirmAsync is null || !await ConfirmAsync(item)) return;
            if (item.State != "承認待ち" || token.IsCancellationRequested) return;
            record = new ExecutionRecord
            {
                StartedAt = DateTimeOffset.UtcNow, Source = item.Request.Provider,
                ChannelId = item.Request.ChannelId, ThreadId = item.Request.ThreadId,
                UserId = item.Request.UserId, WorkspaceRoot = item.Workspace,
                SessionId = binding?.SessionId, Status = "running",
                AgentModel = binding?.Model ?? item.Settings.AgentModel
            };
            await _history.RecordAsync(record, _execution.Token);
            item.ExecutionId = record.Id;
            await AuditAsync("approved", item.Request, record.Id, _execution.Token);
            await AuditAsync("started", item.Request, record.Id, _execution.Token);
            item.State = "実行中";
            Output = "エージェントを実行しています…";
            var result = item.Settings.AgentMode == "app-server"
                ? await _serverRunner.RunAsync(key, item.Settings, item.Request.Text,
                    new AppServerRunOptions(ApprovalHandler: (approval, ct) => HandleApprovalAsync(item, approval, ct), InputHandler: ServerInputHandler), _execution.Token)
                : await _runner.RunAsync(key, item.Settings, item.Request.Text, _execution.Token);
            Output = (result.Codex?.FinalMessage ?? result.Output) + (string.IsNullOrEmpty(result.Error) ? "" : "\n" + result.Error);
            if (result.HasOutputTruncated) Output += "\n（出力ログの表示上限を超えたため、一部を省略しています。）";
            // Only a bounded preview can be shared. Retaining full outputs for 100 inbox
            // items otherwise holds up to 100 million characters until eviction.
            var reply = ExtractReply(result);
            item.ResultText = reply.Length > 4001 ? reply[..4001] : reply;
            item.State = result.Success ? "完了" : $"失敗 ({result.ExitCode})";
            var updated = await _sessions.FindAsync(key);
            if (updated is not null) item.SessionLabel = $"継続セッション: {updated.SessionId}";
            record = record with { FinishedAt = DateTimeOffset.UtcNow, Status = result.Success ? "completed" : "failed", ExitCode = result.ExitCode, SessionId = updated?.SessionId };
            await _history.RecordAsync(record);
            await AuditAsync(record.Status, item.Request, record.Id);
            try
            {
                await item.Transport.ReplyAsync(item.Request,
                    $"Chat Agent Bridge: {item.State}。結果はローカルのWinUI画面で確認してください。", _execution.Token);
                Status = "実行結果を確認できます。本文を共有する場合は「結果をスレッドに送信」を選択してください。";
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
            if (record is not null) await FinishFailureAsync(record, "cancelled", item.Request);
        }
        catch (Exception)
        {
            if (record?.Status == "running" || record is null) item.State = "実行／記録エラー";
            Status = "実行または履歴の記録に失敗しました。設定を変更した場合は、履歴画面で保存セッションを切り離してから再度依頼してください。ローカル結果も確認してください。";
            if (record is not null && record.Status == "running") await FinishFailureAsync(record, "failed", item.Request);
        }
        finally
        {
            if (record is not null) _history.EndTracking(record.Id);
            _execution?.Dispose();
            _execution = null;
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
        try { await AuditAsync("rejected", item.Request, null); item.State = "拒否"; }
        catch (Exception) { Status = "拒否の記録に失敗しました。依頼は実行していません。"; }
        finally { IsExecuting = false; }
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    private bool CanSendResult() => !IsExecuting && IsConnected && Selected is { ResultText.Length: > 0 } item && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanSendResult))]
    private async Task SendResultAsync()
    {
        var item = Selected;
        if (item is null || !CanSendResult()) return;
        var text = item.ResultText.Length > 4000 ? item.ResultText[..4000] + "\n（長いため省略。全文はPC上で確認してください。）" : item.ResultText;
        IsExecuting = true;
        try
        {
            _execution = CancellationTokenSource.CreateLinkedTokenSource(_connection!.Token);
            var token = _execution.Token;
            if (ConfirmReplyAsync is null || !await ConfirmReplyAsync(item, text) || token.IsCancellationRequested) return;
            await item.Transport.ReplyAsync(item.Request, text, token);
            await AuditAsync("reply_sent", item.Request, item.ExecutionId);
            Status = "結果本文を元のスレッドへ送信しました。";
        }
        catch (OperationCanceledException) { Status = "結果の送信をキャンセルしました。"; }
        catch (Exception) { Status = "結果の送信または監査記録に失敗しました。再送前にスレッドを確認してください。"; }
        finally { _execution?.Dispose(); _execution = null; IsExecuting = false; }
    }

    private Task AuditAsync(string kind, ChatRequest request, Guid? executionId, CancellationToken token = default) =>
        _audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, executionId, request.Provider, request.ChannelId, request.ThreadId), token);

    private async Task<AppServerDecision> HandleApprovalAsync(InboxItem item, AppServerApproval approval, CancellationToken token)
    {
        var decision = await _approvalBroker.RequestAsync(item.Transport, item.Request, approval, ServerApprovalHandler, token);
        try { await AuditAsync(decision == AppServerDecision.Accept ? "approved" : "rejected", item.Request, item.ExecutionId, token); }
        catch (Exception) { return AppServerDecision.Decline; }
        return decision;
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
