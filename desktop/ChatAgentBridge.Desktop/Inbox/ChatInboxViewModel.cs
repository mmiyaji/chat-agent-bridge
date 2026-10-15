using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
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
}

public sealed partial class ChatInboxViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AgentRunner _runner = new();
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
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectMattermostCommand)), NotifyCanExecuteChangedFor(nameof(ConnectSlackCommand)), NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    public partial bool IsConnected { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsExecuting { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand))]
    public partial InboxItem? Selected { get; set; }
    public Func<InboxItem, Task<bool>>? ConfirmAsync { get; set; }

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
            if (_closing) { await transport.DisposeAsync(); IsConnected = false; return; }
            if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot) || !Directory.Exists(settings.WorkspaceRoot))
                throw new InvalidOperationException("作業フォルダーを設定し、保存してください。");
            // Reconnection invalidates pending work from the previous connection.
            Requests.Clear();
            Selected = null;
            _transport = transport;
            _connection = new CancellationTokenSource();
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
            await transport.RunAsync(settings, (request, token) =>
            {
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
                return completion.Task;
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
            // Keep reconnect disabled until old execution and transport cleanup finish.
            if (ApproveCommand.ExecutionTask is { IsCompleted: false } execution) await execution;
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
        try
        {
            if (ConfirmAsync is null || !await ConfirmAsync(item)) return;
            if (item.State != "承認待ち" || _connection is null || _connection.IsCancellationRequested) return;
            _execution = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
            item.State = "実行中";
            var result = await _runner.RunAsync(item.Settings, item.Request.Text, _execution.Token);
            Output = result.Output + (string.IsNullOrEmpty(result.Error) ? "" : "\n" + result.Error);
            item.State = result.Success ? "完了" : $"失敗 ({result.ExitCode})";
            // Never send arbitrary process output (which may contain credentials) automatically.
            await item.Transport.ReplyAsync(item.Request,
                $"Chat Agent Bridge: {item.State}。結果はローカルのWinUI画面で確認してください。", _execution.Token);
            Status = "実行結果と終了状態をローカル画面で確認してください。";
        }
        catch (OperationCanceledException) { item.State = "キャンセル"; Status = "実行をキャンセルしました。"; }
        catch (Exception) { item.State = "実行／返信エラー"; Status = "処理に失敗しました。ローカルの結果と接続設定を確認してください。"; }
        finally
        {
            _execution?.Dispose();
            _execution = null;
            IsExecuting = false;
            ConnectMattermostCommand.NotifyCanExecuteChanged();
            ConnectSlackCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Reject()
    {
        if (Selected is null) return;
        Selected.State = "拒否";
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
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
