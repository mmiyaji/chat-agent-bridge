using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private async Task ChangeWorkspaceAsync(InboxItem item, string path, bool approvedByNotification, CancellationToken token)
    {
        var changed = false;
        (string Workspace, string SessionLabel)? originalDisplay = null;
        (string Workspace, string SessionLabel)? previewDisplay = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                throw new InvalidOperationException(DesktopText.Get("WorkspacePathInstruction"));
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!ExecutionPolicy.IsWorkspaceTrusted(item.Settings, path))
                throw new InvalidOperationException(DesktopText.Get("WorkspaceOutsideTrustedRoots"));
            var key = CreateSessionKey(item);
            var previousWorkspace = await _sessions.FindWorkspaceAsync(key, token);
            var currentWorkspace = previousWorkspace ?? item.Settings.WorkspaceRoot;
            originalDisplay = (item.Workspace, item.SessionLabel);
            previewDisplay = (path, DesktopText.Get("WorkspaceChangePrompt", currentWorkspace, path, item.Request.UserId));
            item.Workspace = previewDisplay.Value.Workspace;
            item.SessionLabel = previewDisplay.Value.SessionLabel;
            // A folder change always requires a local decision, even for auto-approved conversations.
            if (!approvedByNotification && (ConfirmAsync is null || !await ConfirmAsync(item, token))) return;
            if (item.State != "承認待ち" || !await CheckAccessAsync(item, token)) return;
            if (previousWorkspace != await _sessions.FindWorkspaceAsync(key, token))
                throw new InvalidOperationException(DesktopText.Get("WorkspaceChangedDuringConfirmation"));
            await _sessions.ChangeWorkspaceAsync(key, path, item.Settings, token);
            changed = true;
            var invalidated = Requests.Where(other => !ReferenceEquals(other, item) && other.State == "承認待ち" && CreateSessionKey(other) == key).ToArray();
            foreach (var pending in invalidated)
                pending.State = "作業先変更・再投稿が必要";
            await FinishProgressBatchAsync(invalidated, ChatAgentBridge.Transports.ChatProgressState.Cancelled);
            item.ExecutionPolicyFingerprint = null;
            item.State = "フォルダー変更済み";
            await FinishProgressAsync(item, ChatAgentBridge.Transports.ChatProgressState.Completed);
            item.ResultText = "";
            item.AllowsResultSharing = false;
            item.FullOutput = DesktopText.Get("WorkspaceChangedOutput", path);
            item.Usage = DesktopText.Get("WorkspaceChangeNoUsage");
            await AuditAsync("workspace_changed", item.Request, null, token);
            if (await CheckAccessAsync(item, token))
                await item.Transport.ReplyAsync(item.Request, DesktopText.Get("WorkspaceChangedChatReply", path), token);
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
