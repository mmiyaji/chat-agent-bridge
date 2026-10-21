using System.Diagnostics;
using System.Text;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public sealed record AgentRunResult(bool Success, int ExitCode, string Output, string Error,
    CodexOutput? Codex = null, bool HasOutputTruncated = false)
{
    public ClaudeOutput? Claude { get; init; }
    public AgentUsage? Usage { get; init; }
    public AgentExecutionMetrics? Metrics { get; init; }
}

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default);

    Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt,
        IProgress<AgentRunProgress>? progress, CancellationToken cancellationToken = default)
        => RunAsync(settings, prompt, cancellationToken);
}

public sealed class AgentRunner : IAgentRunner
{
    private const int MaxCapturedCharacters = 1_000_000;

    public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt,
        CancellationToken cancellationToken = default)
        => RunAsync(settings, prompt, progress: null, cancellationToken);

    public async Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt,
        IProgress<AgentRunProgress>? progress, CancellationToken cancellationToken = default)
    {
        var totalTimer = Stopwatch.StartNew();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ArgumentException("A workspace directory is required.", nameof(settings));
        var workspace = Path.GetFullPath(settings.WorkspaceRoot);
        if (!System.IO.Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspace}");
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, workspace))
            throw new InvalidOperationException(CoreText.Get("TrustedWorkspaceRequired"));
        if (string.IsNullOrWhiteSpace(settings.AgentExecutable))
            throw new ArgumentException("An agent executable is required.", nameof(settings));

        var queueTimer = Stopwatch.StartNew();
        using var workspaceLease = await AgentWorkspaceGate.AcquireAsync(workspace, cancellationToken).ConfigureAwait(false);
        var queueMilliseconds = queueTimer.Elapsed.TotalMilliseconds;
        var result = await RunLockedAsync(settings, workspace, prompt, progress, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Metrics = (result.Metrics ?? new AgentExecutionMetrics()) with
            {
                QueueMilliseconds = queueMilliseconds,
                TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds
            }
        };
    }

    private static async Task<AgentRunResult> RunLockedAsync(BridgeSettings settings, string workspace, string prompt,
        IProgress<AgentRunProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentInvocation invocation;
        try { invocation = AgentProfile.BuildInvocation(settings); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return new AgentRunResult(false, -1, "", ex.Message); }
        // Partial output is opt-in because older Claude CLI versions may not support this flag.
        if (invocation.Provider == AgentProfile.Claude && progress is not null)
            invocation = invocation with { Arguments = invocation.Arguments.Append("--include-partial-messages").ToArray() };
        NodeCliPackageResolver.Resolution? nodePackage;
        try { nodePackage = NodeCliPackageResolver.Resolve(invocation.Executable, invocation.Provider); }
        catch (InvalidOperationException ex) { return new AgentRunResult(false, -1, "", ex.Message); }
        string executable;
        try { executable = nodePackage?.Executable ?? CodexExecutableResolver.Resolve(invocation.Executable); }
        catch (InvalidOperationException ex) { return new AgentRunResult(false, -1, "", ex.Message); }
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        if (nodePackage is not null) start.ArgumentList.Add(nodePackage.EntryPoint);
        foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var processStartTimer = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) return new AgentRunResult(false, -1, "", "Agent process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AgentRunResult(false, -1, "", ex.Message);
        }
        var processStartMilliseconds = processStartTimer.Elapsed.TotalMilliseconds;

        // Window shutdown can end the UI process before an async catch resumes.
        // Kill synchronously when cancellation is requested so the child cannot outlive it.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        var stdout = ReadBoundedAsync(process.StandardOutput, MaxCapturedCharacters,
            invocation.Provider == AgentProfile.Codex, invocation.Provider == AgentProfile.Claude, progress);
        var stderr = ReadBoundedTextAsync(process.StandardError, MaxCapturedCharacters, progress);
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Agent execution was cancelled while writing the prompt.", ex, cancellationToken);
                // The child may exit before consuming all input; its exit status and output remain authoritative.
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            throw;
        }

        var (output, codex, claude, outputTruncated) = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        var usage = codex?.Usage ?? claude?.Usage;
        var success = process.ExitCode == 0;
        var exitCode = process.ExitCode;
        if (success && invocation.Provider == AgentProfile.Claude && claude?.HasResult != true)
        {
            success = false;
            exitCode = -1;
            error = AppendError(error, "Claude Code exited successfully without a terminal result event.");
        }
        if (success && invocation.Provider == AgentProfile.Claude && claude?.TurnFailed == true)
        {
            success = false;
            exitCode = -1;
            error = AppendError(error, "Claude Code reported a failed result event.");
        }
        return new AgentRunResult(success, exitCode, output, error, codex, outputTruncated)
        {
            Claude = claude,
            Usage = usage,
            Metrics = new AgentExecutionMetrics { ProcessStartMilliseconds = processStartMilliseconds }
        };
    }

    private static string AppendError(string error, string message)
        => string.IsNullOrWhiteSpace(error) ? message : error + Environment.NewLine + message;

    private static async Task<(string Output, CodexOutput? Codex, ClaudeOutput? Claude, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int limit, bool parseCodex, bool parseClaude, IProgress<AgentRunProgress>? progress)
    {
        var builder = new StringBuilder(Math.Min(limit, 16_384));
        var buffer = new char[8192];
        var parseEvents = parseCodex || parseClaude;
        var line = parseEvents ? new StringBuilder(1024) : null;
        var codexParser = parseCodex ? new CodexOutputParser.Accumulator() : null;
        var claudeParser = parseClaude ? new ClaudeOutputParser.Accumulator() : null;
        var progressParser = progress is not null && parseEvents ? new AgentRunProgressParser(parseCodex ? AgentProfile.Codex : AgentProfile.Claude) : null;
        var droppingLongLine = false;
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, limit - builder.Length);
            if (keep > 0) builder.Append(buffer, 0, keep);
            if (keep < read) truncated = true;

            if (!parseEvents)
            {
                if (progress is not null)
                    for (var offset = 0; offset < read; offset += AgentRunProgressParser.MaxTextCharacters)
                        SafeReport(progress, new("stdout", new string(buffer, offset, Math.Min(AgentRunProgressParser.MaxTextCharacters, read - offset))));
                continue;
            }
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    if (!droppingLongLine)
                    {
                        var text = line!.ToString();
                        codexParser?.AddLine(text);
                        claudeParser?.AddLine(text);
                        if (progressParser is not null)
                            foreach (var update in progressParser.ParseLine(text)) SafeReport(progress, update);
                    }
                    line!.Clear();
                    droppingLongLine = false;
                }
                else if (!droppingLongLine)
                {
                    if (line!.Length < CodexOutputParser.MaxLineCharacters) line.Append(ch);
                    else { line.Clear(); droppingLongLine = true; }
                }
            }
        }
        if (!droppingLongLine && line is { Length: > 0 })
        {
            codexParser?.AddLine(line.ToString());
            claudeParser?.AddLine(line.ToString());
            if (progressParser is not null)
                foreach (var update in progressParser.ParseLine(line.ToString())) SafeReport(progress, update);
        }
        return (builder.ToString(), codexParser?.Snapshot(), claudeParser?.Snapshot(), truncated);
    }

    private static async Task<string> ReadBoundedTextAsync(StreamReader reader, int limit, IProgress<AgentRunProgress>? progress)
    {
        var builder = new StringBuilder(Math.Min(limit, 16_384));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, limit - builder.Length);
            if (keep > 0) builder.Append(buffer, 0, keep);
            if (progress is not null)
                for (var offset = 0; offset < read; offset += AgentRunProgressParser.MaxTextCharacters)
                    SafeReport(progress, new("stderr", new string(buffer, offset, Math.Min(AgentRunProgressParser.MaxTextCharacters, read - offset))));
        }
        return builder.ToString();
    }

    private static void SafeReport(IProgress<AgentRunProgress>? progress, AgentRunProgress update)
    {
        try { progress?.Report(update); }
        catch { /* Progress observers are UI-facing and must not break process draining. */ }
    }
}

public static class AgentArgumentParser
{
    // Splits a command-line string into arguments without invoking a shell.
    public static IReadOnlyList<string> Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var ch = commandLine[index];
            if (ch == '\\')
            {
                var slashStart = index;
                while (index + 1 < commandLine.Length && commandLine[index + 1] == '\\') index++;
                var slashCount = index - slashStart + 1;
                if (index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 1) current.Append('"');
                    else quoted = !quoted;
                    index++;
                    started = true;
                    continue;
                }
                current.Append('\\', slashCount);
                started = true;
                continue;
            }
            if (ch == '"') { quoted = !quoted; started = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) { args.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            current.Append(ch);
            started = true;
        }
        if (quoted) throw new FormatException("Agent arguments contain an unterminated quote.");
        if (started) args.Add(current.ToString());
        return args;
    }
}
