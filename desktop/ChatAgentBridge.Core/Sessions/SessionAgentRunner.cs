using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Sessions;

public sealed class SessionAgentRunner
{
    private static readonly string[] DefaultCodexArguments = ["exec", "--json", "-"];
    private readonly SessionStore _store;
    private readonly IAgentRunner _runner;

    public SessionAgentRunner(SessionStore store, IAgentRunner? runner = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? new AgentRunner();
    }

    public Task<AgentRunResult> RunAsync(SessionKey key, BridgeSettings settings, string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        return _store.WithSessionLockAsync(key,
            token => RunLockedAsync(key, settings, prompt, token), cancellationToken);
    }

    private async Task<AgentRunResult> RunLockedAsync(SessionKey key, BridgeSettings settings, string prompt,
        CancellationToken cancellationToken)
    {
        var existing = await _store.FindAsync(key, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> arguments;
        try { arguments = AgentArgumentParser.Parse(settings.AgentArguments); }
        catch (FormatException ex)
        {
            return new AgentRunResult(false, -1, "", $"Agent arguments are invalid: {ex.Message}");
        }
        var tracksCodex = AgentProfile.ResolveProvider(settings) == AgentProfile.Codex && IsCodexExecutable(settings.AgentExecutable) &&
            arguments.SequenceEqual(DefaultCodexArguments, StringComparer.Ordinal);
        if (!tracksCodex)
        {
            if (existing is not null)
                throw new InvalidOperationException("This chat thread has a saved Codex session. Restore the default Codex arguments or detach the saved session before using custom agent arguments.");
            return await _runner.RunAsync(settings, prompt, cancellationToken).ConfigureAwait(false);
        }

        var workspace = NormalizeWorkspace(settings.WorkspaceRoot);
        var executable = NormalizeExecutableIdentity(settings.AgentExecutable, workspace);
        if (existing is not null)
        {
            if (!SamePath(existing.WorkspaceRoot, workspace) ||
                !SameExecutable(existing.AgentExecutable, executable) ||
                !AgentArgumentParser.Parse(existing.AgentArguments).SequenceEqual(DefaultCodexArguments, StringComparer.Ordinal))
                throw new InvalidOperationException("The saved Codex session is bound to a different workspace or executable. Detach the session before changing its settings.");

            var resumedSettings = settings with
            {
                WorkspaceRoot = workspace,
                AgentArguments = $"exec resume --json {Guid.Parse(existing.SessionId):D} -",
                AgentModel = existing.Model ?? settings.AgentModel
            };
            var resumed = await _runner.RunAsync(resumedSettings, prompt, cancellationToken).ConfigureAwait(false);
            var resumeOutput = CodexOutputParser.Parse(resumed.Output);
            if (resumeOutput.TurnFailed && resumed.Success)
                resumed = FailClosed(resumed, "Codex reported turn.failed while resuming the saved thread.");
            if (resumeOutput.SessionId is not null)
            {
                if (!Guid.TryParse(existing.SessionId, out var savedId) || Guid.Parse(resumeOutput.SessionId) != savedId)
                    return FailClosed(resumed, "Codex returned a different thread id while resuming; the saved binding was retained.");
                if (resumed.Success)
                    await _store.SaveAsync(existing with { UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
            }
            if (resumeOutput.SessionId is null && resumed.Success)
                return FailClosed(resumed, "Codex did not confirm the saved thread id while resuming; the saved binding was retained.");
            return resumed;
        }

        var newSettings = settings with { WorkspaceRoot = workspace, AgentModel = existing?.Model ?? settings.AgentModel };
        var result = await _runner.RunAsync(newSettings, prompt, cancellationToken).ConfigureAwait(false);
        var output = CodexOutputParser.Parse(result.Output);
        if (output.SessionId is not null)
        {
            await _store.SaveAsync(new SessionBinding(key, workspace, executable, settings.AgentArguments,
                output.SessionId, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            if (output.TurnFailed && result.Success)
                return FailClosed(result, "Codex reported turn.failed for this turn; the session was saved so it can be resumed.");
            return result;
        }
        if (result.Success)
            return FailClosed(result, output.TurnFailed
                ? "Codex reported turn.failed and returned no valid thread id."
                : "Codex completed without a valid thread.started session id; this turn cannot be resumed safely.");
        return result;
    }

    private static AgentRunResult FailClosed(AgentRunResult result, string message)
        => result with
        {
            Success = false,
            ExitCode = result.ExitCode == 0 ? -1 : result.ExitCode,
            Error = string.IsNullOrWhiteSpace(result.Error) ? message : result.Error + Environment.NewLine + message
        };

    private static bool IsCodexExecutable(string executable)
    {
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }
            .Any(candidate => string.Equals(leaf, candidate, comparison));
    }

    private static string NormalizeWorkspace(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("A workspace directory is required.", nameof(workspace));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    }

    private static string NormalizeExecutableIdentity(string executable, string workspace)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("An agent executable is required.", nameof(executable));
        if (Path.IsPathFullyQualified(executable)) return Path.GetFullPath(executable);
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(Path.Combine(workspace, executable));
        return executable.Trim();
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SameExecutable(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }
}
