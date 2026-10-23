using System.Threading.Channels;

namespace ChatAgentBridge.Core.History;

/// <summary>Bounded, single-worker batching for noncritical received audit events.</summary>
public sealed class ReceivedAuditQueue : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    public const int DefaultBatchSize = 64;

    private readonly Channel<AuditEvent> _channel;
    private readonly Func<IReadOnlyList<AuditEvent>, CancellationToken, Task> _appendMany;
    private readonly Action<Exception>? _onFailure;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Task _worker;
    private int _failureReported;
    private readonly object _lifecycleLock = new();
    private Task<bool>? _shutdownTask;
    private Task? _disposeTask;

    public ReceivedAuditQueue(
        AuditStore store,
        Action<Exception>? onFailure = null,
        int capacity = DefaultCapacity,
        int batchSize = DefaultBatchSize)
        : this(CreateWriter(store), onFailure, capacity, batchSize) { }

    /// <summary>Creates a queue with an injectable writer, useful for deterministic queue tests.</summary>
    public ReceivedAuditQueue(
        Func<IReadOnlyList<AuditEvent>, CancellationToken, Task> appendMany,
        Action<Exception>? onFailure = null,
        int capacity = DefaultCapacity,
        int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(appendMany);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _appendMany = appendMany;
        _onFailure = onFailure;
        BatchSize = batchSize;
        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _worker = RunWorkerAsync();
    }

    public int BatchSize { get; }

    public ValueTask EnqueueAsync(AuditEvent record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _channel.Writer.WriteAsync(record, cancellationToken);
    }

    /// <summary>Stops accepting records and gives queued writes a bounded time to finish.</summary>
    public Task<bool> CompleteAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_lifecycleLock)
        {
            if (_shutdownTask is not null)
                return cancellationToken.CanBeCanceled ? _shutdownTask.WaitAsync(cancellationToken) : _shutdownTask;
            _channel.Writer.TryComplete();
            _shutdownTask = DrainAsync(timeout, cancellationToken);
            return _shutdownTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return await StopWorkerAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await StopWorkerAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> StopWorkerAsync()
    {
        _workerCancellation.Cancel();
        try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return false;
    }

    private async Task DisposeCoreAsync()
    {
        await CompleteAndDrainAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (_worker.IsCompleted) _workerCancellation.Dispose();
        else _ = _worker.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _workerCancellation, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Func<IReadOnlyList<AuditEvent>, CancellationToken, Task> CreateWriter(AuditStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (records, cancellationToken) => store.AppendManyAsync(records, cancellationToken);
    }

    private async Task RunWorkerAsync()
    {
        var token = _workerCancellation.Token;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                var batch = new List<AuditEvent>(BatchSize);
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var record)) batch.Add(record);
                if (batch.Count == 0) continue;
                try { await _appendMany(batch, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Received events are best-effort. Report once and keep the worker alive for later batches.
                    if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                    {
                        try { _onFailure?.Invoke(ex); }
                        catch (Exception) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
