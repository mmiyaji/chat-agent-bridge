using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private async Task ChangeWorkspaceAsync(InboxItem item, string path, bool approvedByNotification, bool approvedAutomatically, CancellationToken token, string? worktreeName = null)
    {
        var changed = false;
        (string Workspace, string SessionLabel)? originalDisplay = null;
        (string Workspace, string SessionLabel)? previewDisplay = null;
        try
        {
            if (worktreeName is null)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                    throw new InvalidOperationException(DesktopText.Get("WorkspacePathInstruction"));
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            else if (!GitWorktreeService.IsValidName(worktreeName))
                throw new InvalidOperationException(DesktopText.Get("WorktreeNameInstruction"));
            var defaults = await _store.LoadAsync(token);
            if (worktreeName is null && !ExecutionPolicy.IsWorkspaceTrusted(defaults, path))
                throw new InvalidOperationException(DesktopText.Get("WorkspaceOutsideTrustedRoots"));
            var key = CreateSessionKey(item);
            using var operation = _sessions.TryBeginConversationOperation(key)
                ?? throw new InvalidOperationException(DesktopText.Get("AgentSwitchBusy"));
            var previousWorkspace = await _sessions.FindWorkspaceAsync(key, token);
            var binding = await _sessions.FindExecutionBindingAsync(key, token);
            var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(key, defaults, token), binding);
            GitWorktreePlan? worktree = null;
            if (worktreeName is not null)
            {
                // Read-only git lookups only; the worktree itself is created after approval.
                worktree = await GitWorktreeService.PlanAsync(effective.WorkspaceRoot, worktreeName, token);
                path = worktree.Path;
                // The worktree folder does not exist yet, so check the deepest existing folder on its path;
                // the created folder is checked again after approval.
                var existing = path;
                while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
                if (!ExecutionPolicy.IsWorkspaceTrusted(defaults, existing))
                    throw new InvalidOperationException(DesktopText.Get("WorkspaceOutsideTrustedRoots"));
            }
            if (approvedAutomatically && await _sessions.RequiresApprovalAsync(key, effective, binding, switchCommand: true, token: token))
            {
                NotifyPendingRequest(item);
                Status = DesktopText.Get("InboxApprovalRequiredAgain");
                return;
            }
            item.ExecutionPolicyFingerprint = ConversationExecutionPolicy.GetFingerprint(key, effective);
            var currentWorkspace = previousWorkspace ?? defaults.WorkspaceRoot;
            originalDisplay = (item.Workspace, item.SessionLabel);
            previewDisplay = (path, DesktopText.Get("WorkspaceChangePrompt", currentWorkspace, path, item.Request.UserId));
            item.Workspace = previewDisplay.Value.Workspace;
            item.SessionLabel = previewDisplay.Value.SessionLabel;
            // A folder change requires a local decision unless it was approved from a notification or the user
            // opted in to automatic switch approval (trusted roots are still enforced above).
            if (!approvedByNotification && !approvedAutomatically && (ConfirmAsync is null || !await ConfirmAsync(item, token))) return;
            if (item.State != "承認待ち" || !await CheckAccessAsync(item, token)) return;
            if (previousWorkspace != await _sessions.FindWorkspaceAsync(key, token))
                throw new InvalidOperationException(DesktopText.Get("WorkspaceChangedDuringConfirmation"));
            if (worktree is not null)
            {
                await GitWorktreeService.EnsureAsync(worktree, token);
                if (!ExecutionPolicy.IsWorkspaceTrusted(defaults, path))
                    throw new InvalidOperationException(DesktopText.Get("WorkspaceOutsideTrustedRoots"));
            }
            await _sessions.ChangeWorkspaceAsync(key, path, defaults, token, requireSwitchConsent: approvedAutomatically);
            changed = true;
            item.ExecutionPolicyFingerprint = null;
            var invalidated = Requests.Where(other => !ReferenceEquals(other, item) && other.State == "承認待ち" && CreateSessionKey(other) == key).ToArray();
            foreach (var pending in invalidated)
                pending.State = "作業先変更・再投稿が必要";
            await FinishProgressBatchAsync(invalidated, ChatAgentBridge.Transports.ChatProgressState.Cancelled);
            item.State = "フォルダー変更済み";
            await FinishProgressAsync(item, ChatAgentBridge.Transports.ChatProgressState.Completed);
            item.ResultText = "";
            item.AllowsResultSharing = false;
            item.FullOutput = worktree is null ? DesktopText.Get("WorkspaceChangedOutput", path)
                : DesktopText.Get("WorktreeChangedOutput", path, worktree.Branch);
            item.Usage = DesktopText.Get("WorkspaceChangeNoUsage");
            await AuditAsync("workspace_changed", item.Request, null, token);
            if (await CheckAccessAsync(item, token))
                await item.Transport.ReplyAsync(item.Request, worktree is null
                    ? DesktopText.Get("WorkspaceChangedChatReply", path)
                    : DesktopText.Get("WorktreeChangedChatReply", path, worktree.Branch), token);
            Status = DesktopText.Get("WorkspaceChangeSucceeded");
        }
        catch (OperationCanceledException) when (changed)
        {
            item.State = "フォルダー変更済み";
            Status = DesktopText.Get("WorkspaceSavedNotificationInterrupted");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (changed)
            {
                item.State = "フォルダー変更済み";
                Status = DesktopText.Get("WorkspaceSavedButAuditOrNotificationFailed");
                return;
            }
            item.State = "フォルダー変更不可";
            await FinishProgressAsync(item, ChatAgentBridge.Transports.ChatProgressState.Failed);
            item.FullOutput = ex.Message;
            Status = DesktopText.Get("WorkspaceChangeFailed");
            if (await CheckAccessAsync(item, token))
                await item.Transport.ReplyAsync(item.Request, DesktopText.Get("WorkspaceChangeFailedChatReply"), token);
        }
        finally
        {
            // A rejected or failed change must not leave its proposed folder visible.
            // Preserve any newer display updates made while confirmation was pending.
            if (!changed && originalDisplay is { } original && previewDisplay is { } preview)
            {
                if (item.Workspace == preview.Workspace) item.Workspace = original.Workspace;
                if (item.SessionLabel == preview.SessionLabel) item.SessionLabel = original.SessionLabel;
            }
        }
    }
}
