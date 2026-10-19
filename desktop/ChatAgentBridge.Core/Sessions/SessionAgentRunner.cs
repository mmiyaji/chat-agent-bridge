using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Sessions;

public sealed class SessionAgentRunner
{
    private static readonly string[] DefaultCodexArguments = ["exec", "--json", "-"];
    private const string ClaudeSessionMarker = "claude-stream-json-v1";
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
        settings = await _store.ApplyWorkspaceAsync(key, settings, cancellationToken).ConfigureAwait(false);
        var existing = await _store.FindExecutionBindingAsync(key, cancellationToken).ConfigureAwait(false);
        prompt = await _store.PrepareHandoffPromptAsync(key, prompt, cancellationToken).ConfigureAwait(false);
        if (existing?.AgentPermissionMode is not null)
            settings = settings with { AgentPermissionMode = existing.AgentPermissionMode };
        var provider = AgentProfile.ResolveProvider(settings);
        IReadOnlyList<string> arguments;
        try { arguments = provider == AgentProfile.Claude ? Array.Empty<string>() : AgentArgumentParser.Parse(settings.AgentArguments); }
        catch (FormatException ex)
        {
            return new AgentRunResult(false, -1, "", $"Agent arguments are invalid: {ex.Message}");
        }
        var tracksCodex = provider == AgentProfile.Codex && IsCodexExecutable(settings.AgentExecutable) &&
            arguments.SequenceEqual(DefaultCodexArguments, StringComparer.Ordinal);
        var tracksClaude = provider == AgentProfile.Claude && settings.AgentMode == "exec";
        if (!tracksCodex && !tracksClaude)
        {
            if (existing is not null)
                throw new InvalidOperationException("This chat thread has a saved agent session. Restore the saved provider settings or detach the saved session before changing agent arguments.");
            return await _runner.RunAsync(settings, prompt, cancellationToken).ConfigureAwait(false);
        }

        var workspace = NormalizeWorkspace(settings.WorkspaceRoot);
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, workspace))
            throw new InvalidOperationException("The selected workspace is outside the configured trusted workspace roots.");
        var executable = NormalizeExecutableIdentity(AgentProfile.ResolveEffectiveExecutable(settings), workspace);
        if (existing is not null)
        {
            if (!SamePath(existing.WorkspaceRoot, workspace) ||
                !SameExecutable(existing.AgentExecutable, executable) ||
                (tracksCodex && !AgentArgumentParser.Parse(existing.AgentArguments).SequenceEqual(DefaultCodexArguments, StringComparer.Ordinal)) ||
                (tracksClaude && !string.Equals(existing.AgentArguments, ClaudeSessionMarker, StringComparison.Ordinal)))
                throw new InvalidOperationException("The saved agent session is bound to different provider settings, workspace, or executable. Detach the session before changing its settings.");

            var resumedSettings = settings with
            {
                WorkspaceRoot = workspace,
                AgentArguments = tracksCodex
                    ? $"exec resume --json {Guid.Parse(existing.SessionId):D} -"
                    : $"--resume {Guid.Parse(existing.SessionId):D}",
                AgentModel = existing.Model ?? settings.AgentModel,
                AgentReasoningEffort = EffectiveEffort(existing, settings)
            };
            var resumed = await _runner.RunAsync(resumedSettings, prompt, cancellationToken).ConfigureAwait(false);
            var resumedClaude = tracksClaude ? resumed.Claude ?? ClaudeOutputParser.Parse(resumed.Output) : null;
            if (tracksClaude && resumed.Success && resumedClaude?.HasResult != true)
                resumed = FailClosed(resumed, "Claude Code exited successfully without a terminal result event; the saved session was retained.");
            var resumeSessionId = tracksCodex
                ? (resumed.Codex ?? CodexOutputParser.Parse(resumed.Output)).SessionId
                : resumedClaude!.SessionId;
            var resumeFailed = tracksCodex
                ? (resumed.Codex ?? CodexOutputParser.Parse(resumed.Output)).TurnFailed
                : resumedClaude!.TurnFailed;
            if (resumeFailed && resumed.Success)
                resumed = FailClosed(resumed, tracksCodex
                    ? "Codex reported turn.failed while resuming the saved thread."
                    : "Claude Code reported a failed turn while resuming the saved session.");
            if (resumeSessionId is not null)
            {
                if (!Guid.TryParse(existing.SessionId, out var savedId) || Guid.Parse(resumeSessionId) != savedId)
                    return FailClosed(resumed, tracksCodex
                        ? "Codex returned a different thread id while resuming; the saved binding was retained."
                        : "Claude Code returned a different session id while resuming; the saved binding was retained.");
                if (resumed.Success)
                    await _store.SaveAsync(existing with { UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
            }
            if (resumeSessionId is null && resumed.Success)
                return FailClosed(resumed, tracksCodex
                    ? "Codex did not confirm the saved thread id while resuming; the saved binding was retained."
                    : "Claude Code did not confirm the saved session id while resuming; the saved binding was retained.");
            return resumed;
        }

        var newSettings = settings with { WorkspaceRoot = workspace, AgentModel = existing?.Model ?? settings.AgentModel,
            AgentReasoningEffort = existing is null ? settings.AgentReasoningEffort : EffectiveEffort(existing, settings),
            AgentArguments = tracksClaude ? "" : settings.AgentArguments };
        var result = await _runner.RunAsync(newSettings, prompt, cancellationToken).ConfigureAwait(false);
        var claudeOutput = tracksClaude ? result.Claude ?? ClaudeOutputParser.Parse(result.Output) : null;
        if (tracksClaude && result.Success && claudeOutput?.HasResult != true)
            result = FailClosed(result, "Claude Code exited successfully without a terminal result event.");
        var newSessionId = tracksCodex
            ? (result.Codex ?? CodexOutputParser.Parse(result.Output)).SessionId
            : claudeOutput!.SessionId;
        var turnFailed = tracksCodex
            ? (result.Codex ?? CodexOutputParser.Parse(result.Output)).TurnFailed
            : claudeOutput!.TurnFailed;
        if (newSessionId is not null)
        {
            var sessionArguments = tracksCodex ? settings.AgentArguments : ClaudeSessionMarker;
            await _store.SaveAsync(new SessionBinding(key, workspace, executable, sessionArguments,
                newSessionId, DateTimeOffset.UtcNow,
                existing?.Model, existing?.ReasoningEffort, existing?.ChatApprovalMode, existing?.ChatResultMode,
                existing?.AgentPermissionMode, existing?.FirstApprovalFingerprint), CancellationToken.None).ConfigureAwait(false);
            if (turnFailed && result.Success)
                return FailClosed(result, tracksCodex
                    ? "Codex reported turn.failed; the session was saved so it can be resumed."
                    : "Claude Code reported a failed turn; the session was saved so it can be resumed.");
            return result;
        }
        if (result.Success)
            return FailClosed(result, turnFailed
                ? tracksCodex ? "Codex reported turn.failed and returned no valid thread id." : "Claude Code reported a failed turn without a valid session id."
                : tracksCodex ? "Codex completed without a valid thread.started session id; this turn cannot be resumed safely." : "Claude Code completed without a valid session id; this turn cannot be resumed safely.");
        return result;
    }

    private static string? EffectiveEffort(SessionBinding session, BridgeSettings settings)
    {
        if (session.ReasoningEffort is not null) return session.ReasoningEffort;
        var globalModel = AgentProfile.NormalizeModel(settings.AgentModel);
        return session.Model is null || string.Equals(AgentProfile.NormalizeModel(session.Model), globalModel, StringComparison.Ordinal)
            ? AgentProfile.NormalizeReasoningEffort(settings.AgentReasoningEffort)
            : null;
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
