using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly ConversationModelService _chatModels = new(new SessionStore());
    private readonly HashSet<SessionKey> _modelControls = [];

    private async Task HandleChatModelOnUiAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, string argument, CancellationToken token)
    {
        var scope = CreateControlScope(request, received);
        if (!_modelControls.Add(scope)) return;
        string? savedReply = null;
        try
        {
            var current = await _store.LoadAsync(token);
            if (!CanHandleControl(transport, received, request, current)) return;
            var binding = await _sessions.FindExecutionBindingAsync(scope, token);
            var workspaceSettings = await _sessions.ApplyWorkspaceAsync(scope, current, token);
            var effective = ConversationExecutionPolicy.GetEffectiveSettings(workspaceSettings, binding);
            if (AgentProfile.ResolveProvider(effective) is not (AgentProfile.Codex or AgentProfile.Claude))
            {
                await ReplyToControlAsync(transport, received, request, DesktopText.Get("ChatModelUnsupported"), token);
                return;
            }
            string reply;
            if (argument.Length == 0)
            {
                reply = DesktopText.Get("ChatModelCurrent", effective.AgentModel ?? DesktopText.Get("CliDefault"),
                    effective.AgentReasoningEffort ?? DesktopText.Get("CliDefault"));
            }
            else if (argument.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                if (IsExecuting)
                {
                    await ReplyToControlAsync(transport, received, request, DesktopText.Get("ChatModelBusy"), token);
                    return;
                }
                var models = await _chatModels.GetModelsAsync(effective, token);
                var text = new StringBuilder(DesktopText.Get("ChatModelList"));
                foreach (var model in models)
                {
                    var line = $"\n• {model.Id}";
                    if (text.Length + line.Length > 3200) { text.AppendLine().Append(DesktopText.Get("ChatModelMoreInPc")); break; }
                    text.Append(line);
                }
                reply = models.Count == 0 ? DesktopText.Get("ChatModelUnavailable") : text.ToString();
            }
            else
            {
                if (binding is null)
                {
                    await ReplyToControlAsync(transport, received, request, DesktopText.Get("ChatModelNoSession"), token);
                    return;
                }
                if (!await CanChangeModelAsync(token))
                {
                    await ReplyToControlAsync(transport, received, request, DesktopText.Get("ChatModelBusy"), token);
                    return;
                }
                var model = argument.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : argument;
                await _chatModels.ChangeAsync(scope, effective, model, CanChangeModelAsync, token);
                reply = DesktopText.Get("ChatModelChanged", model ?? DesktopText.Get("FollowAgentSettings"));
                savedReply = reply;
                await AuditAsync("model_changed", request, null, token);
            }
            await ReplyToControlAsync(transport, received, request, reply, token);

            async Task<bool> CanChangeModelAsync(CancellationToken cancellation = default)
            {
                var latest = await _store.LoadAsync(cancellation);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!App.DispatcherQueue.TryEnqueue(() => completion.TrySetResult(
                    !cancellation.IsCancellationRequested && CanHandleControl(transport, received, request, latest) &&
                    SameExecutionDefaults(current, latest) && !IsExecuting &&
                    !Requests.Any(item => CreateSessionKey(item) == scope && item.State == "承認待ち"))))
                    return false;
                return await completion.Task.WaitAsync(cancellation);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UnauthorizedAccessException)
        {
            await TryReplyFailureAsync("ChatModelBusy");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or KeyNotFoundException or TimeoutException)
        {
            Status = DesktopText.Get("ChatModelFailed");
            if (savedReply is not null)
            {
                // Persistence succeeded; a later audit error must not claim the model was unchanged.
                Status = DesktopText.Get("ChatModelSavedAuditFailed");
                try { await ReplyToControlAsync(transport, received, request, savedReply, token); }
                catch (Exception) { /* Keep the accurate local saved-state notice. */ }
            }
            else await TryReplyFailureAsync("ChatModelFailed");
        }
        catch (Exception) { Status = DesktopText.Get("ChatModelFailed"); }
        finally { _modelControls.Remove(scope); }

        async Task TryReplyFailureAsync(string resource)
        {
            try { await ReplyToControlAsync(transport, received, request, DesktopText.Get(resource), token); }
            catch (Exception) { Status = DesktopText.Get("ChatModelFailed"); }
        }
    }
}
