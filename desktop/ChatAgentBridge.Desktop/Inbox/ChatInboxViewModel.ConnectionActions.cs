using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly Func<string, Task<IChatTransport>> _connectionTransportFactory;
    private readonly CancellationTokenSource _connectionActionsLifetime = new();
    private string? _lastConnectionProvider;
    private Task? _disconnectTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectMattermostCommand), nameof(ConnectSlackCommand), nameof(ConnectDiscordCommand), nameof(DisconnectCommand), nameof(ReconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RejectCommand), nameof(SendResultCommand), nameof(SendAttachmentsCommand))]
    public partial bool IsConnectionActionInProgress { get; set; }

    public Func<CancellationToken, Task<bool>>? ConfirmReconnectAsync { get; set; }

    public string ReconnectLabel => DesktopText.Get("InboxReconnectLabel");
    public string ReconnectHint => IsExecuting ? DesktopText.Get("InboxReconnectBusyHint") :
        _lastConnectionProvider is null ? DesktopText.Get("InboxReconnectInitialHint") :
        DesktopText.Get("InboxReconnectHint", _lastConnectionProvider switch
        {
            "mattermost" => "Mattermost", "slack" => "Slack", "discord" => "Discord", _ => ""
        });

    private bool CanDisconnect() => !_closing && IsConnected && !IsConnectionActionInProgress;
    private bool CanReconnect() => !_closing && !IsExecuting && !IsConnectionActionInProgress &&
        _lastConnectionProvider is not null && (!IsConnected || _connection is not null);

    private Task DisconnectCoreAsync() => _disconnectTask is { IsCompleted: false } pending
        ? pending : _disconnectTask = DisconnectTransportAsync();

    partial void OnIsConnectionActionInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(ReconnectHint));
        if (!value && !_closing) _ = ProcessPendingPoliciesAsync();
    }

    private async Task ConnectProviderAsync(string provider)
    {
        if (!CanConnect()) return;
        IsConnectionActionInProgress = true;
        _lastConnectionProvider = provider;
        OnPropertyChanged(nameof(ReconnectHint));
        try { await CreateAndConnectAsync(provider); }
        catch (Exception)
        {
            if (!_closing)
            {
                HasConnectionError = true;
                Status = DesktopText.Get("InboxConnectionFailed");
            }
        }
        finally { IsConnectionActionInProgress = false; }
    }

    private async Task CreateAndConnectAsync(string provider)
    {
        var transport = await _connectionTransportFactory(provider);
        if (_closing) { await transport.DisposeAsync(); return; }
        await ConnectAsync(transport);
    }

    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        if (!CanReconnect()) return;
        var provider = _lastConnectionProvider!;
        var previousConnection = _connection;
        IsConnectionActionInProgress = true;
        try
        {
            // Old request actions are intentionally not carried into a new
            // transport. Include completed rows so unsent results/files cannot
            // silently lose their send action during a manual reconnect.
            if (IsConnected && Requests.Count > 0 && (ConfirmReconnectAsync is null ||
                !await ConfirmReconnectAsync(_connectionActionsLifetime.Token))) return;
            // Another path (settings revocation or shutdown) may have ended the
            // connection while the confirmation was open. Never replace newer state.
            if (_closing || IsExecuting || provider != _lastConnectionProvider ||
                !ReferenceEquals(previousConnection, _connection)) return;
            await DisconnectCoreAsync();
            if (_closing || IsExecuting) return;
            await CreateAndConnectAsync(provider);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception)
        {
            if (!_closing)
            {
                HasConnectionError = true;
                Status = DesktopText.Get("InboxReconnectFailed");
            }
        }
        finally { IsConnectionActionInProgress = false; }
    }
}
