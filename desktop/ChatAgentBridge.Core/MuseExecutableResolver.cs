using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core;

/// <summary>Resolves Muse's official Windows launcher to the versioned native executable.</summary>
public static class MuseExecutableResolver
{
    private const int VersionFileLimit = 128;
    private static readonly Regex VersionPattern = new(@"\A[0-9]{1,8}\.[0-9]{1,8}\.[0-9]{1,8}-R[0-9]{1,12}(?:\.[0-9]{1,8})?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Returns the native executable for a bare Muse command or official muse.cmd launcher.</summary>
    public static string Resolve(string executable)
        => Resolve(executable, DefaultInstallRoot(), Environment.GetEnvironmentVariable("PATH"));

    internal static string Resolve(string executable, string? installRoot, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var trimmed = executable.Trim().Trim('"');
        var leaf = Path.GetFileName(trimmed);
        if (!leaf.Equals("muse", StringComparison.OrdinalIgnoreCase) && !leaf.Equals("muse.cmd", StringComparison.OrdinalIgnoreCase))
            return executable;

        string? root = null;
        var hasPath = Path.IsPathFullyQualified(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar);
        if (hasPath)
        {
            var fullLauncher = Path.GetFullPath(trimmed);
            if (!leaf.Equals("muse.cmd", StringComparison.OrdinalIgnoreCase)) return executable;
            if (!File.Exists(fullLauncher) || IsReparsePoint(fullLauncher)) throw InvalidLauncher(fullLauncher);
            root = Path.GetDirectoryName(fullLauncher);
        }
        else if (leaf.Equals("muse.cmd", StringComparison.OrdinalIgnoreCase))
        {
            var match = FindPathCommand(searchPath, launcherOnly: true);
            root = match?.Kind == PathCommandKind.Launcher ? Path.GetDirectoryName(match.Path) : null;
            if (root is null && installRoot is not null && Directory.Exists(installRoot) && File.Exists(Path.Combine(installRoot, "muse.cmd"))) root = installRoot;
            if (root is null) throw InvalidLauncher(trimmed);
        }
        else
        {
            var match = FindPathCommand(searchPath);
            if (match?.Kind == PathCommandKind.Native) return match.Path;
            if (match?.Kind == PathCommandKind.Launcher) root = Path.GetDirectoryName(match.Path);
            else if (installRoot is not null && Directory.Exists(installRoot)) root = installRoot;
            if (root is null) return executable;
        }

        return ResolveInstallRoot(root!);
    }

    private static string ResolveInstallRoot(string root)
    {
        string fullRoot;
        try { fullRoot = Path.GetFullPath(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw InvalidLauncher(root, ex); }
        if (!Directory.Exists(fullRoot) || HasReparsePointInPath(fullRoot)) throw InvalidLauncher(fullRoot);

        var versionPath = Path.Combine(fullRoot, ".muse-version");
        if (!File.Exists(versionPath) || IsReparsePoint(versionPath)) throw InvalidLauncher(fullRoot);
        var info = new FileInfo(versionPath);
        if (info.Length is 0 or > VersionFileLimit) throw InvalidLauncher(fullRoot);
        string version;
        try { version = File.ReadAllText(versionPath).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw InvalidLauncher(fullRoot, ex); }
        if (!VersionPattern.IsMatch(version)) throw InvalidLauncher(fullRoot);

        var binary = Path.GetFullPath(Path.Combine(fullRoot, $"muse-bin-{version}.exe"));
        if (!Path.GetDirectoryName(binary)!.Equals(fullRoot, PathComparison) || !File.Exists(binary) || IsReparsePoint(binary))
            throw InvalidLauncher(fullRoot);
        return binary;
    }

    private static PathCommand? FindPathCommand(string? searchPath, bool launcherOnly = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            try
            {
                directory = Path.GetFullPath(directory);
                var native = Path.Combine(directory, "muse.exe");
                if (!launcherOnly && File.Exists(native)) return new(PathCommandKind.Native, Path.GetFullPath(native));
                var launcher = Path.Combine(directory, "muse.cmd");
                if (File.Exists(launcher)) return new(PathCommandKind.Launcher, Path.GetFullPath(launcher));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                // Continue to the next PATH entry; only a matching launcher shadows later entries.
            }
        }
        return null;
    }

    internal static string ResolveDefaultInstall()
        => ResolveDefaultInstall(DefaultInstallRoot());

    internal static string ResolveDefaultInstall(string? root)
    {
        return root is not null && Directory.Exists(root) ? ResolveInstallRoot(root) : "muse";
    }

    private enum PathCommandKind { Native, Launcher }
    private sealed record PathCommand(PathCommandKind Kind, string Path);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return true; }
    }

    private static bool HasReparsePointInPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return true;
            var current = root;
            if (IsReparsePoint(current)) return true;
            foreach (var part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (IsReparsePoint(current)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        { return true; }
    }

    private static string? DefaultInstallRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "Programs", "muse");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static InvalidOperationException InvalidLauncher(string launcher, Exception? inner = null)
        => new(ChatAgentBridge.Core.Localization.CoreText.Get("MuseLauncherInvalid", launcher), inner);
}
