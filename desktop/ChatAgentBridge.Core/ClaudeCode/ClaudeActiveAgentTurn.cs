using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.ClaudeCode;

internal interface IClaudeInputChannel
{
    Task<bool> WriteAsync(string line, CancellationToken cancellationToken, Action? writeAttemptStarted = null);
    Task CloseAsync();
}

/// <summary>Accepts follow-ups after serializing their write and confirms them from replay events later.</summary>
internal sealed class ClaudeActiveAgentTurn(
    IClaudeInputChannel input,
    string sessionId,
    Action<string> registerReplay,
    Action<string> removeReplay,
    Func<string, CancellationToken, Task<bool>> waitForReplay,
    Func<bool> processExited) : IQueuedActiveAgentTurn
{
    private const int MaxInputs = 100;
    private static readonly TimeSpan WriteLimit = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _active = 1;
    private int _count;

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public async Task<AgentTurnInputResult> AddInputAsync(string text, CancellationToken cancellationToken = default)
    {
        var submission = await SubmitInputAsync(text, cancellationToken).ConfigureAwait(false);
        if (submission is null) return AgentTurnInputResult.NotAccepted;
        return await submission.Confirmation.ConfigureAwait(false);
    }

    public async Task<AgentTurnInputSubmission?> SubmitInputAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _active) == 0 || cancellationToken.IsCancellationRequested)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteLimit);
        var entered = false;
        var writeAttempted = false;
        string? pendingId = null;
        try
        {
            await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _active) == 0 || processExited() || _count >= MaxInputs)
                return null;
            _count++;
            var id = pendingId = Guid.NewGuid().ToString("D");
            var payload = ClaudeStreamingRunner.SerializeUserMessage(id, sessionId, text);
            registerReplay(id);
            if (!await input.WriteAsync(payload, timeout.Token, () => writeAttempted = true).ConfigureAwait(false))
            {
                removeReplay(id);
                pendingId = null;
                return null;
            }
            pendingId = null; // The confirmation task now owns and removes this pending replay entry.
            return new AgentTurnInputSubmission(ConfirmAsync(id, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            if (pendingId is not null) removeReplay(pendingId);
            return writeAttempted ? UnknownSubmission() : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (pendingId is not null) removeReplay(pendingId);
            return writeAttempted ? UnknownSubmission() : null;
        }
        finally { if (entered) _send.Release(); }
    }

    public async Task CloseInputAsync()
    {
        Deactivate();
        await _send.WaitAsync().ConfigureAwait(false);
        try { await input.CloseAsync().ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    private async Task<AgentTurnInputResult> ConfirmAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var acknowledged = await waitForReplay(id, cancellationToken).ConfigureAwait(false);
            return acknowledged ? AgentTurnInputResult.Accepted : AgentTurnInputResult.DeliveryUnknown;
        }
        catch (OperationCanceledException) { return AgentTurnInputResult.DeliveryUnknown; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { return AgentTurnInputResult.DeliveryUnknown; }
        finally { removeReplay(id); }
    }

    private static AgentTurnInputSubmission UnknownSubmission()
        => new(Task.FromResult(AgentTurnInputResult.DeliveryUnknown));
}
