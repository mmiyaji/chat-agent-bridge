using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    // A CLI without native session tracking cannot carry first-session consent to a later process run.
    private static string UntrackedAttachmentSession(InboxItem item) =>
        "cab-turn-" + (item.ExecutionId ?? throw new InvalidOperationException("Attachment execution identity is missing."));
    private readonly AttachmentFileCollector _attachmentCollector = new();
    private bool _attachmentTransferActive;
    public string SelectedAttachmentStatus => Selected?.AttachmentStatus ?? "";
    public Func<InboxItem, IReadOnlyList<FileAttachmentCandidate>, bool, CancellationToken,
        Task<IReadOnlyList<FileAttachmentCandidate>?>>? ConfirmAttachmentsAsync { get; set; }

    private async Task<FileAttachmentSnapshot?> CaptureAttachmentBaselineAsync(InboxItem item,
        BridgeSettings settings, CancellationToken token)
    {
        try
        {
            var current = await _store.LoadAsync(token);
            var effective = CopyAttachmentPolicy(settings, current);
            item.AttachmentSettingsAtStart = effective;
            if (FileAttachmentPolicy.NormalizeMode(effective.FileAttachmentApprovalMode) == "disabled" ||
                effective.ChatResultMode == "none" || item.Transport is not IChatFileTransport) return null;
            return await _attachmentCollector.CaptureBeforeAsync(effective.WorkspaceRoot, _store.DirectoryPath, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { item.AttachmentStatus = DesktopText.Get("AttachmentsScanIncomplete"); return null; }
    }

    private async Task DiscoverCompletedAttachmentsAsync(InboxItem item, FileAttachmentSnapshot? before,
        string sessionId, CancellationToken token)
    {
        if (item.AttachmentSettingsAtStart is not { } settings) return;
        if (before is null)
        {
            if (AsksForAttachment(item.Request.Text))
                await ReplyAttachmentNoticeAsync(item, settings, item.Transport is IChatFileTransport
                    ? (FileAttachmentPolicy.NormalizeMode(settings.FileAttachmentApprovalMode) == "disabled"
                        ? "AttachmentsChatDisabled" : "AttachmentsChatIncomplete")
                    : "AttachmentsChatNotSupported", null, token);
            return;
        }
        try
        {
            var discovered = await _attachmentCollector.DiscoverAsync(before, settings, token);
            item.AttachmentCandidates = discovered.Candidates;
            item.AttachmentSessionId = sessionId;
            item.AttachmentScanComplete = discovered.IsComplete;
            item.AttachmentStatus = (discovered.Candidates.Count == 0
                ? DesktopText.Get(discovered.IsComplete ? "AttachmentsNone" : "AttachmentsScanIncomplete")
                : DesktopText.Get("AttachmentsFound", discovered.Candidates.Count) +
                    (discovered.IsComplete ? "" : " " + DesktopText.Get("AttachmentsScanIncomplete"))) +
                (discovered.ExcludedFiles.Count == 0 ? "" : " " + DesktopText.Get("AttachmentsExcluded",
                    discovered.ExcludedFiles.Count, string.Join(", ", discovered.ExcludedFiles.Take(3)) +
                    (discovered.ExcludedFiles.Count > 3 ? " …" : "")));
            SendAttachmentsCommand.NotifyCanExecuteChanged();
            if (discovered.Candidates.Count == 0)
            {
                if (discovered.ExcludedFiles.Count > 0)
                    await ReplyAttachmentNoticeAsync(item, settings, "AttachmentsChatExcluded",
                        string.Join(", ", discovered.ExcludedFiles.Take(3)) + (discovered.ExcludedFiles.Count > 3 ? " …" : ""), token);
                else if (!discovered.IsComplete)
                    await ReplyAttachmentNoticeAsync(item, settings, "AttachmentsChatIncomplete", null, token);
                else if (AsksForAttachment(item.Request.Text))
                    await ReplyAttachmentNoticeAsync(item, settings, "AttachmentsChatNone", null, token);
                return;
            }
            // The default 'always' mode leaves a visible pending action. Text auto-reply never counts as file consent.
            await ShareAttachmentsAsync(item, automatic: true, token);
        }
        catch (OperationCanceledException) { item.AttachmentStatus = DesktopText.Get("AttachmentsCancelled"); }
        catch (Exception) { item.AttachmentStatus = DesktopText.Get("AttachmentsFailed"); }
    }

    private bool CanSendAttachments() => !IsExecuting && !IsConnectionActionInProgress && IsConnectionConfirmed && !_closing &&
        Selected is { AttachmentCandidates.Count: > 0, AllowsResultSharing: true } item &&
        item.Transport is IChatFileTransport && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanSendAttachments))]
    private async Task SendAttachmentsAsync()
    {
        var item = Selected;
        if (item is null || !CanSendAttachments()) return;
        using var operation = _sessions.TryBeginConversationOperation(CreateSessionKey(item));
        if (operation is null) { item.AttachmentStatus = DesktopText.Get("AttachmentsPolicyChanged"); return; }
        IsExecuting = true;
        _activeItem = item;
        _execution = CancellationTokenSource.CreateLinkedTokenSource(_connection?.Token ?? CancellationToken.None);
        try { await ShareAttachmentsAsync(item, automatic: false, _execution.Token); }
        finally
        {
            _execution.Dispose(); _execution = null; _activeItem = null; IsExecuting = false;
            SendAttachmentsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ShareAttachmentsAsync(InboxItem item, bool automatic, CancellationToken token)
    {
        if (item.Transport is not IChatFileTransport uploader || item.AttachmentSessionId is null) return;
        _attachmentTransferActive = true;
        try
        {
            if (!await CheckAccessAsync(item, token)) return;
            var key = CreateSessionKey(item);
            var current = await _store.LoadAsync(token);
            var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(key, current, token),
                await _sessions.FindExecutionBindingAsync(key, token));
            var settings = automatic ? item.AttachmentSettingsAtStart ?? effective : effective;
            item.EffectiveSettings = settings;
            var context = new FileAttachmentShareContext(key, item.AttachmentSessionId, settings,
                item.AttachmentCandidates, item.AttachmentScanComplete);
            var sentCount = 0;
            var outcome = await new FileAttachmentSharingService(_store.DirectoryPath).ShareAsync(context, automatic,
                (files, first, ct) => ConfirmAttachmentsOnUiAsync(item, files, first, ct),
                ct => ValidateAttachmentScopeAsync(item, context, ct),
                async (files, ct) =>
                {
                    // Persist intent before the first external mutation. Logs contain metadata, never bytes or paths.
                    await AuditAsync("attachment_approved", item.Request, item.ExecutionId, ct);
                    var ids = await uploader.UploadFilesAsync(item.Request, files, ct);
                    sentCount = ids.Count;
                    await AuditAsync("attachment_sent", item.Request, item.ExecutionId, CancellationToken.None);
                }, token);
            if (outcome == FileAttachmentShareOutcome.Sent)
            {
                item.AttachmentCandidates = [];
                item.AttachmentStatus = DesktopText.Get("AttachmentsSent", sentCount);
            }
            else if (outcome != FileAttachmentShareOutcome.PendingConsent || !automatic)
            {
                item.AttachmentStatus = DesktopText.Get(outcome switch
                {
                    FileAttachmentShareOutcome.Disabled => "AttachmentsDisabled",
                    FileAttachmentShareOutcome.Private => "AttachmentsResultPrivate",
                    FileAttachmentShareOutcome.ScopeChanged => "AttachmentsPolicyChanged",
                    FileAttachmentShareOutcome.Cancelled => "AttachmentsCancelled",
                    FileAttachmentShareOutcome.PendingConsent => "AttachmentsApprovalRequired",
                    _ => "AttachmentsNone"
                });
            }
            var chatReason = outcome switch
            {
                FileAttachmentShareOutcome.Disabled => "AttachmentsChatDisabled",
                FileAttachmentShareOutcome.ScopeChanged => "AttachmentsChatPolicyChanged",
                FileAttachmentShareOutcome.PendingConsent when automatic => "AttachmentsChatApprovalPending",
                _ => null
            };
            if (chatReason is not null) await ReplyAttachmentNoticeAsync(item, settings, chatReason, null, token);
        }
        catch (OperationCanceledException) { item.AttachmentStatus = DesktopText.Get("AttachmentsCancelled"); }
        catch (Exception ex)
        {
            var missingScope = ex.Message.Contains("files:write", StringComparison.OrdinalIgnoreCase);
            item.AttachmentStatus = DesktopText.Get(missingScope
                ? "AttachmentsSlackScopeMissing" : "AttachmentsFailed");
            try { await AuditAsync("attachment_failed", item.Request, item.ExecutionId); } catch (Exception) { }
            if (!token.IsCancellationRequested)
                await ReplyAttachmentNoticeAsync(item, item.EffectiveSettings, "AttachmentsChatFailed", null, token);
        }
        finally { _attachmentTransferActive = false; SendAttachmentsCommand.NotifyCanExecuteChanged(); }
    }

    private async Task<bool> ValidateAttachmentScopeAsync(InboxItem item, FileAttachmentShareContext context,
        CancellationToken token)
    {
        if (_closing || !IsConnectionConfirmed || !ReferenceEquals(item.Transport, _transport) ||
            !await CheckAccessAsync(item, token)) return false;
        var current = await _store.LoadAsync(token);
        var binding = await _sessions.FindExecutionBindingAsync(context.Key, token);
        if ((binding?.SessionId ?? UntrackedAttachmentSession(item)) != context.SessionId) return false;
        var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(context.Key, current, token), binding);
        return FileAttachmentPolicy.GetConsentFingerprint(context.Key, context.SessionId, effective) ==
            FileAttachmentPolicy.GetConsentFingerprint(context.Key, context.SessionId, context.Settings);
    }

    private Task<IReadOnlyList<FileAttachmentCandidate>?> ConfirmAttachmentsOnUiAsync(InboxItem item,
        IReadOnlyList<FileAttachmentCandidate> files, bool first, CancellationToken token)
    {
        if (ConfirmAttachmentsAsync is null) return Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(null);
        if (App.DispatcherQueue.HasThreadAccess) return ConfirmAttachmentsAsync(item, files, first, token);
        var completion = new TaskCompletionSource<IReadOnlyList<FileAttachmentCandidate>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(async () =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(await ConfirmAttachmentsAsync(item, files, first, token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception) { completion.TrySetResult(null); }
        })) completion.TrySetResult(null);
        return completion.Task;
    }

    private static bool AsksForAttachment(string? text) => FileAttachmentRequest.IsRequested(text);

    /// <summary>Tells the chat why no file was sent, unless results stay private. Never throws.</summary>
    private async Task ReplyAttachmentNoticeAsync(InboxItem item, BridgeSettings? settings, string reasonKey,
        string? detail, CancellationToken token)
    {
        if (settings is not null && ExecutionPolicy.NormalizeChatResultMode(settings.ChatResultMode) == "none") return;
        try
        {
            if (!await CheckAccessAsync(item, token)) return;
            var reason = detail is null ? DesktopText.Get(reasonKey) : DesktopText.Get(reasonKey, detail);
            // Waiting for approval is not a failure, so it is sent without the "could not send" lead.
            await item.Transport.ReplyAsync(item.Request, reasonKey == "AttachmentsChatApprovalPending"
                ? reason : DesktopText.Get("AttachmentsChatNotice", reason), token);
        }
        catch (Exception) { }
    }

    private static bool SameAttachmentPolicy(BridgeSettings a, BridgeSettings b) =>
        a.FileAttachmentApprovalMode == b.FileAttachmentApprovalMode && a.FileAttachmentMaxSizeMb == b.FileAttachmentMaxSizeMb &&
        a.FileAttachmentMaxCount == b.FileAttachmentMaxCount && a.FileAttachmentMaxTotalSizeMb == b.FileAttachmentMaxTotalSizeMb;

    private static BridgeSettings CopyAttachmentPolicy(BridgeSettings settings, BridgeSettings current) => settings with
    {
        FileAttachmentApprovalMode = current.FileAttachmentApprovalMode,
        FileAttachmentMaxSizeMb = current.FileAttachmentMaxSizeMb,
        FileAttachmentMaxCount = current.FileAttachmentMaxCount,
        FileAttachmentMaxTotalSizeMb = current.FileAttachmentMaxTotalSizeMb
    };
}
