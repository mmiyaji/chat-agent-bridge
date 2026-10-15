using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ChatAgentBridge.Core.AppServer;

/// <summary>A bounded JSONL RPC client over Codex App Server's stdio streams.</summary>
public sealed class CodexAppServerClient : IAppServerRpcClient
{
    private const int MaxLineLength = 2 * 1024 * 1024;
    private const int MaxPendingRequests = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private int _nextId;
    private int _serverRequestCount;
    private int _disposed;

    public CodexAppServerClient(Stream input, Stream output, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanWrite || !output.CanRead) throw new ArgumentException("App Server streams must be writable input and readable output.");
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _reader = new StreamReader(output, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(input, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
        _readLoop = ReadLoopAsync(_lifetime.Token);
    }

    public event Action<string, JsonElement>? NotificationReceived;
    public Func<AppServerServerRequest, CancellationToken, Task<AppServerServerResponse>>? ServerRequestHandler { get; set; }
    public Task<Exception?> Closed => _closed.Task;

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed.Task.IsCompleted) throw new IOException("Codex App Server connection is closed.");
        var id = Interlocked.Increment(ref _nextId);
        var idKey = $"n:{id}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(idKey, completion)) throw new InvalidOperationException("Could not allocate an App Server request id.");
        try
        {
            await WriteAsync(new { id, method, @params = parameters ?? new { } }, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_requestTimeout);
            try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException($"Codex App Server request '{method}' timed out after {_requestTimeout.TotalMilliseconds:0}ms.");
            }
        }
        finally { _pending.TryRemove(idKey, out _); }
    }

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return WriteAsync(new { method, @params = parameters ?? new { } }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed.Task.IsCompleted) throw new IOException("Codex App Server connection is closed.");
            await _writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var buffer = new char[8192];
            var line = new StringBuilder();
            while (true)
            {
                var count = await _reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (line.Length > 0) await ReceiveLineAsync(line.ToString(), cancellationToken).ConfigureAwait(false);
                    break;
                }
                for (var i = 0; i < count; i++)
                {
                    var character = buffer[i];
                    if (character == '\n')
                    {
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        await ReceiveLineAsync(line.ToString(), cancellationToken).ConfigureAwait(false);
                        line.Clear();
                    }
                    else
                    {
                        line.Append(character);
                        if (line.Length > MaxLineLength) throw new InvalidDataException("Codex App Server sent a line larger than the 2 MiB protocol limit.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            var closedError = failure ?? new IOException("Codex App Server closed its output stream.");
            foreach (var entry in _pending.ToArray())
            {
                if (_pending.TryRemove(entry.Key, out var pending)) pending.TrySetException(closedError);
            }
            _closed.TrySetResult(failure);
        }
    }

    private async Task ReceiveLineAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("id", out var id) && !root.TryGetProperty("method", out _))
        {
            var idKey = MakeIdKey(id);
            if (!_pending.TryRemove(idKey, out var pending)) return;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number : 0;
                var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                    ? messageValue.GetString() : "unknown App Server error";
                pending.TrySetException(new InvalidOperationException($"Codex App Server error {code}: {message}"));
            }
            else pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }

        if (!root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String) return;
        var method = methodValue.GetString()!;
        var parameters = root.TryGetProperty("params", out var paramsValue) ? paramsValue.Clone() : default;
        if (root.TryGetProperty("id", out id))
        {
            if (Interlocked.Increment(ref _serverRequestCount) > MaxPendingRequests)
            {
                Interlocked.Decrement(ref _serverRequestCount);
                await WriteServerErrorAsync(id.Clone(), -32001, "Too many concurrent Codex server requests.", cancellationToken).ConfigureAwait(false);
                return;
            }
            _ = RespondToServerRequestAsync(new AppServerServerRequest(id.Clone(), method, parameters), cancellationToken);
            return;
        }

        try { NotificationReceived?.Invoke(method, parameters); }
        catch { /* UI observers cannot break the protocol reader. */ }
    }

    private async Task RespondToServerRequestAsync(AppServerServerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            AppServerServerResponse response;
            var handler = ServerRequestHandler;
            if (handler is null) response = AppServerServerResponse.FromError(-32601, "Unsupported server request.");
            else response = await handler(request, cancellationToken).ConfigureAwait(false);

            if (response.Error is { } error)
                await WriteServerErrorAsync(request.Id, error.Code, error.Message, cancellationToken).ConfigureAwait(false);
            else
                await WriteAsync(new { id = request.Id, result = response.Result }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { await WriteServerErrorAsync(request.Id, -32000, "The Codex server request could not be handled.", cancellationToken).ConfigureAwait(false); }
            catch { /* The process may have closed while the request was handled. */ }
        }
        finally { Interlocked.Decrement(ref _serverRequestCount); }
    }

    private Task WriteServerErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken)
        => WriteAsync(new { id, error = new { code, message } }, cancellationToken);

    private static string MakeIdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + id.GetRawText(),
        _ => "x:" + id.GetRawText()
    };
}
