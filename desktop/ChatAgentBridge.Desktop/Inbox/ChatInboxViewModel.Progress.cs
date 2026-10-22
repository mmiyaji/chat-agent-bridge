using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private void InitializeProgress(InboxItem item)
    {
        if (item.Transport is not IChatProgressTransport) return;
        item.Progress = new ChatProgressReporter(item.Transport, item.Request,
            (state, elapsed) => FormatProgress(state, elapsed, item.Transport is MattermostWebSocketTransport),
            token => CanPublishProgressAsync(item, token),
            _ => App.DispatcherQueue.TryEnqueue(() => ProgressNotice = DesktopText.Get("ProgressNoticeFailed")));
        item.ReceiptNotification = item.Progress.ReceiveAsync();
    }

    private async Task<bool> CanPublishProgressAsync(InboxItem item, CancellationToken token)
    {
        if (!ReferenceEquals(item.Transport, _transport)) return false;
        var current = await _store.LoadAsync(token).ConfigureAwait(false);
        return HasAccess(current, item.Request, item.Settings);
    }

    private static string FormatProgress(ChatProgressState state, TimeSpan elapsed, bool reactionCancellation)
    {
        var key = state switch
        {
            ChatProgressState.Received => "ProgressReceived",
            ChatProgressState.Running => "ProgressRunning",
            ChatProgressState.Completed => "ProgressCompleted",
            ChatProgressState.Failed => "ProgressFailed",
            ChatProgressState.Cancelled => "ProgressCancelled",
            ChatProgressState.Rejected => "ProgressRejected",
            ChatProgressState.Disconnected => "ProgressDisconnected",
            ChatProgressState.FollowUpApplied => "ProgressFollowUpApplied",
            ChatProgressState.FollowUpPending => "ProgressFollowUpPending",
            ChatProgressState.FollowUpUncertain => "ProgressFollowUpUncertain",
            _ => "ProgressReceived"
        };
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        var duration = $"{seconds / 60:00}:{seconds % 60:00}";
        var text = DesktopText.Get(key, duration);
        return reactionCancellation && state is ChatProgressState.Received or ChatProgressState.Running
            ? text + Environment.NewLine + DesktopText.Get("ProgressReactionCancelHint")
            : text;
    }

    private static async Task StartProgressAsync(InboxItem item)
    {
        if (item.Progress is null) return;
        await item.ReceiptNotification;
        await item.Progress.StartAsync();
    }

    private static async Task FinishProgressAsync(InboxItem item, ChatProgressState state,
        CancellationToken cancellationToken = default)
    {
        if (item.Progress is null) return;
        await item.ReceiptNotification;
        await item.Progress.FinishAsync(state, cancellationToken);
        if (state is ChatProgressState.FollowUpApplied or ChatProgressState.FollowUpUncertain)
            await item.Progress.DisposeAsync();
    }

    private async Task FinishConnectionProgressAsync(IChatTransport transport)
    {
        var pending = Requests.Where(item => ReferenceEquals(item.Transport, transport) && item.Progress is not null).ToArray();
        await FinishProgressBatchAsync(pending, ChatProgressState.Disconnected, dispose: true);
    }

    private static async Task FinishProgressBatchAsync(IEnumerable<InboxItem> items, ChatProgressState state,
        bool dispose = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = CancellationToken.None
        }, async (item, _) =>
        {
            await FinishProgressAsync(item, state, timeout.Token);
            if (dispose && item.Progress is not null) await item.Progress.DisposeAsync();
        });
    }
}
