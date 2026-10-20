using System.Security.Cryptography;

namespace ChatAgentBridge.Core;

/// <summary>Finds new, ordinary workspace files and copies approved bytes into immutable memory snapshots.</summary>
public sealed class AttachmentFileCollector
{
    private const int MaxEntries = 20_000;
    private const int MaxDepth = 64;
    private const int MaxCandidates = 100;
    private const long MaxHashBytes = 100L * 1024 * 1024;
    private const int BufferSize = 64 * 1024;

    public Task<FileAttachmentSnapshot> CaptureBeforeAsync(string workspace, string dataDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => CaptureBefore(workspace, dataDirectory, cancellationToken), cancellationToken);

    public Task<FileAttachmentScanResult> DiscoverAsync(FileAttachmentSnapshot before, BridgeSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => Discover(before, settings, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<PreparedFileAttachment>> PrepareAsync(IReadOnlyList<FileAttachmentCandidate> candidates, BridgeSettings settings, string dataDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => Prepare(candidates, settings, dataDirectory, cancellationToken), cancellationToken);

    private static FileAttachmentSnapshot CaptureBefore(string workspace, string dataDirectory, CancellationToken token)
    {
        var root = CanonicalDirectory(workspace);
        var data = CanonicalOptional(dataDirectory);
        var baseline = new HashSet<string>(PathComparer);
        var complete = true;
        var count = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            if (depth > MaxDepth) { complete = false; continue; }
            IEnumerator<string>? entries = null;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (entries.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > MaxEntries) { complete = false; stack.Clear(); break; }
                    var entry = entries.Current;
                    if (IsExcludedPath(entry, root, data)) continue;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception ex) when (IsExpectedFileError(ex)) { complete = false; continue; }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) stack.Push((entry, depth + 1));
                    else baseline.Add(Path.GetFullPath(entry));
                }
            }
            catch (Exception ex) when (IsExpectedFileError(ex)) { complete = false; }
            finally { entries?.Dispose(); }
        }
        return new FileAttachmentSnapshot(root, baseline, complete) { DataDirectory = data };
    }

    private static FileAttachmentScanResult Discover(FileAttachmentSnapshot before, BridgeSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(settings);
        var candidates = new List<FileAttachmentCandidate>();
        var complete = before.IsComplete;
        var errors = new List<string>();
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, before.Workspace))
            return new FileAttachmentScanResult(candidates, false, "Workspace is outside configured trusted roots.");
        string root;
        try { root = CanonicalDirectory(before.Workspace); }
        catch (Exception ex) when (IsExpectedFileError(ex)) { return new FileAttachmentScanResult(candidates, false, "Workspace is unavailable."); }
        var data = before.DataDirectory;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var entriesSeen = 0;
        long bytesHashed = 0;
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            if (depth > MaxDepth) { complete = false; AddError("Directory depth limit reached."); continue; }
            IEnumerator<string>? entries = null;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (entries.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    if (++entriesSeen > MaxEntries) { complete = false; stack.Clear(); AddError("Entry limit reached."); break; }
                    var entry = entries.Current;
                    if (IsExcludedPath(entry, root, data)) continue;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception ex) when (IsExpectedFileError(ex)) { complete = false; AddError("Some workspace entries could not be inspected."); continue; }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { stack.Push((entry, depth + 1)); continue; }
                    if (!IsSafeFileName(Path.GetFileName(entry)) || before.BaselinePaths.Contains(Path.GetFullPath(entry))) continue;
                    if (candidates.Count >= MaxCandidates) { complete = false; AddError("Candidate limit reached."); stack.Clear(); break; }
                    try
                    {
                        var info = new FileInfo(entry);
                        var length = info.Length;
                        if (length < 0 || bytesHashed > MaxHashBytes - length) { complete = false; AddError("Hash read budget reached."); continue; }
                        using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                        AttachmentHandlePolicy.EnsureSafe(stream, root, entry);
                        if (stream.Length != length) { complete = false; AddError("A file changed during discovery."); continue; }
                        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        AttachmentHandlePolicy.EnsureSafe(stream, root, entry);
                        bytesHashed += length;
                        var after = new FileInfo(entry);
                        if (after.Length != length || after.LastWriteTimeUtc != info.LastWriteTimeUtc) { complete = false; AddError("A file changed during discovery."); continue; }
                        candidates.Add(new FileAttachmentCandidate(Path.GetFullPath(entry), Path.GetRelativePath(root, entry), length, info.LastWriteTimeUtc, hash));
                    }
                    catch (Exception ex) when (IsExpectedFileError(ex)) { complete = false; AddError("Some new files could not be safely hashed."); }
                }
            }
            catch (Exception ex) when (IsExpectedFileError(ex)) { complete = false; AddError("Some workspace entries could not be read."); }
            finally { entries?.Dispose(); }
        }
        return new FileAttachmentScanResult(candidates, complete, errors.Count == 0 ? null : string.Join(" ", errors.Distinct()));

        void AddError(string error) { if (!errors.Contains(error, StringComparer.Ordinal)) errors.Add(error); }
    }

    private static IReadOnlyList<PreparedFileAttachment> Prepare(IReadOnlyList<FileAttachmentCandidate> candidates, BridgeSettings settings, string dataDirectory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);
        FileAttachmentPolicy.Validate(settings);
        var root = CanonicalDirectory(settings.WorkspaceRoot);
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, root)) throw new InvalidDataException("Workspace is outside configured trusted roots.");
        var data = CanonicalOptional(dataDirectory);
        var maxEach = checked((long)settings.FileAttachmentMaxSizeMb * 1024 * 1024);
        var maxTotal = checked((long)settings.FileAttachmentMaxTotalSizeMb * 1024 * 1024);
        if (candidates.Count == 0 || candidates.Count > settings.FileAttachmentMaxCount) throw new InvalidDataException("Attachment count is outside the configured limit.");
        var seen = new HashSet<string>(PathComparer);
        var result = new List<PreparedFileAttachment>(candidates.Count);
        long total = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate is null || !IsSha256(candidate.Sha256)) throw new InvalidDataException("Attachment candidate is invalid.");
            var full = Path.GetFullPath(candidate.FullPath);
            if (!seen.Add(full) || !IsWithin(full, root) || IsExcludedPath(full, root, data) || !IsSafeFileName(Path.GetFileName(full)))
                throw new InvalidDataException("Attachment path is duplicate, excluded, or outside the workspace.");
            var relative = Path.GetRelativePath(root, full);
            if (!string.Equals(relative, candidate.RelativePath, PathComparison) || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) || Path.IsPathRooted(relative))
                throw new InvalidDataException("Attachment relative path no longer matches its candidate.");
            if (!ExecutionPolicy.IsWorkspaceTrusted(settings, full)) throw new InvalidDataException("Attachment path is no longer trusted.");
            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Length != candidate.Length || info.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                throw new InvalidDataException("Attachment changed after discovery.");
            if (candidate.Length > maxEach || total > maxTotal - candidate.Length) throw new InvalidDataException("Attachment size is outside the configured limit.");
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            AttachmentHandlePolicy.EnsureSafe(stream, root, full);
            var opened = new FileInfo(full);
            if (stream.Length != candidate.Length || opened.LastWriteTimeUtc != candidate.LastWriteTimeUtc) throw new InvalidDataException("Attachment changed before it could be copied.");
            using var buffer = new MemoryStream(candidate.Length <= int.MaxValue ? checked((int)candidate.Length) : 0);
            var block = new byte[BufferSize];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(block, 0, block.Length);
                if (read == 0) break;
                if (buffer.Length > maxEach - read || buffer.Length > maxTotal - total - read) throw new InvalidDataException("Attachment grew beyond the configured size limit.");
                buffer.Write(block, 0, read);
            }
            AttachmentHandlePolicy.EnsureSafe(stream, root, full);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var finalInfo = new FileInfo(full);
            if (bytes.LongLength != candidate.Length || finalInfo.Length != candidate.Length || finalInfo.LastWriteTimeUtc != candidate.LastWriteTimeUtc || !string.Equals(hash, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Attachment changed while it was being copied.");
            total += bytes.LongLength;
            result.Add(new PreparedFileAttachment(Path.GetFileName(full), relative, bytes, hash));
        }
        return result;
    }

    private static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] == '.' || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(ch => char.IsControl(ch) || Path.GetInvalidFileNameChars().Contains(ch) || "<>:\"/\\|?*".Contains(ch))) return false;
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("secret", StringComparison.Ordinal) || lower.StartsWith("settings", StringComparison.Ordinal) ||
            lower.StartsWith("credentials", StringComparison.Ordinal) || lower.StartsWith("tokens", StringComparison.Ordinal) ||
            lower.StartsWith("auth", StringComparison.Ordinal) || lower.Contains("history", StringComparison.Ordinal) ||
            lower.Contains("session", StringComparison.Ordinal) || lower.Contains("appstate", StringComparison.Ordinal) ||
            lower.Contains("audit", StringComparison.Ordinal) || lower.Contains("staging", StringComparison.Ordinal) ||
            lower.StartsWith("id_rsa", StringComparison.Ordinal) || lower.StartsWith("id_ed25519", StringComparison.Ordinal) ||
            lower.StartsWith("id_ecdsa", StringComparison.Ordinal) || lower.StartsWith("id_dsa", StringComparison.Ordinal)) return false;
        var baseName = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(baseName, StringComparer.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(name);
        return !new[] { ".pem", ".key", ".pfx", ".p12", ".cer", ".kdbx", ".ppk", ".jks", ".keystore", ".p8", ".der", ".exe", ".dll", ".bat", ".cmd", ".ps1", ".lnk" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedPath(string path, string root, string? dataDirectory)
    {
        if (!IsWithin(path, root)) return true;
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".artifacts", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".vs", StringComparison.OrdinalIgnoreCase) || part.Equals("vendor", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".cache", StringComparison.OrdinalIgnoreCase) || part.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("secrets", StringComparison.OrdinalIgnoreCase) || part.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("settings", StringComparison.OrdinalIgnoreCase) || part.Equals("history", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("audit", StringComparison.OrdinalIgnoreCase) || part.Equals("staging", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".claude", StringComparison.OrdinalIgnoreCase) || part.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".ssh", StringComparison.OrdinalIgnoreCase) || part.Equals(".aws", StringComparison.OrdinalIgnoreCase))) return true;
        if (dataDirectory is not null && IsWithin(Path.GetFullPath(path), dataDirectory)) return true;
        return !IsSafeFileName(Path.GetFileName(path));
    }

    private static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsNetworkPath(path)) throw new ArgumentException("Workspace must be a local absolute path.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Workspace directory does not exist.");
        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Workspace cannot be a reparse point.");
        return full;
    }

    private static string? CanonicalOptional(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (IsExpectedFileError(ex)) { return null; }
    }

    private static bool IsNetworkPath(string path) => OperatingSystem.IsWindows() && (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));
    private static bool IsWithin(string path, string root) => string.Equals(path, root, PathComparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsExpectedFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
