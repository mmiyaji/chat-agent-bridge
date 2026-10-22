using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.ClaudeCode;

/// <summary>Runs a Claude exec turn over its stream-json input channel and accepts bounded live follow-ups.</summary>
internal static class ClaudeStreamingRunner
{
    private const int MaxLineCharacters = 2 * 1024 * 1024;
    private const int MaxCapturedCharacters = 1_000_000;
    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(5);

    // A null result means the CLI exited before any user message could be written. The caller may safely
    // retry with its established one-shot invocation because no prompt reached Claude.
    internal static async Task<AgentRunResult?> TryRunAsync(AgentInvocation invocation,
        string executable, string? nodeEntryPoint, string workspace, string prompt, IProgress<AgentRunProgress>? progress,
        Action<IActiveAgentTurn?> activeTurnChanged, CancellationToken cancellationToken)
    {
        var sessionId = ReadResumeId(invocation.Arguments) ?? Guid.NewGuid().ToString("D");
        var arguments = invocation.Arguments.Concat(new[]
        {
            "--input-format", "stream-json", "--replay-user-messages"
        }).ToList();
        if (ReadResumeId(invocation.Arguments) is null)
        {
            arguments.Add("--session-id");
            arguments.Add(sessionId);
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        if (nodeEntryPoint is not null) start.ArgumentList.Add(nodeEntryPoint);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var started = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) return new AgentRunResult(false, -1, "", "Claude Code CLI did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AgentRunResult(false, -1, "", ex.Message);
        }
        var processStartMilliseconds = started.Elapsed.TotalMilliseconds;

        var input = new InputChannel(process.StandardInput);
        var reader = new ProtocolReader(process.StandardOutput, input, progress, sessionId);
        var stderrTask = DrainStderrAsync(process.StandardError);
        using var killOnCancellation = cancellationToken.Register(() => Kill(process));
        var firstUserWriteAttempted = false;
        ClaudeActiveAgentTurn? activeTurn = null;
        try
        {
            _ = reader.ReadTask;
            var requestId = Guid.NewGuid().ToString("D");
            reader.SetInitializeRequestId(requestId);
            var initialize = JsonSerializer.Serialize(new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "initialize", hooks = new { } }
            });
            await input.WriteAsync(initialize, cancellationToken).ConfigureAwait(false);

            var initialized = await WaitProtocolAsync(reader.Initialized.Task, process, cancellationToken).ConfigureAwait(false);
            if (!initialized)
            {
                await StopAsync(process).ConfigureAwait(false);
                await reader.ReadTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                return null;
            }

            var initialId = Guid.NewGuid().ToString("D");
            reader.RegisterReplay(initialId);
            var initialMessage = SerializeUserMessage(initialId, sessionId, prompt);
            firstUserWriteAttempted = true;
            await input.WriteAsync(initialMessage, cancellationToken).ConfigureAwait(false);
            var accepted = await WaitProtocolAsync(reader.WaitForReplayAsync(initialId, cancellationToken), process, cancellationToken).ConfigureAwait(false);
            reader.RemoveReplay(initialId);
            if (!accepted)
            {
                Kill(process);
                await BoundedCleanupAsync(process, reader.ReadTask, stderrTask).ConfigureAwait(false);
                return FailResult("Claude Code did not acknowledge the initial prompt for the requested session.");
            }

            activeTurn = new ClaudeActiveAgentTurn(input, sessionId, reader.RegisterReplay, reader.RemoveReplay, reader.WaitForReplayAsync,
                () => process.HasExited);
            reader.BindActiveTurn(activeTurn, activeTurnChanged);
            var processExit = process.WaitForExitAsync(cancellationToken);
            var terminal = await Task.WhenAny(reader.FirstResult.Task, processExit).ConfigureAwait(false);
            if (terminal == reader.FirstResult.Task)
            {
                activeTurn.Deactivate();
                SafeNotify(activeTurnChanged, null);
                await activeTurn.CloseInputAsync().ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
                await processExit.ConfigureAwait(false);
            await reader.ReadTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            activeTurn.Deactivate();
            SafeNotify(activeTurnChanged, null);

            var output = reader.Snapshot();
            var success = process.ExitCode == 0 && output.Claude is { HasResult: true, TurnFailed: false };
            var error = success ? "" : process.ExitCode != 0
                ? $"Claude Code CLI failed with exit code {process.ExitCode}."
                : output.Claude?.HasResult != true
                    ? "Claude Code exited without a terminal result event."
                    : "Claude Code reported a failed result event.";
            return output with
            {
                Success = success,
                ExitCode = success ? process.ExitCode : process.ExitCode == 0 ? -1 : process.ExitCode,
                Error = error,
                Metrics = new AgentExecutionMetrics { ProcessStartMilliseconds = processStartMilliseconds }
            };
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await BoundedCleanupAsync(process, reader.ReadTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            Kill(process);
            await BoundedCleanupAsync(process, reader.ReadTask, stderrTask).ConfigureAwait(false);
            if (!firstUserWriteAttempted)
                return null;
            return FailResult("Claude Code stream-json input ended before the turn completed.");
        }
        finally
        {
            activeTurn?.Deactivate();
            SafeNotify(activeTurnChanged, null);
            if (!process.HasExited) Kill(process);
            await BoundedCleanupAsync(process, reader.ReadTask, stderrTask).ConfigureAwait(false);
        }

        AgentRunResult FailResult(string error)
            => new(false, -1, "", error);
    }

    private static async Task<bool> WaitProtocolAsync(Task<bool> signal, Process process, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProtocolTimeout);
        try
        {
            var exited = process.WaitForExitAsync(timeout.Token);
            var completed = await Task.WhenAny(signal, exited).ConfigureAwait(false);
            if (completed == signal) return await signal.ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    internal static string SerializeUserMessage(string id, string sessionId, string text)
        => JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = text },
            parent_tool_use_id = (string?)null,
            session_id = sessionId,
            uuid = id
        });

    private static string? ReadResumeId(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
            if (arguments[i] == "--resume") return Guid.TryParse(arguments[i + 1], out var id) ? id.ToString("D") : null;
        return null;
    }

    private static async Task<string> DrainStderrAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
        }
        // Deliberately discard raw stderr, which can contain paths and provider/account details.
        return "";
    }

    private static async Task BoundedCleanupAsync(Process process, Task reader, Task stderr)
    {
        var exited = process.WaitForExitAsync();
        try { await exited.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { ObserveLate(exited); }
        var drains = Task.WhenAll(reader, stderr);
        try { await drains.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { ObserveLate(drains); }
    }

    private static void ObserveLate(Task task)
        => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task StopAsync(Process process)
    {
        Kill(process);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void SafeNotify(Action<IActiveAgentTurn?> callback, IActiveAgentTurn? turn)
    {
        try { callback(turn); } catch { /* UI callbacks must not leak a running CLI process. */ }
    }

    private sealed class InputChannel(StreamWriter writer) : IClaudeInputChannel
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _closed;
        public async Task<bool> WriteAsync(string line, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_closed) return false;
                await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
                return true;
            }
            finally { _gate.Release(); }
        }
        public async Task CloseAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (!_closed) { _closed = true; writer.Close(); } }
            finally { _gate.Release(); }
        }
    }

    internal sealed class ProtocolReader
    {
        private readonly StreamReader _stdout;
        private readonly IClaudeInputChannel _input;
        private readonly IProgress<AgentRunProgress>? _progress;
        private readonly string _expectedSessionId;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _replays = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _resultTexts = [];
        private readonly List<AgentUsage?> _usage = [];
        private readonly AgentRunProgressParser _progressParser = new(AgentProfile.Claude);
        private string? _sessionId;
        private string? _initializeRequestId;
        private bool _badProtocol;
        private bool _resultFailed;
        private bool _outputTruncated;
        private int _resultCount;
        private readonly object _turnGate = new();
        private bool _terminalSeen;
        private ClaudeActiveAgentTurn? _activeTurn;
        private Action<IActiveAgentTurn?>? _activeTurnChanged;

        internal ProtocolReader(StreamReader stdout, IClaudeInputChannel input, IProgress<AgentRunProgress>? progress, string expectedSessionId)
        {
            _stdout = stdout;
            _input = input;
            _progress = progress;
            _expectedSessionId = expectedSessionId;
            _readTask = new Lazy<Task>(ReadAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private readonly Lazy<Task> _readTask;
        internal Task ReadTask => _readTask.Value;
        internal TaskCompletionSource<bool> Initialized => _initialized;
        internal TaskCompletionSource<bool> FirstResult => _firstResult;
        internal void SetInitializeRequestId(string requestId) => _initializeRequestId = requestId;
        internal void BindActiveTurn(ClaudeActiveAgentTurn activeTurn, Action<IActiveAgentTurn?> changed)
        {
            lock (_turnGate)
            {
                if (_terminalSeen) { activeTurn.Deactivate(); return; }
                _activeTurn = activeTurn;
                _activeTurnChanged = changed;
                SafeNotify(changed, activeTurn);
            }
        }

        internal void RegisterReplay(string id)
        {
            if (_replays.Count >= 101 || !_replays.TryAdd(id, new(TaskCreationOptions.RunContinuationsAsynchronously)))
                throw new InvalidOperationException("Too many pending Claude input acknowledgements.");
        }

        internal void RemoveReplay(string id) => _replays.TryRemove(id, out _);

        internal async Task<bool> WaitForReplayAsync(string id, CancellationToken token)
            => _replays.TryGetValue(id, out var ack) && await ack.Task.WaitAsync(token).ConfigureAwait(false);

        private async Task ReadAsync()
        {
            var line = new StringBuilder();
            var buffer = new char[8192];
            var dropping = false;
            while (true)
            {
                var read = await _stdout.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) break;
                for (var i = 0; i < read; i++)
                {
                    var ch = buffer[i];
                    if (ch == '\n')
                    {
                        if (!dropping) await ProcessLineAsync(line.ToString().TrimEnd('\r')).ConfigureAwait(false);
                        line.Clear(); dropping = false;
                    }
                    else if (!dropping)
                    {
                        if (line.Length >= MaxLineCharacters) { dropping = true; line.Clear(); _badProtocol = true; }
                        else line.Append(ch);
                    }
                }
            }
            if (!dropping && line.Length > 0) await ProcessLineAsync(line.ToString()).ConfigureAwait(false);
            _initialized.TrySetResult(false);
            foreach (var replay in _replays.Values) replay.TrySetResult(false);
            if (!_firstResult.Task.IsCompleted) _firstResult.TrySetResult(false);
        }

        private async Task ProcessLineAsync(string text)
        {
            if (text.Length == 0 || text.Length > MaxLineCharacters) { _badProtocol = true; return; }
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type)) { _badProtocol = true; return; }
                if (type == "control_request") { await RespondToControlAsync(root).ConfigureAwait(false); return; }
                if (type == "control_response")
                {
                    var response = Property(root, "response");
                    if (String(response, "request_id") is { } requestId && requestId == _initializeRequestId && String(response, "subtype") == "success")
                    {
                        // Initialization request id is set as the sole pending initialization.
                        _initialized.TrySetResult(true);
                    }
                    else if (String(response, "request_id") == _initializeRequestId && String(response, "subtype") == "error") _initialized.TrySetResult(false);
                    return;
                }
                if (String(root, "session_id") is { } sessionId)
                {
                    if (!Guid.TryParse(sessionId, out var parsed) || !string.Equals(parsed.ToString("D"), _expectedSessionId, StringComparison.OrdinalIgnoreCase))
                    { _badProtocol = true; return; }
                    _sessionId = parsed.ToString("D");
                }
                if (type == "user" && String(root, "uuid") is { } uuid && String(root, "session_id") is { } ackSession &&
                    Property(root, "isReplay").ValueKind == JsonValueKind.True)
                {
                    if (string.Equals(ackSession, _expectedSessionId, StringComparison.OrdinalIgnoreCase) &&
                        _replays.TryGetValue(uuid, out var replay)) replay.TrySetResult(true);
                }
                if (type == "error") _resultFailed = true;
                if (type == "result")
                {
                    if (_resultCount >= 101) { _badProtocol = true; DeactivateTurn(); return; }
                    _resultCount++;
                    if (String(root, "session_id") is not { } resultSession ||
                        !string.Equals(resultSession, _expectedSessionId, StringComparison.OrdinalIgnoreCase))
                        _badProtocol = true;
                    var resultText = String(root, "result") ?? "";
                    var currentLength = _resultTexts.Sum(value => value.Length);
                    if (currentLength < MaxCapturedCharacters)
                        _resultTexts.Add(resultText[..Math.Min(resultText.Length, MaxCapturedCharacters - currentLength)]);
                    else if (resultText.Length > 0) _outputTruncated = true;
                    if (resultText.Length > Math.Max(0, MaxCapturedCharacters - currentLength)) _outputTruncated = true;
                    if (String(root, "subtype") != "success" || Property(root, "is_error").ValueKind == JsonValueKind.True)
                        _resultFailed = true;
                    var resultParser = new ClaudeOutputParser.Accumulator();
                    resultParser.AddLine(text);
                    // The SDK defines result.usage as per-turn. modelUsage can include cumulative
                    // session spend, so only its model identifier is retained by the parser.
                    _usage.Add(resultParser.Snapshot().Usage);
                    if (_firstResult.TrySetResult(true)) DeactivateTurn();
                }
                foreach (var update in _progressParser.ParseLine(text))
                    try { _progress?.Report(update); } catch { }
            }
            catch (JsonException) { _badProtocol = true; }
        }

        private void DeactivateTurn()
        {
            ClaudeActiveAgentTurn? turn;
            Action<IActiveAgentTurn?>? changed;
            lock (_turnGate)
            {
                _terminalSeen = true;
                turn = _activeTurn;
                changed = _activeTurnChanged;
                _activeTurn = null;
                _activeTurnChanged = null;
            }
            turn?.Deactivate();
            if (changed is not null) SafeNotify(changed, null);
        }

        private async Task RespondToControlAsync(JsonElement root)
        {
            var id = String(root, "request_id");
            var request = Property(root, "request");
            if (id is null) { _badProtocol = true; return; }
            var subtype = String(request, "subtype");
            string response;
            if (subtype == "can_use_tool")
            {
                response = JsonSerializer.Serialize(new
                {
                    type = "control_response",
                    response = new { subtype = "success", request_id = id, response = new { behavior = "deny", message = "Tool approval is not available through live follow-up." } }
                });
            }
            else
            {
                _badProtocol = true;
                response = JsonSerializer.Serialize(new
                {
                    type = "control_response",
                    response = new { subtype = "error", request_id = id, error = "Unsupported Claude control request." }
                });
            }
            try { await _input.WriteAsync(response, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { _badProtocol = true; }
        }

        internal AgentRunResult Snapshot()
        {
            var text = string.Join(Environment.NewLine + Environment.NewLine, _resultTexts.Where(value => value.Length > 0));
            var usage = SumUsage(_usage);
            var claude = new ClaudeOutput(_sessionId, text,
                _badProtocol || _resultFailed, HasEvents: true)
            { HasResult = _resultCount > 0, Usage = usage };
            return new AgentRunResult(false, -1, text, "", HasOutputTruncated: _outputTruncated)
            { Claude = claude, Usage = usage };
        }

        private static AgentUsage? SumUsage(IReadOnlyList<AgentUsage?> values)
        {
            if (values.Count == 0 || values.Any(value => value is null)) return null;
            var measured = values.Select(value => value!).ToArray();
            static long? Sum(IEnumerable<long?> counters)
            {
                var present = counters.Where(value => value is not null).Select(value => value!.Value).ToArray();
                if (present.Length == 0 || present.Length != counters.Count()) return null;
                try { return present.Aggregate(0L, checked((total, value) => total + value)); }
                catch (OverflowException) { return null; }
            }
            return new AgentUsage
            {
                InputTokens = Sum(measured.Select(value => value.InputTokens)), OutputTokens = Sum(measured.Select(value => value.OutputTokens)),
                CachedInputTokens = Sum(measured.Select(value => value.CachedInputTokens)), CacheWriteInputTokens = Sum(measured.Select(value => value.CacheWriteInputTokens)),
                InputIncludesCache = false, Scope = "turn", Source = "claude.stream-json", Model = measured.Select(value => value.Model).Distinct().Count() == 1 ? measured[0].Model : null
            };
        }

        private static bool TryString(JsonElement element, string name, out string? value)
        {
            value = String(element, name);
            return value is not null;
        }
        private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
        private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    }
}
