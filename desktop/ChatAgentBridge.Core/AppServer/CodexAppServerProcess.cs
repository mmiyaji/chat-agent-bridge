using System.Diagnostics;
using System.Text;

namespace ChatAgentBridge.Core.AppServer;

public sealed class CodexAppServerProcessFactory : IAppServerProcessFactory
{
    public Task<IAppServerProcess> StartAsync(string executable, string workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"App Server workspace does not exist: {workspace}");
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Codex executable is required.", nameof(executable));
        var leaf = Path.GetFileName(executable.Trim().Trim('"')).Replace('\\', '/').Split('/').Last();
        if (!new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }.Contains(leaf,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            throw new InvalidOperationException("Codex App Server requires AgentExecutable to identify the native Codex CLI.");

        var nativeExecutable = CodexExecutableResolver.Resolve(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = nativeExecutable,
            WorkingDirectory = Path.GetFullPath(workspace),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        startInfo.Environment["CHAT_AGENT_BRIDGE"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Codex App Server process did not start.");
        }
        catch { process.Dispose(); throw; }
        IAppServerProcess handle = new CodexAppServerProcess(process);
        return Task.FromResult(handle);
    }
}

internal sealed class CodexAppServerProcess : IAppServerProcess, IAppServerProgressSource
{
    private readonly Process _process;
    private readonly CodexAppServerClient _client;
    private readonly object _stderrSync = new();
    private readonly StringBuilder _stderrBuffer = new();
    private int _disposed;

    public CodexAppServerProcess(Process process)
    {
        _process = process;
        _client = new CodexAppServerClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        ExitCode = WaitForExitAsync();
        _ = ReadBoundedAsync(process.StandardError, 16_000, _stderrBuffer, _stderrSync, ReportStandardError);
    }

    public IAppServerRpcClient Client => _client;
    public event Action<string>? StandardErrorReceived;
    public Task<int?> ExitCode { get; }
    public Task<string> StandardError { get { lock (_stderrSync) return Task.FromResult(_stderrBuffer.ToString()); } }

    public void KillTree()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _process.StandardInput.Close(); } catch (IOException) { }
        catch (InvalidOperationException) { }
        if (!ExitCode.IsCompleted)
        {
            if (await Task.WhenAny(ExitCode, Task.Delay(TimeSpan.FromMilliseconds(750))).ConfigureAwait(false) != ExitCode)
            {
                KillTree();
                await Task.WhenAny(ExitCode, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task<int?> WaitForExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task ReadBoundedAsync(StreamReader reader, int maxCharacters, StringBuilder builder, object sync,
        Action<string> report)
    {
        var buffer = new char[2048];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) break;
                lock (sync)
                {
                    var keep = Math.Min(count, maxCharacters - builder.Length);
                    if (keep > 0) builder.Append(buffer, 0, keep);
                }
                if (StandardErrorReceived is not null) report(new string(buffer, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void ReportStandardError(string text)
    {
        var handlers = StandardErrorReceived;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(text); }
            catch { /* Diagnostics observers must never stop stderr draining. */ }
        }
    }
}
