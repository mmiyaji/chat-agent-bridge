using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private sealed record ActiveTurn(InboxItem Owner, IActiveAgentTurn Handle);
    private ActiveTurn? _activeTurn;
    private readonly SemaphoreSlim _followUpGate = new(1, 1);

    private async Task AcceptReceivedRequestAsync(ChatRequest request, IChatTransport transport,
        BridgeSettings settings, CancellationToken token)
    {
        if (token.IsCancellationRequested || _closing) return;
        if (Requests.Count >= 100)
        {
            var completed = Requests.FirstOrDefault(item => item.State is not ("承認待ち" or "実行中"));
            if (completed is null)
            {
                _droppedCount++;
                OverflowNotice = DesktopText.Get("InboxOverflow", _droppedCount, request.Provider, request.ChannelId, request.MessageId);
                return;
            }
            Requests.Remove(completed);
            if (completed.Progress is { } progress) _ = progress.DisposeAsync();
            _notifiedRequests.Remove(completed);
            _automaticallyAttempted.Remove(completed);
        }
        var incoming = new InboxItem(request, transport, settings);
        InitializeProgress(incoming);
        Requests.Add(incoming);
        if (await TryAddFollowUpAsync(incoming, token)) return;
        _ = ProcessPendingPoliciesAsync();
        Status = DesktopText.Get("InboxRequestReceived", request.Provider);
    }

    private void SetActiveTurn(InboxItem owner, IActiveAgentTurn? handle)
    {
        if (handle is not null)
        {
            if (ReferenceEquals(Volatile.Read(ref _activeItem), owner) && !_closing)
            {
                Volatile.Write(ref _activeTurn, new(owner, handle));
                App.DispatcherQueue.TryEnqueue(() => _ = ProcessQueuedFollowUpsAsync(owner));
            }
            return;
        }
        var current = Volatile.Read(ref _activeTurn);
        if (current is not null && ReferenceEquals(current.Owner, owner))
            Interlocked.CompareExchange(ref _activeTurn, null, current);
    }

    private async Task ProcessQueuedFollowUpsAsync(InboxItem owner)
    {
        if (ChatFollowUpPolicy.Normalize(owner.EffectiveSettings?.ChatFollowUpMode) != ChatFollowUpPolicy.Steer) return;
        try
        {
            foreach (var item in Requests.ToArray())
                if (!ReferenceEquals(item, owner) && item.State == "承認待ち")
                    await TryAddFollowUpAsync(item, _connection?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Status = DesktopText.Get("InboxAutomaticExecutionPaused"); }
    }

    private async Task CloseActiveTurnAsync(InboxItem owner)
    {
        SetActiveTurn(owner, null);
        // Include acknowledged additions in the handoff summary before completing this execution.
        await _followUpGate.WaitAsync();
        _followUpGate.Release();
    }

    private async Task<bool> TryAddFollowUpAsync(InboxItem incoming, CancellationToken token)
    {
        if (ChatFollowUpPolicy.IsConfigurationCommand(incoming.Prompt)) return false;
        await _followUpGate.WaitAsync(token);
        try
        {
            var context = Volatile.Read(ref _activeTurn);
            if (context is null || !ReferenceEquals(context.Owner, _activeItem) ||
                context.Owner.State != "実行中" || !IsExecuting ||
                ChatFollowUpPolicy.Normalize(context.Owner.EffectiveSettings?.ChatFollowUpMode) != ChatFollowUpPolicy.Steer ||
                !ReferenceEquals(incoming.Transport, context.Owner.Transport) || _closing || _disconnecting ||
                CreateSessionKey(incoming) != CreateSessionKey(context.Owner) || incoming.State != "承認待ち")
                return false;
            // Never let a later addition overtake an earlier queued message in the same conversation.
            if (Requests.TakeWhile(item => !ReferenceEquals(item, incoming)).Any(item =>
                    item.State == "承認待ち" && !ReferenceEquals(item, context.Owner) &&
                    CreateSessionKey(item) == CreateSessionKey(incoming))) return false;
            try
            {
                var current = await _store.LoadAsync(token);
                var effective = context.Owner.EffectiveSettings;
                if (effective is null || !ChatFollowUpPolicy.CanSteer(current, effective,
                        CreateSessionKey(incoming), CreateSessionKey(context.Owner))) return false;
                if (!await CheckAccessAsync(incoming, token) || !await CheckAccessAsync(context.Owner, token)) return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return false; } // The ordinary queue rechecks permissions before execution.
            if (!ReferenceEquals(context, Volatile.Read(ref _activeTurn)) || context.Owner.State != "実行中") return false;

            AgentTurnInputResult result;
            try { result = await context.Handle.AddInputAsync(incoming.Prompt, token); }
            catch (Exception) { result = AgentTurnInputResult.DeliveryUnknown; }
            if (result == AgentTurnInputResult.NotAccepted) return false;
            incoming.ExecutionId = context.Owner.ExecutionId;
            incoming.SessionLabel = context.Owner.SessionLabel;
            incoming.AllowsResultSharing = false;
            if (result == AgentTurnInputResult.Accepted)
            {
                incoming.State = "実行中へ追加";
                incoming.FullOutput = DesktopText.Get("FollowUpAppliedHelp");
                // Preserve only bounded context; usage remains on the original execution record.
                var combined = context.Owner.FollowUpSummary + "\n\n" + incoming.Prompt;
                context.Owner.FollowUpSummary = combined.Length <= 44000 ? combined : combined[^44000..];
                await AuditFollowUpAsync("follow_up_applied", incoming);
                await FinishProgressAsync(incoming, ChatProgressState.FollowUpApplied);
            }
            else
            {
                incoming.State = "追加指示の反映未確認";
                incoming.FullOutput = DesktopText.Get("FollowUpUncertainHelp");
                await AuditFollowUpAsync("follow_up_uncertain", incoming);
                await FinishProgressAsync(incoming, ChatProgressState.FollowUpUncertain);
            }
            Status = incoming.FullOutput;
            return true;
        }
        finally { _followUpGate.Release(); }
    }

    private async Task AuditFollowUpAsync(string kind, InboxItem item)
    {
        try { await AuditAsync(kind, item.Request, item.ExecutionId, CancellationToken.None); }
        catch (Exception) { NotificationStatus = DesktopText.Get("InboxAuditSaveFailed"); }
    }

    private static string SummaryPrompt(InboxItem item) => item.FollowUpSummary.Length == 0
        ? item.Prompt[..Math.Min(item.Prompt.Length, 90000)]
        : item.Prompt[..Math.Min(item.Prompt.Length, 44000)] + "\n\nAdditional user messages:\n" + item.FollowUpSummary;
}
