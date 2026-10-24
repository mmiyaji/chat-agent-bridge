namespace ChatAgentBridge.Transports;

/// <summary>Event metadata is only a bounded preflight hint, never authority to download a file.</summary>
internal static class IncomingFileCandidates
{
    internal static IReadOnlyList<IncomingChatFile> FromEvent(ChatRequest request)
    {
        if (request.IncomingFilesError is not null || request.IncomingFiles.Count > TransportHttp.MaxIncomingFileCount)
            throw new InvalidOperationException("Incoming attachment metadata could not be processed safely.");
        var files = request.IncomingFiles.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Id) || file.Id.Length > 128 || !ids.Add(file.Id) ||
                string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 1024 || file.Size < 0)
                throw new InvalidOperationException("Incoming attachment metadata is incomplete or duplicated.");
        }
        return files;
    }
}
