namespace ChatAgentBridge.Core;

/// <summary>Canonicalizes execution and chat policy settings before they are persisted or used.</summary>
public static class ExecutionPolicy
{
    public static string NormalizePermissionMode(string? value) => Normalize(value, "inherit", ["inherit", "read-only", "workspace-write", "full-access", "ask"], "agent permission mode");
    public static string NormalizeChatApprovalMode(string? value) => Normalize(value, "first", ["always", "first", "auto"], "chat approval mode");
    public static string NormalizeChatResultMode(string? value) => Normalize(value, "manual", ["manual", "auto", "none"], "chat result mode");

    public static string NormalizeTrustedRoots(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var roots = value.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = new List<string>();
        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Trusted workspace roots must be absolute paths.", nameof(value));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!normalized.Contains(full, PathComparer)) normalized.Add(full);
        }
        return string.Join(Environment.NewLine, normalized);
    }

    public static bool IsWorkspaceTrusted(BridgeSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(path)) return false;
        string candidate;
        try { candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        var roots = new List<string>();
        try { roots.AddRange(NormalizeTrustedRoots(settings.TrustedWorkspaceRoots).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)); }
        catch (ArgumentException) { return false; }
        if (roots.Count == 0 && !string.IsNullOrWhiteSpace(settings.WorkspaceRoot)) roots.Add(settings.WorkspaceRoot);
        foreach (var rawRoot in roots)
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { continue; }
            if (IsWithin(candidate, root) && !HasReparseEscape(candidate, root)) return true;
        }
        return false;
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.Equals(path, root, PathComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool HasReparseEscape(string candidate, string root)
    {
        try
        {
            FileSystemInfo rootInfo = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            var relative = Path.GetRelativePath(root, candidate);
            var current = root;
            foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                var target = info.ResolveLinkTarget(true);
                if (target is null || !IsWithin(Path.GetFullPath(target.FullName), Path.GetFullPath(root))) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return true; }
    }

    private static string Normalize(string? value, string fallback, string[] allowed, string label)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : throw new ArgumentException($"Unsupported {label}.", nameof(value));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
