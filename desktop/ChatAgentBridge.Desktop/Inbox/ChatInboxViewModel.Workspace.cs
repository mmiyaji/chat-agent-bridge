using ChatAgentBridge.Core;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private async Task ChangeWorkspaceAsync(InboxItem item, string path, bool approvedByNotification, CancellationToken token)
    {
        var changed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                throw new InvalidOperationException("!cab folder C:\\Projects\\my-app のように、存在するフォルダーの絶対パスを1行目に指定してください。空白を含むパスは二重引用符で囲めます。");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!ExecutionPolicy.IsWorkspaceTrusted(item.Settings, path))
                throw new InvalidOperationException("変更先は信頼フォルダーの範囲外です。PCのエージェント設定で、許可する親フォルダーを確認・保存してから再接続してください。");
            var key = CreateSessionKey(item);
            var previousWorkspace = await _sessions.FindWorkspaceAsync(key, token);
            var currentWorkspace = previousWorkspace ?? item.Settings.WorkspaceRoot;
            item.Workspace = path;
            item.SessionLabel = $"作業フォルダーの変更（AIは実行しません）\n変更前: {currentWorkspace}\n変更後: {path}\n所有者: {item.Request.UserId}\nこのユーザー・スレッドだけに適用します。保存済みの会話・モデル・承認の上書きは解除し、次の依頼は新しいAIセッションで開始します。実行履歴は残ります。";
            // A folder change always requires a local decision, even for auto-approved conversations.
            if (!approvedByNotification && (ConfirmAsync is null || !await ConfirmAsync(item))) return;
            if (item.State != "承認待ち" || !await CheckAccessAsync(item, token)) return;
            if (previousWorkspace != await _sessions.FindWorkspaceAsync(key, token))
                throw new InvalidOperationException("確認中に作業フォルダーが変更されました。変更先を再確認して依頼し直してください。");
            await _sessions.ChangeWorkspaceAsync(key, path, item.Settings, token);
            changed = true;
            foreach (var pending in Requests.Where(other => !ReferenceEquals(other, item) && other.State == "承認待ち" && CreateSessionKey(other) == key))
                pending.State = "作業先変更・再投稿が必要";
            item.ExecutionPolicyFingerprint = null;
            item.State = "フォルダー変更済み";
            item.ResultText = "";
            item.AllowsResultSharing = false;
            Output = $"作業フォルダーを {path} に変更しました。次の依頼は新しいAI会話として開始します。";
            UsageSummary = "設定だけを変更しました。AIの呼び出し・トークン消費はありません。";
            await AuditAsync("workspace_changed", item.Request, null, token);
            if (await CheckAccessAsync(item, token))
                await item.Transport.ReplyAsync(item.Request, $"Chat Agent Bridge: この会話の作業フォルダーを {path} に変更しました。次の依頼は新しいAI会話として開始します。", token);
            Status = "このユーザー・スレッドの作業フォルダーを変更しました。";
        }
        catch (OperationCanceledException) when (changed)
        {
            item.State = "フォルダー変更済み";
            Status = "作業フォルダーの変更は保存済みです。切断によりチャットへの通知を中断しました。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (changed)
            {
                item.State = "フォルダー変更済み";
                Status = "作業フォルダーの変更は保存済みですが、記録またはチャットへの通知に失敗しました。";
                return;
            }
            item.State = "フォルダー変更不可";
            Output = ex.Message;
            Status = "作業フォルダーを変更できませんでした。実行結果欄で指定先と信頼フォルダーを確認してください。";
            if (await CheckAccessAsync(item, token))
                await item.Transport.ReplyAsync(item.Request, "Chat Agent Bridge: 作業フォルダーを変更できませんでした。絶対パス・フォルダーの存在・信頼範囲をPCで確認してください。AIは実行していません。", token);
        }
    }
}
