using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

public sealed record CliProbeResult(string Executable, string Version, bool ProviderRecognized);
internal sealed record CliProbeLaunch(string Provider, string Workspace, string Executable, IReadOnlyList<string> PrefixArguments, string? NodeEntryPoint, string? NodePackageName);

/// <summary>Safely checks the configured CLI executable without invoking a model or a user prompt.</summary>
public sealed class CliProbeService
{
    private const int OutputLimit = 8192;
    private readonly TimeSpan _timeout;

    public CliProbeService(TimeSpan? timeout = null)
    {
        var requested = timeout ?? TimeSpan.FromSeconds(8);
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "CLI probe timeout must be positive.");
        _timeout = requested > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : requested;
    }

    public async Task<CliProbeResult> CheckAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var launch = ResolveLaunch(settings);
        var provider = launch.Provider;
        var workspace = launch.Workspace;
        var executable = launch.Executable;
        var nodePrefix = launch.PrefixArguments;
        var (versionOutput, versionError, versionExitCode) = await RunCommandAsync(executable, workspace,
            nodePrefix.Append("--version"), cancellationToken).ConfigureAwait(false);
        var version = Sanitize(versionOutput);
        if (versionExitCode != 0)
        {
            var error = IsMissingDotNetRuntime(versionError)
                ? CoreText.Get("CliRuntimeMissing")
                : CoreText.Get("CliVersionCommandFailed", versionExitCode);
            throw new InvalidOperationException(error);
        }
        if (version.Length == 0)
            throw new InvalidOperationException(CoreText.Get("CliVersionUnavailable"));

        var recognized = launch.NodePackageName is not null
            ? ProviderMatchesPackage(provider, launch.NodePackageName)
            : RecognizesProvider(provider, version);
        if (!recognized && AgentProfile.IsBasicCliProvider(provider))
        {
            // Gemini and some Copilot versions print only a semver for --version. Help is
            // non-interactive and gives us a provider identity without starting a model turn.
            var (helpOutput, _, helpExitCode) = await RunCommandAsync(executable, workspace,
                nodePrefix.Append("--help"), cancellationToken).ConfigureAwait(false);
            recognized = helpExitCode == 0 && RecognizesProvider(provider, Sanitize(helpOutput));
        }
        if ((provider is AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot) && !recognized)
            throw new InvalidOperationException(CoreText.Get("CliProviderMismatch", provider));
        return new CliProbeResult(executable, version, recognized);
    }

    /// <summary>Resolves the same executable and entrypoint as CheckAsync without starting a process.</summary>
    internal static CliProbeLaunch ResolveLaunch(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = AgentProfile.ResolveProvider(settings);
        var configured = AgentProfile.ResolveEffectiveExecutable(settings);
        var workspace = ValidateWorkspace(settings);
        var probeExecutable = configured;
        if (!Path.IsPathFullyQualified(probeExecutable) &&
            (probeExecutable.Contains(Path.DirectorySeparatorChar) || probeExecutable.Contains(Path.AltDirectorySeparatorChar)))
            probeExecutable = Path.GetFullPath(probeExecutable, workspace);
        var nodePackage = NodeCliPackageResolver.Resolve(probeExecutable, provider);
        if (nodePackage is null)
        {
            var npmShim = AgentProfile.FindWindowsNpmShim(probeExecutable, provider);
            if (npmShim is not null) throw new InvalidOperationException(AgentProfile.WindowsNpmShimGuidance(npmShim, provider));
        }
        else probeExecutable = nodePackage.Executable;
        if (provider == AgentProfile.Codex) probeExecutable = CodexExecutableResolver.Resolve(probeExecutable);
        var extension = Path.GetExtension(probeExecutable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(CoreText.Get("NativeCliRequired"));
        var executable = nodePackage?.Executable ?? ResolveExecutable(probeExecutable, workspace);
        if (executable is null) throw new FileNotFoundException(CoreText.Get("CliExecutableNotFound", configured), configured);
        var prefix = nodePackage is not null ? new[] { nodePackage.EntryPoint } : AgentProfile.IsNodeExecutable(executable)
            ? AgentProfile.GetNodeProbePrefix(settings, provider) : Array.Empty<string>();
        return new CliProbeLaunch(provider, workspace, executable, prefix,
            nodePackage?.EntryPoint ?? (AgentProfile.IsNodeExecutable(executable) ? prefix.FirstOrDefault() : null), nodePackage?.PackageName);
    }

    /// <summary>Validates and canonicalizes the configured workspace without executing a CLI or changing trust settings.</summary>
    public static string ValidateWorkspace(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cwd = settings.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(cwd)) throw new InvalidOperationException(CoreText.Get("WorkspaceRequired"));
        if (!IsFullyQualifiedWorkspacePath(cwd)) throw new InvalidOperationException(CoreText.Get("WorkspacePathMustBeAbsolute", cwd));
        string workspace;
        try { workspace = Path.GetFullPath(cwd); }
        catch (Exception ex) when (IsInvalidWorkspacePathException(ex)) { throw new InvalidOperationException(CoreText.Get("WorkspacePathInvalid", cwd)); }
        if (!Directory.Exists(workspace)) throw new InvalidOperationException(CoreText.Get("WorkspaceFolderNotFound", workspace));
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, workspace)) throw DescribeUntrustedWorkspace(workspace);
        return workspace;
    }

    /// <summary>Builds a localized, read-only diagnosis for a workspace rejected by the trust policy.</summary>
    public static CliWorkspaceTrustException DescribeUntrustedWorkspace(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var fullWorkspace = Path.GetFullPath(workspace);
        var suggestedWorkspace = ResolveSuggestedWorkspacePath(fullWorkspace);
        var message = suggestedWorkspace is null
            ? CoreText.Get("WorkspaceNotTrusted", fullWorkspace)
            : CoreText.Get("WorkspaceNotTrustedLink", fullWorkspace, suggestedWorkspace);
        return new CliWorkspaceTrustException(fullWorkspace, suggestedWorkspace, message);
    }

    internal static bool RecognizesProvider(string provider, string output) => provider switch
    {
        AgentProfile.Codex => Regex.IsMatch(output, @"\bcodex(?:[- ]cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.Claude => Regex.IsMatch(output, @"\bclaude\s+code\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.Gemini => Regex.IsMatch(output, @"\bgemini(?:\s+cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.GitHubCopilot => Regex.IsMatch(output, @"\bgithub\s+copilot(?:\s+cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        _ => false
    };

    private static bool IsFullyQualifiedWorkspacePath(string path)
    {
        try { return Path.IsPathFullyQualified(path); }
        catch (Exception ex) when (IsInvalidWorkspacePathException(ex))
        {
            throw new InvalidOperationException(CoreText.Get("WorkspacePathInvalid", path));
        }
    }

    private static bool IsInvalidWorkspacePathException(Exception exception) => exception is
        ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private static string? ResolveSuggestedWorkspacePath(string workspace)
    {
        try
        {
            if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace)) return null;
            var sawLink = false;
            var candidate = ResolvePhysicalDirectoryPath(workspace, ref sawLink, 0);
            if (!sawLink || candidate is null || SamePath(candidate, workspace) || !Path.IsPathFullyQualified(candidate) || !Directory.Exists(candidate))
                return null;
            return (new DirectoryInfo(candidate).Attributes & FileAttributes.ReparsePoint) == 0 ? candidate : null;
        }
        catch (Exception ex) when (IsInvalidWorkspacePathException(ex) || ex is System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? ResolvePhysicalDirectoryPath(string path, ref bool sawLink, int linkDepth)
    {
        if (linkDepth > 16) return null;
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath) || !Directory.Exists(fullPath)) return null;
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return null;

        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.GetFullPath(Path.Combine(current, part));
            var directory = new DirectoryInfo(next);
            if (!directory.Exists) return null;
            if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = next;
                continue;
            }

            sawLink = true;
            if (directory.ResolveLinkTarget(returnFinalTarget: true) is not DirectoryInfo target) return null;
            var resolvedTarget = ResolvePhysicalDirectoryPath(target.FullName, ref sawLink, linkDepth + 1);
            if (resolvedTarget is null) return null;
            current = resolvedTarget;
        }

        return Path.GetFullPath(current);
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsMissingDotNetRuntime(string errorOutput) =>
        errorOutput.Contains("You must install or update .NET to run this application.", StringComparison.OrdinalIgnoreCase);

    private static bool ProviderMatchesPackage(string provider, string packageName) => provider switch
    {
        AgentProfile.Gemini => packageName == "@google/gemini-cli",
        AgentProfile.GitHubCopilot => packageName == "@github/copilot",
        _ => false
    };

    private async Task<(string Output, string Error, int ExitCode)> RunCommandAsync(string executable, string cwd,
        IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new InvalidOperationException(CoreText.Get("CliStartFailed"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(CoreText.Get("CliLaunchFailed"), ex);
        }

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdout = DrainBoundedAsync(process.StandardOutput, OutputLimit, linked.Token);
        var stderr = DrainBoundedAsync(process.StandardError, OutputLimit, linked.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await BoundedCleanupAsync(process, stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(CoreText.Get("CliVersionTimeout"));
        }
        return (await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), process.ExitCode);
    }

    private static string? ResolveExecutable(string name, string workspace)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? Path.GetFullPath(name) : null;
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            var local = Path.GetFullPath(name, workspace);
            return File.Exists(local) ? local : null;
        }
        var extensions = OperatingSystem.IsWindows()
            ? (Path.HasExtension(name) ? [""] : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Equals(".exe", StringComparison.OrdinalIgnoreCase)).ToArray())
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        return null;
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken = default)
    {
        var buffer = new char[1024];
        var kept = new StringBuilder(limit);
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = limit - kept.Length;
            if (remaining > 0) kept.Append(buffer, 0, Math.Min(read, remaining));
        }
        return kept.ToString();
    }

    private static string Sanitize(string output)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var clean = new string(line.Where(c => !char.IsControl(c)).Take(256).ToArray());
        return clean.Trim();
    }

    private static async Task BoundedCleanupAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var waits = new List<Task>();
        try { waits.Add(process.WaitForExitAsync(cleanup.Token)); } catch { }
        waits.Add(stdout);
        waits.Add(stderr);
        try { await Task.WhenAll(waits).WaitAsync(cleanup.Token).ConfigureAwait(false); } catch { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
