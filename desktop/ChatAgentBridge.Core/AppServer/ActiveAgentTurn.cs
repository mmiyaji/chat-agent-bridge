namespace ChatAgentBridge.Core.AppServer;

/// <summary>A handle scoped to one confirmed App Server turn.</summary>
internal sealed class ActiveAgentTurn : IActiveAgentTurn
{
    private readonly IAppServerRpcClient client;
    private readonly string threadId;
    private readonly string turnId;
    private readonly TimeSpan requestLimit;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _active = 1;

    public ActiveAgentTurn(IAppServerRpcClient client, string threadId, string turnId, TimeSpan? requestLimit = null)
    {
        this.client = client;
        this.threadId = threadId;
        this.turnId = turnId;
        this.requestLimit = requestLimit ?? TimeSpan.FromSeconds(5);
    }

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public async Task<AgentTurnInputResult> AddInputAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested || Volatile.Read(ref _active) == 0)
            return AgentTurnInputResult.NotAccepted;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestLimit);
        var entered = false;
        var releaseGateOnExit = true;
        Task<System.Text.Json.JsonElement>? pendingRequest = null;
        try
        {
            await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _active) == 0 || cancellationToken.IsCancellationRequested)
                return AgentTurnInputResult.NotAccepted;

            try
            {
                pendingRequest = client.RequestAsync("turn/steer", new
                {
                    threadId,
                    input = new[] { new { type = "text", text } },
                    expectedTurnId = turnId
                }, timeout.Token);
                var response = await pendingRequest.WaitAsync(timeout.Token).ConfigureAwait(false);
                var returnedTurnId = response.ValueKind == System.Text.Json.JsonValueKind.Object
                    && response.TryGetProperty("turnId", out var turnIdValue)
                    && turnIdValue.ValueKind == System.Text.Json.JsonValueKind.String
                    ? turnIdValue.GetString()
                    : response.ValueKind == System.Text.Json.JsonValueKind.Object
                        && response.TryGetProperty("turn", out var turnValue)
                        && turnValue.ValueKind == System.Text.Json.JsonValueKind.Object
                        && turnValue.TryGetProperty("id", out turnIdValue)
                        && turnIdValue.ValueKind == System.Text.Json.JsonValueKind.String
                            ? turnIdValue.GetString() : null;
                if (returnedTurnId is null || !string.Equals(returnedTurnId, turnId, StringComparison.Ordinal))
                    return AgentTurnInputResult.DeliveryUnknown;
                return AgentTurnInputResult.Accepted;
            }
            catch (AppServerRpcException ex) when (IsExplicitRejection(ex))
            {
                return AgentTurnInputResult.NotAccepted;
            }
            catch (AppServerRpcException)
            {
                return AgentTurnInputResult.DeliveryUnknown;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Once the request may have reached the server, retrying could duplicate the user input.
                if (pendingRequest is { IsCompleted: false })
                {
                    releaseGateOnExit = false;
                    _ = ReleaseGateWhenRequestFinishesAsync(pendingRequest);
                }
                return AgentTurnInputResult.DeliveryUnknown;
            }
        }
        catch (OperationCanceledException) when (!entered)
        {
            return AgentTurnInputResult.NotAccepted;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _active) == 0)
        {
            return AgentTurnInputResult.NotAccepted;
        }
        finally
        {
            if (pendingRequest is { IsCompleted: true }) _ = pendingRequest.Exception;
            if (entered && releaseGateOnExit) _sendGate.Release();
        }
    }

    private async Task ReleaseGateWhenRequestFinishesAsync(Task<System.Text.Json.JsonElement> request)
    {
        try { await request.ConfigureAwait(false); }
        catch { /* Observe the abandoned RPC outcome; the caller already received DeliveryUnknown. */ }
        finally { _sendGate.Release(); }
    }

    private static bool IsExplicitRejection(AppServerRpcException exception)
    {
        if (exception.Code == -32601) return true;
        var message = exception.ServerMessage;
        return message.Contains("no active turn", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not belong to the active", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expectedTurnId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("turn mismatch", StringComparison.OrdinalIgnoreCase);
    }
}
