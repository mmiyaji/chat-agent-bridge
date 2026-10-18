using System.Threading;

namespace ChatAgentBridge_Desktop.Startup;

/// <summary>
/// Allows one startup operation at a time. Contending callers are rejected instead of queued.
/// </summary>
public sealed class StartupOperationGuard
{
    private int _held;

    /// <summary>
    /// Attempts to enter the guarded operation, returning null when another operation holds it.
    /// </summary>
    public IDisposable? TryEnter()
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            return null;
        }

        return new Lease(this);
    }

    private void Release() => Interlocked.Exchange(ref _held, 0);

    private sealed class Lease(StartupOperationGuard owner) : IDisposable
    {
        private StartupOperationGuard? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
