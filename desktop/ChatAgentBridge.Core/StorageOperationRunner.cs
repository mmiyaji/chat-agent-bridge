using System.Collections.Immutable;
using System.Diagnostics;

namespace ChatAgentBridge.Core;

/// <summary>Runs local storage operations off the caller thread and in order for one store instance.</summary>
public sealed class StorageOperationRunner
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)];
    private readonly SemaphoreSlim _serial = new(1, 1);

    public Task<T> RunAsync<T>(string operation, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        StorageOperationDiagnostics.ValidateOperation(operation);
        return RunCoreAsync(operation, action, cancellationToken);
    }

    public Task RunAsync(string operation, Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync<object?>(operation, async token => { await action(token).ConfigureAwait(false); return null; }, cancellationToken);
    }

    private async Task<T> RunCoreAsync<T>(string operation, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        var retries = 0;
        try
        {
            while (true)
            {
                try
                {
                    var result = await Task.Run(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
                    StorageOperationDiagnostics.Record(operation, timer.ElapsedMilliseconds, retries, "success", null);
                    return result;
                }
                catch (IOException ex) when (IsSharingOrLockViolation(ex) && retries < RetryDelays.Length)
                {
                    try { await Task.Delay(RetryDelays[retries], cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException canceled)
                    {
                        StorageOperationDiagnostics.Record(operation, timer.ElapsedMilliseconds, retries, "cancelled", canceled.HResult);
                        throw;
                    }
                    retries++;
                }
                catch (Exception ex)
                {
                    StorageOperationDiagnostics.Record(operation, timer.ElapsedMilliseconds, retries, "failure", ex.HResult);
                    throw;
                }
            }
        }
        finally { _serial.Release(); }
    }

    private static bool IsSharingOrLockViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;
}

public sealed record StorageOperationDiagnostic(DateTimeOffset RecordedAtUtc, string Operation, long ElapsedMilliseconds,
    int RetryCount, string Outcome, int? HResult);

/// <summary>Process-local, bounded diagnostics for storage operations. Records contain no paths or exception text.</summary>
public static class StorageOperationDiagnostics
{
    private const int Capacity = 64;
    private static readonly object Gate = new();
    private static readonly Queue<StorageOperationDiagnostic> Records = new();

    public static IReadOnlyList<StorageOperationDiagnostic> Snapshot()
    {
        lock (Gate) return Records.ToImmutableArray();
    }

    internal static void ValidateOperation(string operation)
    {
        if (operation is not ("read" or "write" or "probe"))
            throw new ArgumentOutOfRangeException(nameof(operation), "Storage operation name is not allowlisted.");
    }

    internal static void Record(string operation, long elapsedMilliseconds, int retryCount, string outcome, int? hResult)
    {
        lock (Gate)
        {
            if (Records.Count == Capacity) Records.Dequeue();
            Records.Enqueue(new StorageOperationDiagnostic(DateTimeOffset.UtcNow, operation, elapsedMilliseconds, retryCount, outcome, hResult));
        }
    }
}
