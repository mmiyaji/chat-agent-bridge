using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Sessions;

/// <summary>Persists App Server bindings using the same per-conversation lock as exec/detach.</summary>
public sealed class AppServerSessionRunner(SessionStore store, IAppServerRunner? runner = null)
{
    private readonly IAppServerRunner _runner = runner ?? new CodexAppServerRunner();

    public Task<AgentRunResult> RunAsync(SessionKey key, BridgeSettings settings, string prompt,
        AppServerRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        if (settings.AgentMode != "app-server") throw new ArgumentException("App Server mode is required.", nameof(settings));
        if (AgentProfile.ResolveProvider(settings) != AgentProfile.Codex)
            throw new InvalidOperationException("Codex App Server requires the Codex provider. Select Codex or detach the saved session before changing providers.");
        return store.WithSessionLockAsync(key, async token =>
        {
            var existing = await store.FindAsync(key, token).ConfigureAwait(false);
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot));
            var executable = NormalizeExecutable(settings.AgentExecutable, workspace);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (existing is not null &&
                (existing.AgentArguments != "app-server" ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.WorkspaceRoot)), workspace, comparison) ||
                !string.Equals(existing.AgentExecutable, executable, comparison)))
                throw new InvalidOperationException("The saved session belongs to another mode, workspace, or executable. Detach it before changing settings.");

            if (options?.SessionId is not null && options.SessionId != existing?.SessionId)
                throw new ArgumentException("A session ID must come from the saved conversation binding.", nameof(options));
            var effectiveOptions = (options ?? new AppServerRunOptions()) with { SessionId = existing?.SessionId };
            var run = await _runner.RunAsync(settings with { WorkspaceRoot = workspace, AgentModel = existing?.Model ?? settings.AgentModel }, prompt, effectiveOptions, token).ConfigureAwait(false);
            if (!Guid.TryParse(run.SessionId, out var actualId) || actualId == Guid.Empty)
                return Fail(run.Result, "App Server did not return a valid session id.");
            if (existing is not null && Guid.Parse(existing.SessionId) != actualId)
                return Fail(run.Result, "App Server returned a different session id. The saved binding was retained.");

            await store.SaveAsync(new SessionBinding(key, workspace, executable, "app-server", actualId.ToString("D"), DateTimeOffset.UtcNow, existing?.Model), CancellationToken.None).ConfigureAwait(false);
            return run.Result;
        }, cancellationToken);
    }

    private static string NormalizeExecutable(string executable, string workspace)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("An agent executable is required.", nameof(executable));
        if (Path.IsPathFullyQualified(executable)) return Path.GetFullPath(executable);
        return executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(executable, workspace) : executable.Trim();
    }

    private static AgentRunResult Fail(AgentRunResult result, string error) => result with
    {
        Success = false, ExitCode = result.ExitCode == 0 ? -1 : result.ExitCode,
        Error = string.IsNullOrWhiteSpace(result.Error) ? error : result.Error + Environment.NewLine + error
    };
}
