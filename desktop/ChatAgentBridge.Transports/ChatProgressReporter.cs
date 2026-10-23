using System.Diagnostics;

namespace ChatAgentBridge.Transports;

/// <summary>Safe lifecycle states for a transport-owned progress message.</summary>
public enum ChatProgressState
{
    Received,
    Running,
    Completed,
    Failed,
    Cancelled,
    Rejected,
    Disconnected,
    FollowUpApplied,
    FollowUpUncertain,
    FollowUpPending
}

/// <summary>
/// Publishes one progress message and edits it as work proceeds. Progress failures are intentionally
/// isolated from the request being processed; the ordinary response remains the caller's responsibility.
/// </summary>
public sealed class ChatProgressReporter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LifecycleWaitLimit = TimeSpan.FromSeconds(5);

    private readonly IChatProgressTransport? _transport;
    private readonly IChatProgressLifecycleTransport? _lifecycleTransport;
    private readonly ChatRequest _request;
    private readonly Func<ChatProgressState, TimeSpan, string> _format;
    private readonly Func<CancellationToken, Task<bool>> _canPublish;
    private readonly Action<Exception>? _onFailure;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _providerGate = new(1, 1);
    private readonly Stopwatch _elapsed = new();
    private readonly CancellationTokenSource _heartbeatStop = new();

    private ChatProgressMessage? _message;
    private Task? _heartbeatTask;
    private bool _receiptAttempted;
    private bool _started;
    private bool _terminal;
    private bool _disposed;
    private bool _progressCompleted;
    private ChatProgressState _heartbeatState = ChatProgressState.Running;

    public ChatProgressReporter(
        IChatTransport transport,
        ChatRequest request,
        Func<ChatProgressState, TimeSpan, string> format,
        Func<CancellationToken, Task<bool>> canPublish,
        Action<Exception>? onFailure = null,
        TimeSpan? interval = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport as IChatProgressTransport;
        _lifecycleTransport = transport as IChatProgressLifecycleTransport;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _canPublish = canPublish ?? throw new ArgumentNullException(nameof(canPublish));
        _onFailure = onFailure;
        _interval = interval ?? DefaultInterval;
        _timeout = timeout ?? DefaultTimeout;
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is null)
            return;

        if (!await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (_terminal || _disposed || _receiptAttempted)
                return;

            _receiptAttempted = true;
            var text = Format(ChatProgressState.Received);
            if (text is null)
                return;
            _message = await TryPostAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => StartCoreAsync(ChatProgressState.Running, cancellationToken);

    public Task StartFollowUpAsync(CancellationToken cancellationToken = default) => StartCoreAsync(ChatProgressState.FollowUpPending, cancellationToken);

    private async Task StartCoreAsync(ChatProgressState state, CancellationToken cancellationToken)
    {
        if (_transport is null || !await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (_terminal || _disposed || _started)
                return;

            if (!_receiptAttempted)
            {
                _receiptAttempted = true;
                var receiptText = Format(ChatProgressState.Received);
                if (receiptText is not null)
                    _message = await TryPostAsync(receiptText, cancellationToken).ConfigureAwait(false);
            }

            _started = true;
            _heartbeatState = state;
            _elapsed.Start();
            if (_message is null)
                return;

            var text = Format(state);
            if (text is not null)
                await TryUpdateAsync(_message, text, cancellationToken, allowTerminal: false).ConfigureAwait(false);

            if (state == ChatProgressState.FollowUpPending)
                await CompleteProgressAsync().ConfigureAwait(false);

            if (!_terminal && !_disposed && _heartbeatTask is null)
                _heartbeatTask = HeartbeatLoopAsync(_heartbeatStop.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task FinishAsync(ChatProgressState terminal, CancellationToken cancellationToken = default)
    {
        if (!IsTerminalState(terminal) || _transport is null)
            return;
        if (!await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (_terminal || _disposed)
                return;

            // Close the state transition before stopping the heartbeat. A tick that has not already
            // entered its provider call will observe this flag and cannot publish a stale Running edit.
            _terminal = true;
            _heartbeatStop.Cancel();
            await CompleteProgressAsync().ConfigureAwait(false);
            await DrainHeartbeatAsync().ConfigureAwait(false);
            _elapsed.Stop();

            if (_message is null)
                return;
            var text = Format(terminal);
            if (text is not null)
                await TryUpdateAsync(_message, text, cancellationToken, allowTerminal: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await EnterLifecycleAsync(CancellationToken.None).ConfigureAwait(false))
            return;
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _heartbeatStop.Cancel();
            await CompleteProgressAsync().ConfigureAwait(false);
            await DrainHeartbeatAsync().ConfigureAwait(false);
            _elapsed.Stop();
        }
        finally
        {
            _lifecycleGate.Release();
            _heartbeatStop.Dispose();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = _message;
                if (message is null || _terminal || _disposed)
                    return;
                var text = Format(_heartbeatState);
                if (text is not null)
                    await TryUpdateAsync(message, text, cancellationToken, allowTerminal: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private async Task<ChatProgressMessage?> TryPostAsync(string text, CancellationToken callerToken)
    {
        if (_transport is null || !await CanPublishAsync(callerToken).ConfigureAwait(false))
            return null;

        return await InvokeProviderAsync(
            token => _transport.PostProgressAsync(_request, text, token), callerToken).ConfigureAwait(false);
    }

    private async Task TryUpdateAsync(ChatProgressMessage message, string text, CancellationToken callerToken, bool allowTerminal)
    {
        if (_transport is null || _disposed || _terminal && !allowTerminal ||
            !await CanPublishAsync(callerToken).ConfigureAwait(false) ||
            _disposed || _terminal && !allowTerminal)
            return;

        await InvokeProviderAsync(async token =>
        {
            await _transport.UpdateProgressAsync(_request, message, text, token).ConfigureAwait(false);
            return true;
        }, callerToken).ConfigureAwait(false);
    }

    private async Task<T?> InvokeProviderAsync<T>(Func<CancellationToken, Task<T>> invoke, CancellationToken callerToken)
    {
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutSource.Token);
        var gateHeld = false;
        Task<T>? operation = null;
        try
        {
            await _providerGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateHeld = true;
            operation = invoke(linked.Token);
            return await operation.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (linked.IsCancellationRequested)
        {
            if (timeoutSource.IsCancellationRequested || !callerToken.IsCancellationRequested)
                ReportFailure(ex);
            return default;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return default;
        }
        finally
        {
            if (gateHeld)
            {
                if (operation is { IsCompleted: false })
                {
                    _ = operation.ContinueWith(
                        completed =>
                        {
                            _ = completed.Exception;
                            _providerGate.Release();
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    _providerGate.Release();
                }
            }
        }
    }

    private async Task<bool> CanPublishAsync(CancellationToken callerToken)
    {
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutSource.Token);
        try
        {
            return await _canPublish(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (linked.IsCancellationRequested)
        {
            if (timeoutSource.IsCancellationRequested || !callerToken.IsCancellationRequested)
                ReportFailure(ex);
            return false;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
    }

    private string? Format(ChatProgressState state)
    {
        try
        {
            return _format(state, _elapsed.Elapsed);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return null;
        }
    }

    private void ReportFailure(Exception exception)
    {
        try { _onFailure?.Invoke(exception); }
        catch { }
    }

    private async Task CompleteProgressAsync()
    {
        if (_message is null || _lifecycleTransport is null || _progressCompleted)
            return;
        _progressCompleted = true;
        using var timeout = new CancellationTokenSource(_timeout);
        Task? cleanup = null;
        try
        {
            cleanup = _lifecycleTransport.CompleteProgressAsync(_message, timeout.Token);
            await cleanup.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally
        {
            if (cleanup is not null)
                _ = cleanup.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<bool> EnterLifecycleAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(LifecycleWaitLimit);
        try
        {
            await _lifecycleGate.WaitAsync(wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DrainHeartbeatAsync()
    {
        var heartbeat = _heartbeatTask;
        if (heartbeat is null)
            return;
        try { await heartbeat.WaitAsync(LifecycleWaitLimit).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException ex) { ReportFailure(ex); }
        catch (Exception ex) { ReportFailure(ex); }
        _heartbeatTask = null;
    }

    private static bool IsTerminalState(ChatProgressState state) =>
        state is ChatProgressState.Completed or ChatProgressState.Failed or ChatProgressState.Cancelled
            or ChatProgressState.Rejected or ChatProgressState.Disconnected
            or ChatProgressState.FollowUpApplied or ChatProgressState.FollowUpUncertain;
}
