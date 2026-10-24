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
        if (request.CancellationTargetMessageId is not null && command.Kind != ChatControlCommandKind.Stop) return true;
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
                CreateSessionKey(item) == scope && HasAccess(current, item.Request, item.Settings) &&
                (request.CancellationTargetMessageId is null ||
                    string.Equals(item.Request.MessageId, request.CancellationTargetMessageId, StringComparison.Ordinal))).ToArray();
            string reply;
            if (kind == ChatControlCommandKind.Status)
            {
                reply = await BuildChatControlStatusAsync(own, token);
            }
            else
            {
                var pending = own.Where(item => item.State == "承認待ち" && !ReferenceEquals(item, _activeItem)).ToArray();
                foreach (var item in pending) item.State = "キャンセル";
                var active = own.Any(item => ReferenceEquals(item, _activeItem) && (item.State is "承認待ち" or "実行中")) &&
                    _execution is { IsCancellationRequested: false };
                if (active) RequestExecutionCancellation();
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

    private async Task<string> BuildChatControlStatusAsync(IReadOnlyList<InboxItem> own, CancellationToken token)
    {
        var connection = DesktopText.Get(IsConnectionConfirmed ? "ChatControlConnected" : "ChatControlReconnecting");
        var live = own.Where(item => item.State is "承認待ち" or "追加指示の受領待ち" ||
                ReferenceEquals(item, _activeItem) && item.State == "実行中")
            .OrderBy(item => ReferenceEquals(item, _activeItem) ? 0 : 1)
            .ThenBy(item => item.ReceivedAt)
            .ToArray();
        if (live.Length == 0)
            return DesktopText.Get("ChatControlStatusEmpty", connection);

        var rows = new List<string>(Math.Min(live.Length, ChatControlStatusFormatter.MaximumRows));
        for (var index = 0; index < Math.Min(live.Length, ChatControlStatusFormatter.MaximumRows); index++)
        {
            var item = live[index];
            var active = ReferenceEquals(item, _activeItem);
            var state = active && item.State == "実行中"
                ? ControlState(item)
                : item.State == "追加指示の受領待ち"
                    ? DesktopText.Get("ChatControlStatusQueued")
                    : _notifiedRequests.Contains(item)
                        ? DesktopText.Get("ChatControlStatusApproval")
                        : DesktopText.Get("ChatControlStatusQueued");
            var effective = await GetEffectiveStatusSettingsAsync(item, token);
            var agent = AgentProfile.ResolveProvider(effective) switch
            {
                AgentProfile.Codex => DesktopText.Get("ChatControlAgentCodex"),
                AgentProfile.Claude => DesktopText.Get("ChatControlAgentClaude"),
                AgentProfile.Gemini => DesktopText.Get("ChatControlAgentGemini"),
                AgentProfile.GitHubCopilot => DesktopText.Get("ChatControlAgentCopilot"),
                AgentProfile.Antigravity => DesktopText.Get("ChatControlAgentAntigravity"),
                _ => DesktopText.Get("ChatControlAgentCustom", Path.GetFileName(effective.AgentExecutable))
            };
            var model = CleanStatusField(effective.AgentModel ?? DesktopText.Get("CliDefault"), 100);
            agent = CleanStatusField(agent, 60);
            var workspace = CleanStatusField(effective.WorkspaceRoot, 140);
            rows.Add(DesktopText.Get("ChatControlStatusRow", state, agent, model, workspace));
        }
        return ChatControlStatusFormatter.Format(
            DesktopText.Get("ChatControlStatusHeader", live.Length, connection), rows, live.Length,
            remaining => DesktopText.Get("ChatControlStatusMore", remaining));
    }

    private static string CleanStatusField(string value, int maxLength)
    {
        var cleaned = new string(value.Select(character => char.IsControl(character) || character is '\u061C' or '\u200E' or '\u200F' or
            '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069'
                ? ' ' : character).ToArray()).Trim().Replace("@", "＠", StringComparison.Ordinal)
            .Replace("<", "‹", StringComparison.Ordinal).Replace(">", "›", StringComparison.Ordinal);
        return cleaned.Length <= maxLength ? cleaned : cleaned[..(maxLength - 1)] + "…";
    }

    private async Task<BridgeSettings> GetEffectiveStatusSettingsAsync(InboxItem item, CancellationToken token)
    {
        if (item.EffectiveSettings is { } effective) return effective;
        var key = CreateSessionKey(item);
        var workspaceSettings = await _sessions.ApplyWorkspaceAsync(key, item.Settings, token);
        var binding = await _sessions.FindExecutionBindingAsync(key, token);
        effective = ConversationExecutionPolicy.GetEffectiveSettings(workspaceSettings, binding);
        item.Workspace = effective.WorkspaceRoot;
        return effective;
    }

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
