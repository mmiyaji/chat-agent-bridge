using System.Collections.Concurrent;

namespace ChatAgentBridge.Core;

/// <summary>Coordinates native agent processes that share one workspace.</summary>
public static class AgentWorkspaceGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<IDisposable> AcquireAsync(string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace directory is required.", nameof(workspaceRoot));
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var gate = Gates.GetOrAdd(workspace, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
