using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly Dictionary<InboxItem, IAsyncDisposable> _requestActions = [];
    private readonly HashSet<InboxItem> _publishingRequestActions = [];

    private void NotifyPendingRequest(InboxItem item)
    {
        if (_notifiedRequests.Add(item)) RequestReceived?.Invoke(item);
        _ = PublishRequestActionsAsync(item);
    }

    private async Task PublishRequestActionsAsync(InboxItem item)
    {
        if (item.Transport is not IChatActionTransport actions || _requestActions.ContainsKey(item) ||
            !_publishingRequestActions.Add(item)) return;
        IAsyncDisposable? pendingLease = null;
        try
        {
            var token = _connection?.Token ?? CancellationToken.None;
            var settings = await _store.LoadAsync(token);
            if (!CanUseRequestActions(item, settings)) return;
            pendingLease = await actions.PostActionsAsync(item.Request, DesktopText.Get(item.Transport is MattermostWebSocketTransport
                    ? "MattermostInboxApprovalWaiting" : "DiscordInboxApprovalWaiting"),
                ChatActionKind.RequestApproval, (decision, ct) => HandleRequestActionAsync(item, decision, ct), token);
            if (CanUseRequestActions(item, await _store.LoadAsync(token)))
            {
                _requestActions[item] = pendingLease;
                pendingLease = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closing) ProgressNotice = DesktopText.Get("DiscordInboxApprovalPostFailed"); }
        finally
        {
            _publishingRequestActions.Remove(item);
            if (pendingLease is not null)
                try { await pendingLease.DisposeAsync(); } catch (Exception) { }
        }
    }

    private bool CanUseRequestActions(InboxItem item, ChatAgentBridge.Core.BridgeSettings current) =>
        !_closing && !_disconnecting && IsConnectionConfirmed && current.ShareApprovalsInChat &&
        Requests.Contains(item) && item.State == "承認待ち" && ReferenceEquals(item.Transport, _transport) &&
        HasAccess(current, item.Request, item.Settings);

    private async Task<ChatActionResult> HandleRequestActionAsync(InboxItem item, ChatActionDecision decision,
        CancellationToken token)
    {
        var completion = new TaskCompletionSource<ChatActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!CanUseRequestActions(item, await _store.LoadAsync(token)))
                {
                    completion.TrySetResult(ChatActionResult.Unavailable);
                    return;
                }
                var result = decision == ChatActionDecision.Approve
                    ? await ApproveNotificationAsync(item) : await RejectNotificationAsync(item);
                completion.TrySetResult(result switch
                {
                    Notifications.PendingNotificationActionResult.Completed => ChatActionResult.Completed,
                    Notifications.PendingNotificationActionResult.Busy => ChatActionResult.Busy,
                    _ => ChatActionResult.Unavailable
                });
            }
            catch (Exception) { completion.TrySetResult(ChatActionResult.Unavailable); }
        })) return ChatActionResult.Unavailable;
        return await completion.Task;
    }

    private async Task CompleteRequestActionsAsync(InboxItem item)
    {
        if (!_requestActions.Remove(item, out var lease)) return;
        try { await lease.DisposeAsync(); }
        catch (Exception) { }
    }
}
