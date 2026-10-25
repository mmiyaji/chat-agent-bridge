using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private async Task<bool> TryHandleAgentCommandAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, CancellationToken token)
    {
        if (!AgentChatCommand.TryParse(request.Text, out var command)) return false;
        try
        {
            var current = await _store.LoadAsync(token);
            if (!CanHandleControl(transport, received, request, current)) return true;
            var profiles = await new AgentConnectionStore().ListAsync(token);
            if (command.IsValid && command.Provider is not null && command.IncludeSummary is not null)
            {
                var binding = await _sessions.FindAsync(CreateControlScope(request, received), token);
                if (binding is not null && profiles.Any(profile => profile.Provider == command.Provider))
                {
                    var summary = command.IncludeSummary == true ? await new ConversationSummaryStore().FindAsync(binding.Key, token) : null;
                    if (command.IncludeSummary == false || summary?.SessionId == binding.SessionId) return false;
                    await ReplyToControlAsync(transport, received, request, DesktopText.Get("AgentSummaryUnavailable"), token);
                    return true;
                }
                await ReplyToControlAsync(transport, received, request, DesktopText.Get("AgentSwitchFailed") + "\n" + DesktopText.Get("AgentSwitchRegisterFirst"), token);
                return true;
            }
            var registered = string.Join(", ", profiles.Select(profile => profile.Provider));
            await ReplyToControlAsync(transport, received, request,
                DesktopText.Get("AgentSwitchCommands", string.IsNullOrEmpty(registered) ? DesktopText.Get("AgentSwitchNoConnections") : registered), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Status = DesktopText.Get("AgentSwitchFailed"); }
        return true;
    }

    private async Task ChangeAgentAsync(InboxItem item, AgentChatCommand command,
        bool approvedByNotification, bool approvedAutomatically, CancellationToken token)
    {
        var changed = false;
        (string Workspace, string SessionLabel)? originalDisplay = null;
        (string Workspace, string SessionLabel)? previewDisplay = null;
        try
        {
            if (!command.IsValid || command.Provider is null || command.IncludeSummary is null)
                throw new InvalidOperationException();
            var key = CreateSessionKey(item);
            var binding = await _sessions.FindAsync(key, token) ?? throw new InvalidOperationException();
            var defaults = await _store.LoadAsync(token);
            var executionBinding = await _sessions.FindExecutionBindingAsync(key, token);
            var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(key, defaults, token), executionBinding);
            if (approvedAutomatically && await _sessions.RequiresApprovalAsync(key, effective, executionBinding, switchCommand: true, token: token))
            {
                NotifyPendingRequest(item);
                Status = DesktopText.Get("InboxApprovalRequiredAgain");
                return;
            }
            item.ExecutionPolicyFingerprint = ConversationExecutionPolicy.GetFingerprint(key, effective);
            var profile = await new AgentConnectionStore().FindAsync(command.Provider, token) ?? throw new InvalidOperationException();
            var summary = command.IncludeSummary == true ? await new ConversationSummaryStore().FindAsync(key, token) : null;
            if (command.IncludeSummary == true && (summary is null || summary.SessionId != binding.SessionId))
                throw new InvalidOperationException();
            var previewLabel = DesktopText.Get("AgentSwitchConfirm", command.Provider, profile.Executable, profile.Model ?? DesktopText.Get("CliDefault"),
                PermissionLabel(profile.PermissionMode), binding.WorkspaceRoot, command.IncludeSummary == true ? DesktopText.Get("AgentSummaryYes") : DesktopText.Get("AgentSummaryNo"));
            if (summary is not null) previewLabel += "\n\n" + summary.Text;
            originalDisplay = (item.Workspace, item.SessionLabel);
            previewDisplay = (binding.WorkspaceRoot, previewLabel);
            item.Workspace = previewDisplay.Value.Workspace;
            item.SessionLabel = previewDisplay.Value.SessionLabel;
            if (!approvedByNotification && !approvedAutomatically && (ConfirmAsync is null || !await ConfirmAsync(item, token))) return;
            if (item.State != "承認待ち" || !await CheckAccessAsync(item, token)) return;
            await new ConversationAgentService(_sessions, new AgentConnectionStore(), new ConversationSummaryStore())
                .SwitchAsync(binding, defaults, new(command.Provider, command.IncludeSummary.Value, profile),
                    ct => CheckAccessAsync(item, ct), token, requireSwitchConsent: approvedAutomatically);
            changed = true;
            item.ExecutionPolicyFingerprint = null;
            var invalidated = Requests.Where(other => !ReferenceEquals(other, item) && other.State == "承認待ち" && CreateSessionKey(other) == key).ToArray();
            foreach (var pending in invalidated) pending.State = "エージェント変更・再投稿が必要";
            await FinishProgressBatchAsync(invalidated, ChatProgressState.Cancelled);
            item.State = "エージェント変更済み";
            item.AllowsResultSharing = false;
            item.ResultText = "";
            item.FullOutput = DesktopText.Get("AgentSwitchSaved", command.Provider);
            item.Usage = DesktopText.Get("WorkspaceChangeNoUsage");
            await FinishProgressAsync(item, ChatProgressState.Completed);
            await AuditAsync("agent_changed", item.Request, null, token);
            if (await CheckAccessAsync(item, token)) await item.Transport.ReplyAsync(item.Request, item.FullOutput, token);
            Status = item.FullOutput;
        }
        catch (OperationCanceledException) when (changed) { Status = DesktopText.Get("AgentSwitchSavedNoticeFailed"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            if (changed) { Status = DesktopText.Get("AgentSwitchSavedNoticeFailed"); return; }
            item.State = "エージェント変更不可";
            item.FullOutput = DesktopText.Get("AgentSwitchFailed");
            await FinishProgressAsync(item, ChatProgressState.Failed);
            if (await CheckAccessAsync(item, token)) await item.Transport.ReplyAsync(item.Request, item.FullOutput, token);
            Status = item.FullOutput;
        }
        finally
        {
            // The target-agent details are a preview until the switch is saved.
            // Preserve any newer display updates made while confirmation was pending.
            if (!changed && originalDisplay is { } original && previewDisplay is { } preview)
            {
                if (item.Workspace == preview.Workspace) item.Workspace = original.Workspace;
                if (item.SessionLabel == preview.SessionLabel) item.SessionLabel = original.SessionLabel;
            }
        }
    }
}
