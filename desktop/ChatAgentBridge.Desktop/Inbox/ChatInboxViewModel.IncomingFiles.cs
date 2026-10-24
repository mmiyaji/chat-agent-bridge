using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem
{
    private IReadOnlyList<IncomingChatFile>? _resolvedIncomingFiles;

    internal void SetResolvedIncomingFiles(IReadOnlyList<IncomingChatFile> files)
    {
        _resolvedIncomingFiles = files.ToArray();
        OnPropertyChanged(nameof(IncomingFilesSummary));
        OnPropertyChanged(nameof(PromptDisplay));
    }

    public string PromptDisplay => Request.IncomingFiles.Count == 0 && Request.IncomingFilesError is null
        ? Prompt : Prompt + "\n\n" + IncomingFilesSummary;

    public string IncomingFilesSummary => Request.IncomingFilesError is not null
        ? DesktopText.Get("IncomingFilesInvalid")
        : DesktopText.Get("IncomingFilesSummary", Request.IncomingFiles.Count,
            string.Join(", ", (_resolvedIncomingFiles ?? Request.IncomingFiles).Take(5).Select(file =>
                string.IsNullOrWhiteSpace(file.Name) ? DesktopText.Get("IncomingFileNamePending")
                    : IncomingAttachmentStager.SanitizeDisplayName(file.Name))));
}

public sealed partial class ChatInboxViewModel
{
    private sealed class IncomingFilesException : Exception;

    private async Task<IncomingAttachmentLease?> PrepareIncomingFilesAsync(InboxItem item, CancellationToken token)
    {
        if (item.Request.IncomingFilesError is not null) throw new IncomingFilesException();
        if (item.Request.IncomingFiles.Count == 0) return null;
        if (item.Transport is not IChatIncomingFileTransport transport) throw new IncomingFilesException();
        IncomingAttachmentLease? lease = null;
        try
        {
            if (!await CheckAccessAsync(item, token)) throw new OperationCanceledException(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var files = await transport.ResolveIncomingFilesAsync(item.Request, timeout.Token);
            if (files.Count != item.Request.IncomingFiles.Count) throw new IncomingFilesException();
            item.SetResolvedIncomingFiles(files);
            lease = await IncomingAttachmentStager.PrepareAsync(item.Workspace,
                files.Select(file => new IncomingAttachmentDescriptor(file.Id, file.Name, file.Size, file.ContentType)).ToArray(),
                async (descriptor, destination, maxBytes, ct) =>
                {
                    if (!await CheckAccessAsync(item, ct)) throw new OperationCanceledException(ct);
                    var file = files.Single(file => file.Id == descriptor.Id);
                    await transport.DownloadFileAsync(item.Request, file, destination, maxBytes, ct);
                }, timeout.Token, recoveryDataDirectory: _store.DirectoryPath);
            if (!await CheckAccessAsync(item, token)) throw new OperationCanceledException(token);
            return lease;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lease?.Dispose();
            throw;
        }
        catch (Exception)
        {
            lease?.Dispose();
            throw new IncomingFilesException();
        }
    }
}
