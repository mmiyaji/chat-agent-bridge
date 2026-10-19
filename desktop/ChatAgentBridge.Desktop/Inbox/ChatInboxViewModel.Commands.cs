using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private async Task<bool> TryHandleChatControlAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, CancellationToken token)
    {
        var command = ChatControlCommand.Parse(request.Text);
        if (command.Kind is ChatControlCommandKind.Answer or ChatControlCommandKind.ReservedInvalid) return true;
        if (command.Kind is not (ChatControlCommandKind.Help or ChatControlCommandKind.Status or ChatControlCommandKind.Stop or ChatControlCommandKind.Model)) return false;
        var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(() =>
            completion.TrySetResult(command.Kind == ChatControlCommandKind.Model
                ? HandleChatModelOnUiAsync(transport, received, request, command.ModelArgument ?? "", token)
                : HandleChatControlOnUiAsync(transport, received, request, command.Kind, token))))
            return true;
        await await completion.Task;
        return true;
    }

    private async Task HandleChatControlOnUiAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, ChatControlCommandKind kind, CancellationToken token)
    {
        try
        {
            var current = await _store.LoadAsync(token);
            Volatile.Write(ref _accessSettings, current);
            if (!CanHandleControl(transport, received, request, current)) return;
            if (kind == ChatControlCommandKind.Help)
            {
                await ReplyToControlAsync(transport, received, request, DesktopText.Get("ChatControlHelp"), token);
                return;
            }
            var scope = CreateControlScope(request, received);
            var own = Requests.Where(item => ReferenceEquals(item.Transport, transport) &&
                CreateSessionKey(item) == scope && HasAccess(current, item.Request, item.Settings)).ToArray();
            string reply;
            if (kind == ChatControlCommandKind.Status)
            {
                var active = own.FirstOrDefault(item => ReferenceEquals(item, _activeItem));
                var latest = active ?? own.LastOrDefault();
                reply = latest is null ? DesktopText.Get("ChatControlNoRequests") :
                    DesktopText.Get("ChatControlStatus", ControlState(latest),
                        own.Count(item => item.State == "承認待ち" && !ReferenceEquals(item, _activeItem)),
                        DesktopText.Get(IsConnectionConfirmed ? "ChatControlConnected" : "ChatControlReconnecting"));
            }
            else
            {
                var pending = own.Where(item => item.State == "承認待ち" && !ReferenceEquals(item, _activeItem)).ToArray();
                foreach (var item in pending) item.State = "キャンセル";
                var active = own.Any(item => ReferenceEquals(item, _activeItem) && (item.State is "承認待ち" or "実行中")) &&
                    _execution is { IsCancellationRequested: false };
                if (active) _execution!.Cancel();
                reply = active || pending.Length > 0
                    ? DesktopText.Get("ChatControlStop", active ? 1 : 0, pending.Length)
                    : DesktopText.Get("ChatControlStopNone");
                try { await ReplyToControlAsync(transport, received, request, reply, token); }
                finally { await FinishProgressBatchAsync(pending, ChatProgressState.Cancelled); }
                return;
            }
            await ReplyToControlAsync(transport, received, request, reply, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            Status = DesktopText.Get("ChatControlFailed");
        }
    }

    private bool CanHandleControl(IChatTransport transport, BridgeSettings received,
        ChatRequest request, BridgeSettings current) => !_closing && !_disconnecting &&
        ReferenceEquals(transport, _transport) && HasAccess(current, request, received);

    private static ChatAgentBridge.Core.Sessions.SessionKey CreateControlScope(ChatRequest request, BridgeSettings settings) =>
        ChatAgentBridge.Core.Sessions.ChatSessionScope.Create(request.Provider, request.ChannelId,
            request.ThreadId, request.UserId, request.Provider == "mattermost" ? settings.MattermostServerUrl : null);

    private string ControlState(InboxItem item) => _waitingForInput.ContainsKey(item)
        ? DesktopText.Get("ChatInputWaiting") : item.StateDisplay;

    private async Task ReplyToControlAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, string text, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var current = await _store.LoadAsync(timeout.Token);
        if (CanHandleControl(transport, received, request, current))
            await transport.ReplyAsync(request, text, timeout.Token);
    }
}
