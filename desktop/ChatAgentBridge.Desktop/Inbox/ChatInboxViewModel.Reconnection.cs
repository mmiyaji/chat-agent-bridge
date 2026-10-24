using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private void SubscribeConnectionEvents(IChatTransport transport, BridgeSettings settings,
        CancellationTokenSource connection)
    {
        var hasConnected = false;
        bool IsCurrent() => ReferenceEquals(_transport, transport) &&
            !connection.IsCancellationRequested && !_closing && !_disconnecting;

        if (transport is IChatReconnectionEvents reconnecting)
            reconnecting.ReconnectionStatusChanged += status => App.DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsCurrent()) return;
                if (status.State == ChatReconnectionState.Reconnecting)
                {
                    foreach (var item in _requestActions.Keys.ToArray()) _ = CompleteRequestActionsAsync(item);
                    IsConnectionConfirmed = false;
                    HasConnectionError = false;
                    var delay = Math.Max(1, (int)Math.Ceiling(status.NextRetryDelay?.TotalSeconds ?? 1));
                    Status = DesktopText.Get("InboxReconnecting", status.Attempt, delay);
                }
            });

        if (transport is IChatConnectionEvents events)
            events.Connected += () => App.DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsCurrent()) return;
                IsConnectionConfirmed = true;
                HasConnectionError = false;
                Status = DesktopText.Get(hasConnected ? "InboxReconnected" : "InboxConnectedWaiting");
                if (!hasConnected && settings.PostConnectionNotice)
                    _connectionNotice = NotifyConnectionAsync(transport, settings,
                        DesktopText.Get("InboxConnectedChatNotice"));
                hasConnected = true;
                _ = ProcessPendingPoliciesAsync();
            });
    }
}
