using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public sealed record FileAttachmentShareContext(SessionKey Key, string SessionId, BridgeSettings Settings,
    IReadOnlyList<FileAttachmentCandidate> Candidates, bool ScanComplete);

public enum FileAttachmentShareOutcome { None, Disabled, Private, PendingConsent, Cancelled, ScopeChanged, Sent }

/// <summary>Consent is independent of text replies and CLI approvals. No uploader is called before all gates pass.</summary>
public sealed class FileAttachmentSharingService(string dataDirectory)
{
    private readonly AttachmentFileCollector _collector = new();
    private readonly AttachmentConsentStore _consents = new(dataDirectory);

    public async Task<FileAttachmentShareOutcome> ShareAsync(FileAttachmentShareContext context, bool automatic,
        Func<IReadOnlyList<FileAttachmentCandidate>, bool, CancellationToken, Task<IReadOnlyList<FileAttachmentCandidate>?>>? confirm,
        Func<CancellationToken, Task<bool>> validateScope,
        Func<IReadOnlyList<PreparedFileAttachment>, CancellationToken, Task> upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileAttachmentPolicy.Validate(context.Settings);
        var mode = FileAttachmentPolicy.NormalizeMode(context.Settings.FileAttachmentApprovalMode);
        if (mode == "disabled") return FileAttachmentShareOutcome.Disabled;
        if (ExecutionPolicy.NormalizeChatResultMode(context.Settings.ChatResultMode) == "none") return FileAttachmentShareOutcome.Private;
        if (context.Candidates.Count == 0) return FileAttachmentShareOutcome.None;
        if (!await validateScope(cancellationToken).ConfigureAwait(false)) return FileAttachmentShareOutcome.ScopeChanged;
        var fingerprint = FileAttachmentPolicy.GetConsentFingerprint(context.Key, context.SessionId, context.Settings);
        var alreadyApproved = mode == "first" &&
            await _consents.IsApprovedAsync(context.Key, context.SessionId, fingerprint, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FileAttachmentCandidate> selected = context.Candidates;
        var explicitConsent = !automatic;
        if (automatic)
        {
            if (!context.ScanComplete || (mode != "auto" && !alreadyApproved) ||
                selected.Count > context.Settings.FileAttachmentMaxCount ||
                selected.Any(file => file.Length > context.Settings.FileAttachmentMaxSizeMb * 1024L * 1024L) ||
                selected.Sum(file => file.Length) > context.Settings.FileAttachmentMaxTotalSizeMb * 1024L * 1024L)
                return FileAttachmentShareOutcome.PendingConsent;
        }
        else
        {
            if (confirm is null) return FileAttachmentShareOutcome.PendingConsent;
            selected = await confirm(context.Candidates, mode == "first" && !alreadyApproved, cancellationToken).ConfigureAwait(false)
                ?? [];
            if (selected.Count == 0) return FileAttachmentShareOutcome.Cancelled;
            if (selected.Any(file => !context.Candidates.Contains(file)))
                throw new InvalidDataException("Only files discovered for this execution may be attached.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!await validateScope(cancellationToken).ConfigureAwait(false)) return FileAttachmentShareOutcome.ScopeChanged;
        var prepared = await _collector.PrepareAsync(selected, context.Settings, dataDirectory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await validateScope(cancellationToken).ConfigureAwait(false)) return FileAttachmentShareOutcome.ScopeChanged;
        // Start once; transport errors must never trigger an automatic retry of a possibly shared file.
        await upload(prepared, cancellationToken).ConfigureAwait(false);
        if (mode == "first" && explicitConsent && await validateScope(cancellationToken).ConfigureAwait(false))
            await _consents.SaveAsync(context.Key, context.SessionId, fingerprint, cancellationToken).ConfigureAwait(false);
        return FileAttachmentShareOutcome.Sent;
    }
}
