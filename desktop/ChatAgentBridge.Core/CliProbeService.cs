using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

public sealed record CliProbeResult(string Executable, string Version, bool ProviderRecognized);

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
        var provider = AgentProfile.ResolveProvider(settings);
        var configured = AgentProfile.ResolveEffectiveExecutable(settings);
        var cwd = settings.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd) || !ExecutionPolicy.IsWorkspaceTrusted(settings, cwd))
            throw new InvalidOperationException(CoreText.Get("TrustedWorkspaceRequired"));

        // Use the same native Codex resolution as actual runs and model discovery.
        // In particular, an npm shim must resolve to codex.exe without starting a shell.
        var probeExecutable = configured;
        if (!Path.IsPathFullyQualified(probeExecutable) &&
            (probeExecutable.Contains(Path.DirectorySeparatorChar) || probeExecutable.Contains(Path.AltDirectorySeparatorChar)))
            probeExecutable = Path.GetFullPath(probeExecutable, Path.GetFullPath(cwd));
        var nodePackage = NodeCliPackageResolver.Resolve(probeExecutable, provider);
        if (nodePackage is null)
        {
            var npmShim = AgentProfile.FindWindowsNpmShim(probeExecutable, provider);
            if (npmShim is not null)
                throw new InvalidOperationException(AgentProfile.WindowsNpmShimGuidance(npmShim, provider));
        }
        else
        {
            probeExecutable = nodePackage.Executable;
        }
        if (provider == AgentProfile.Codex)
            probeExecutable = CodexExecutableResolver.Resolve(probeExecutable);
        var extension = Path.GetExtension(probeExecutable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(CoreText.Get("NativeCliRequired"));
        var executable = nodePackage?.Executable ?? ResolveExecutable(probeExecutable, Path.GetFullPath(cwd));
        if (executable is null) throw new FileNotFoundException(CoreText.Get("CliNotFound"), configured);

        var nodePrefix = nodePackage is not null
            ? new[] { nodePackage.EntryPoint }
            : AgentProfile.IsNodeExecutable(executable)
                ? AgentProfile.GetNodeProbePrefix(settings, provider) : Array.Empty<string>();
        var (versionOutput, versionExitCode) = await RunCommandAsync(executable, Path.GetFullPath(cwd),
            nodePrefix.Append("--version"), cancellationToken).ConfigureAwait(false);
        var version = Sanitize(versionOutput);
        if (versionExitCode != 0 || version.Length == 0)
            throw new InvalidOperationException(CoreText.Get("CliVersionUnavailable"));

        var recognized = nodePackage is not null
            ? ProviderMatchesPackage(provider, nodePackage.PackageName)
            : RecognizesProvider(provider, version);
        if (!recognized && AgentProfile.IsBasicCliProvider(provider))
        {
            // Gemini and some Copilot versions print only a semver for --version. Help is
            // non-interactive and gives us a provider identity without starting a model turn.
            var (helpOutput, helpExitCode) = await RunCommandAsync(executable, Path.GetFullPath(cwd),
                nodePrefix.Append("--help"), cancellationToken).ConfigureAwait(false);
            recognized = helpExitCode == 0 && RecognizesProvider(provider, Sanitize(helpOutput));
        }
        if ((provider is AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot) && !recognized)
            throw new InvalidOperationException(CoreText.Get("CliProviderMismatch", provider));
        return new CliProbeResult(executable, version, recognized);
    }

    internal static bool RecognizesProvider(string provider, string output) => provider switch
    {
        AgentProfile.Codex => Regex.IsMatch(output, @"\bcodex(?:[- ]cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.Claude => Regex.IsMatch(output, @"\bclaude\s+code\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.Gemini => Regex.IsMatch(output, @"\bgemini(?:\s+cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        AgentProfile.GitHubCopilot => Regex.IsMatch(output, @"\bgithub\s+copilot(?:\s+cli)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        _ => false
    };

    private static bool ProviderMatchesPackage(string provider, string packageName) => provider switch
    {
        AgentProfile.Gemini => packageName == "@google/gemini-cli",
        AgentProfile.GitHubCopilot => packageName == "@github/copilot",
        _ => false
    };

    private async Task<(string Output, int ExitCode)> RunCommandAsync(string executable, string cwd,
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
        return (await stdout.ConfigureAwait(false), process.ExitCode);
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
