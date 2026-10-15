using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;

namespace ChatAgentBridge.Core;

public sealed record AgentRunResult(bool Success, int ExitCode, string Output, string Error);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default);
}

public sealed class AgentRunner : IAgentRunner
{
    private const int MaxCapturedCharacters = 1_000_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ArgumentException("A workspace directory is required.", nameof(settings));
        var workspace = Path.GetFullPath(settings.WorkspaceRoot);
        if (!System.IO.Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspace}");
        if (string.IsNullOrWhiteSpace(settings.AgentExecutable))
            throw new ArgumentException("An agent executable is required.", nameof(settings));

        var gate = WorkspaceGates.GetOrAdd(workspace, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(settings, workspace, prompt, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static async Task<AgentRunResult> RunLockedAsync(BridgeSettings settings, string workspace, string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string executable;
        try { executable = CodexExecutableResolver.Resolve(settings.AgentExecutable); }
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
        foreach (var argument in AgentArgumentParser.Parse(settings.AgentArguments)) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new AgentRunResult(false, -1, "", "Agent process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AgentRunResult(false, -1, "", ex.Message);
        }

        // Window shutdown can end the UI process before an async catch resumes.
        // Kill synchronously when cancellation is requested so the child cannot outlive it.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        var stdout = ReadBoundedAsync(process.StandardOutput, MaxCapturedCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaxCapturedCharacters);
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

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new AgentRunResult(process.ExitCode == 0, process.ExitCode, output, error);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder(Math.Min(limit, 16_384));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, limit - builder.Length);
            if (keep > 0) builder.Append(buffer, 0, keep);
        }
        return builder.ToString();
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
