using System.Runtime.InteropServices;

namespace ChatAgentBridge.Core;

/// <summary>Finds a native Codex CLI executable so Windows never invokes an npm shell shim.</summary>
public static class CodexExecutableResolver
{
    public static string Resolve(string configuredExecutable)
    {
        if (string.IsNullOrWhiteSpace(configuredExecutable))
            throw new ArgumentException("An agent executable is required.", nameof(configuredExecutable));
        if (!OperatingSystem.IsWindows()) return configuredExecutable;

        var leaf = Path.GetFileName(configuredExecutable);
        var isCodex = string.Equals(Path.GetFileNameWithoutExtension(leaf), "codex", StringComparison.OrdinalIgnoreCase);
        var isShim = leaf.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        if (!isCodex) return RejectShellShim(configuredExecutable);
        if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configuredExecutable))
            return Path.GetFullPath(configuredExecutable);

        var native = FindInstalledDesktopCodex()
            ?? FindNpmNativeCodex(isShim ? Path.GetDirectoryName(Path.GetFullPath(configuredExecutable)) : null)
            ?? FindNpmNativeCodexFromPath();
        if (native is not null) return native;

        if (isShim)
            throw new InvalidOperationException($"'{configuredExecutable}' is a Windows shell shim. Install the native Codex CLI or desktop app; the bridge requires codex.exe and will not launch .cmd/.ps1 files.");
        throw new InvalidOperationException("Could not find the native Codex executable. Install the Codex desktop app or native CLI, or set AgentExecutable to the full path of codex.exe.");
    }

    private static string RejectShellShim(string executable)
    {
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{executable}' is a shell script. Set AgentExecutable to a native executable such as codex.exe; the bridge does not invoke shells.");
        return executable;
    }

    private static string? FindInstalledDesktopCodex()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var versions = Path.Combine(local, "OpenAI", "Codex", "bin");
        try
        {
            return Directory.EnumerateDirectories(versions)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? FindNpmNativeCodexFromPath()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = FindNpmNativeCodex(directory.Trim('"'));
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static string? FindNpmNativeCodex(string? globalBinDirectory)
    {
        if (string.IsNullOrWhiteSpace(globalBinDirectory)) return null;
        var packageRoot = Path.Combine(globalBinDirectory, "node_modules", "@openai", "codex", "node_modules", "@openai");
        var architectures = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new[] { "arm64", "x64" }
            : new[] { "x64" };
        foreach (var architecture in architectures)
        {
            var vendorRoot = Path.Combine(packageRoot, $"codex-win32-{architecture}", "vendor");
            try
            {
                foreach (var targetDirectory in Directory.EnumerateDirectories(vendorRoot))
                {
                    var package = Path.Combine(targetDirectory, "bin", "codex.exe");
                    if (File.Exists(package)) return Path.GetFullPath(package);
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
