using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.ClaudeCode;

internal interface IClaudeInputChannel
{
    Task<bool> WriteAsync(string line, CancellationToken cancellationToken);
    Task CloseAsync();
}

/// <summary>Accepts follow-ups only after a replay acknowledgement confirms their UUID and session.</summary>
internal sealed class ClaudeActiveAgentTurn(
    IClaudeInputChannel input,
    string sessionId,
    Action<string> registerReplay,
    Action<string> removeReplay,
    Func<string, CancellationToken, Task<bool>> waitForReplay,
    Func<bool> processExited) : IActiveAgentTurn
{
    private const int MaxInputs = 100;
    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _active = 1;
    private int _count;

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public async Task<AgentTurnInputResult> AddInputAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _active) == 0 || cancellationToken.IsCancellationRequested)
            return AgentTurnInputResult.NotAccepted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestLimit);
        var entered = false;
        var writeAttempted = false;
        string? pendingId = null;
        try
        {
            await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _active) == 0 || processExited() || Interlocked.Increment(ref _count) > MaxInputs)
                return AgentTurnInputResult.NotAccepted;
            var id = pendingId = Guid.NewGuid().ToString("D");
            var payload = ClaudeStreamingRunner.SerializeUserMessage(id, sessionId, text);
            registerReplay(id);
            writeAttempted = true;
            if (!await input.WriteAsync(payload, timeout.Token).ConfigureAwait(false))
                return AgentTurnInputResult.NotAccepted;
            var acknowledged = await waitForReplay(id, timeout.Token).ConfigureAwait(false);
            return acknowledged ? AgentTurnInputResult.Accepted : AgentTurnInputResult.DeliveryUnknown;
        }
        catch (OperationCanceledException)
        {
            return writeAttempted ? AgentTurnInputResult.DeliveryUnknown : AgentTurnInputResult.NotAccepted;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return writeAttempted ? AgentTurnInputResult.DeliveryUnknown : AgentTurnInputResult.NotAccepted;
        }
        finally
        {
            if (entered) _send.Release();
            // Late acknowledgements after a timeout cannot accumulate or affect another request.
            if (pendingId is not null) removeReplay(pendingId);
        }
    }

    public async Task CloseInputAsync()
    {
        Deactivate();
        await _send.WaitAsync().ConfigureAwait(false);
        try { await input.CloseAsync().ConfigureAwait(false); }
        finally { _send.Release(); }
    }
}
