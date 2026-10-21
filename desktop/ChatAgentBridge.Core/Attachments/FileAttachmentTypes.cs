namespace ChatAgentBridge.Core;

public sealed record FileAttachmentSnapshot(string Workspace, IReadOnlySet<string> BaselinePaths, bool IsComplete)
{
    public string? DataDirectory { get; init; }
}

public sealed record FileAttachmentCandidate(string FullPath, string RelativePath, long Length, DateTime LastWriteTimeUtc, string Sha256);

public sealed record FileAttachmentScanResult(IReadOnlyList<FileAttachmentCandidate> Candidates, bool IsComplete, string? Error = null, int SkippedCount = 0);

public sealed class PreparedFileAttachment
{
    private readonly byte[] _content;

    internal PreparedFileAttachment(string fileName, string relativePath, byte[] content, string sha256)
    {
        FileName = fileName;
        RelativePath = relativePath;
        _content = content;
        Length = content.LongLength;
        Sha256 = sha256;
    }

    public string FileName { get; }
    public string RelativePath { get; }
    public long Length { get; }
    public string Sha256 { get; }

    public Stream OpenRead() => new MemoryStream(_content, writable: false);
}
