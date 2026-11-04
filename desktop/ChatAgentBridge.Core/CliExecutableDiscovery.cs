namespace ChatAgentBridge.Core;

/// <summary>Discovers known CLI launchers on the process PATH without executing them.</summary>
public static class CliExecutableDiscovery
{
    public sealed record Detection(string Executable, string DiscoveredPath);

    public static Detection? Find(string provider, string? searchPath = null)
        => Find(provider, searchPath, null);

    internal static Detection? Find(string provider, string? searchPath, string? museInstallRoot)
    {
        var command = provider switch
        {
            AgentProfile.Codex => "codex",
            AgentProfile.Claude => "claude",
            AgentProfile.Gemini => "gemini",
            AgentProfile.GitHubCopilot => "copilot",
            AgentProfile.Antigravity => "agy",
            AgentProfile.Kiro => "kiro-cli",
            AgentProfile.Qwen => "qwen",
            AgentProfile.Kimi => "kimi",
            AgentProfile.Muse => "muse",
            _ => null
        };
        if (command is null) return null;
        foreach (var entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var selectedSupportedShim = false;
            try
            {
                var directory = entry.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                directory = Path.GetFullPath(directory);
                var native = Path.Combine(directory, command + ".exe");
                if (File.Exists(native))
                    return new Detection(command, native);
                if (!OperatingSystem.IsWindows()) continue;
                var shim = Path.Combine(directory, command + ".cmd");
                if (!File.Exists(shim)) continue;
                if (provider == AgentProfile.Muse)
                {
                    try
                    {
                        var resolved = MuseExecutableResolver.Resolve(shim);
                        return new Detection(command, resolved);
                    }
                    catch (InvalidOperationException) { return null; }
                }
                selectedSupportedShim = AgentProfile.UsesNpmShim(provider) || provider == AgentProfile.Codex;
                if (AgentProfile.UsesNpmShim(provider))
                {
                    if (NodeCliPackageResolver.Resolve(shim, provider) is not null)
                        return new Detection(command, shim);
                    return null;
                }
                else if (provider == AgentProfile.Codex)
                {
                    var resolved = CodexExecutableResolver.Resolve(shim);
                    // The Codex resolver may discover a desktop install elsewhere; it must
                    // not replace the launcher selected by this PATH directory.
                    var npmRoot = Path.Combine(directory, "node_modules") + Path.DirectorySeparatorChar;
                    if (Path.GetFullPath(resolved).StartsWith(npmRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(resolved))
                        return new Detection(command, resolved);
                    return null;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Returning a bare token must preserve runtime PATH selection: an invalid
                // matching shim shadows later installations even though it cannot be run.
                if (selectedSupportedShim) return null;
            }
        }
        if (provider == AgentProfile.Muse)
        {
            try
            {
                var fallback = museInstallRoot is null
                    ? MuseExecutableResolver.ResolveDefaultInstall()
                    : MuseExecutableResolver.ResolveDefaultInstall(museInstallRoot);
                if (!string.Equals(fallback, command, StringComparison.OrdinalIgnoreCase))
                    return new Detection(command, fallback);
            }
            catch (InvalidOperationException) { return null; }
        }
        return null;
    }
}
