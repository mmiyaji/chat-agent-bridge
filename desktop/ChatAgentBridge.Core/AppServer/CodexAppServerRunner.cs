using System.Text.Json;
using System.Diagnostics;

namespace ChatAgentBridge.Core.AppServer;

/// <summary>Runs one Codex turn over the native App Server JSONL protocol.</summary>
public sealed class CodexAppServerRunner : IAppServerRunner, IDisposable
{
    private const int MaxOutputCharacters = 1_000_000;
    private readonly IAppServerProcessFactory _processFactory;
    private readonly AppServerProcessPool? _pool;
    private readonly bool _ownsPool;
    private static readonly AppServerProcessPool SharedPool = new();

    public CodexAppServerRunner(IAppServerProcessFactory? processFactory = null, bool enableProcessReuse = false)
    {
        _processFactory = processFactory ?? new CodexAppServerProcessFactory();
        _pool = processFactory is null ? SharedPool : enableProcessReuse ? new AppServerProcessPool() : null;
        _ownsPool = processFactory is not null && enableProcessReuse;
    }

    /// <summary>Synchronously kills pooled children during application shutdown.</summary>
    public static void ShutdownSharedProcesses() => SharedPool.Dispose();
    public void Dispose() { if (_ownsPool) _pool?.Dispose(); }

    public async Task<AppServerRunResult> RunAsync(BridgeSettings settings, string prompt,
        AppServerRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        options ??= new AppServerRunOptions();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ArgumentException("A workspace directory is required.", nameof(settings));
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot));
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspace}");
        if (string.IsNullOrWhiteSpace(settings.AgentExecutable)) throw new ArgumentException("A Codex executable is required.", nameof(settings));
        string? model;
        string? effort;
        try
        {
            if (AgentProfile.ResolveProvider(settings) != AgentProfile.Codex)
                return new(new AgentRunResult(false, -1, "", "Codex App Server requires the Codex provider."), options.SessionId);
            model = AgentProfile.NormalizeModel(settings.AgentModel);
            effort = AgentProfile.NormalizeReasoningEffort(settings.AgentReasoningEffort);
        }
        catch (ArgumentException ex) { return new(new AgentRunResult(false, -1, "", ex.Message), options.SessionId); }

        var totalTimer = Stopwatch.StartNew();
        var queueTimer = Stopwatch.StartNew();
        using var workspaceLease = await AgentWorkspaceGate.AcquireAsync(workspace, cancellationToken).ConfigureAwait(false);
        var queueMilliseconds = queueTimer.Elapsed.TotalMilliseconds;
        AppServerProcessPool.Lease? pooledLease = null;
        IAppServerProcess process;
        IAppServerProcess? newlyStartedProcess = null;
        double? processStartMilliseconds = null;
        double? initializeMilliseconds = null;
        try
        {
            if (_pool is not null)
            {
                var key = BuildPoolKey(settings, workspace, model, effort);
                pooledLease = await _pool.AcquireAsync(key, _processFactory, settings.AgentExecutable, workspace,
                    InitializeAsync, cancellationToken).ConfigureAwait(false);
                process = pooledLease.Process;
                processStartMilliseconds = pooledLease.ProcessStartMilliseconds;
                initializeMilliseconds = pooledLease.InitializeMilliseconds;
            }
            else
            {
                var startTimer = Stopwatch.StartNew();
                process = await _processFactory.StartAsync(settings.AgentExecutable, workspace, cancellationToken).ConfigureAwait(false);
                newlyStartedProcess = process;
                processStartMilliseconds = startTimer.Elapsed.TotalMilliseconds;
                var initTimer = Stopwatch.StartNew();
                await InitializeAsync(process.Client, cancellationToken).ConfigureAwait(false);
                initializeMilliseconds = initTimer.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            if (pooledLease is not null) await pooledLease.ReleaseAsync(false).ConfigureAwait(false);
            else if (newlyStartedProcess is not null) { newlyStartedProcess.KillTree(); await newlyStartedProcess.DisposeAsync().ConfigureAwait(false); }
            return new(new AgentRunResult(false, -1, "", ex.Message), options.SessionId);
        }

        await using var processScope = new RunProcessScope(process, pooledLease);
        {
            var client = process.Client;
            var output = new BoundedText(MaxOutputCharacters);
            string? threadId = options.SessionId;
            string? turnId = null;
            var turnRequestSent = false;
            using var immediateKill = cancellationToken.Register(process.KillTree);
            TaskCompletionSource<JsonElement>? turnFinished = null;
            Exception? notificationError = null;
            var usageEvents = new List<UsageEvent>();
            var notificationHandler = (string method, JsonElement parameters) =>
            {
                try
                {
                    var eventThreadId = StringAt(parameters, "threadId");
                    if (threadId is not null && eventThreadId is not null && !SameId(threadId, eventThreadId)) return;
                    var eventTurnId = StringAt(parameters, "turnId") ?? StringAt(parameters, "turn", "id");
                    if (method == "thread/tokenUsage/updated")
                    {
                        if (threadId is not null && eventThreadId is not null && SameId(threadId, eventThreadId))
                        {
                            lock (usageEvents)
                            {
                                usageEvents.Add(new UsageEvent(eventThreadId, eventTurnId,
                                    ParseCounters(Property(parameters, "tokenUsage", "last")),
                                    ParseCounters(Property(parameters, "tokenUsage", "total")),
                                    StringAt(parameters, "model")));
                                if (usageEvents.Count > 32) usageEvents.RemoveAt(0);
                            }
                        }
                        return;
                    }
                    if (threadId is null) return;
                    if (!turnRequestSent) return;
                    if (turnId is not null && eventTurnId is not null && !SameId(turnId, eventTurnId)) return;
                    if (method == "turn/started") turnId = eventTurnId;
                    else if (turnId is null) return;
                    if (method == "item/agentMessage/delta") output.Append(StringAt(parameters, "delta"));
                    if (method == "item/commandExecution/outputDelta") output.Append(StringAt(parameters, "delta"));
                    if (method == "item/completed")
                    {
                        var item = Property(parameters, "item");
                        if (StringAt(item, "type") == "agentMessage") output.SetFinal(StringAt(item, "text"));
                    }
                    if (method is "turn/completed" or "turn/failed" && turnFinished is { } completion)
                    {
                        var completedTurn = Property(parameters, "turn");
                        if (completedTurn.ValueKind == JsonValueKind.Undefined) completedTurn = parameters;
                        if (method == "turn/failed") notificationError = new InvalidOperationException(StringAt(completedTurn, "error", "message") ?? "Codex turn failed.");
                        completion.TrySetResult(completedTurn.Clone());
                    }
                }
                catch (Exception ex) { notificationError = ex; turnFinished?.TrySetException(ex); }
            };
            client.NotificationReceived += notificationHandler;
            client.ServerRequestHandler = (request, token) => HandleServerRequestAsync(request, options, threadId, turnId, token);
            using var turnAttachment = new ClientTurnAttachment(client, notificationHandler);

            try
            {
                var threadParameters = new Dictionary<string, object?>
                {
                    ["cwd"] = workspace,
                    ["approvalPolicy"] = "on-request",
                    ["sandbox"] = "workspace-write"
                };
                var threadResponse = options.SessionId is { Length: > 0 } sessionId
                    ? await client.RequestAsync("thread/resume", Add(threadParameters, "threadId", sessionId), cancellationToken).ConfigureAwait(false)
                    : await client.RequestAsync("thread/start", threadParameters, cancellationToken).ConfigureAwait(false);
                threadId = StringAt(threadResponse, "thread", "id") ?? StringAt(threadResponse, "threadId");
                if (!Guid.TryParse(threadId, out var returnedId))
                    return Fail("Codex App Server returned a missing or invalid thread id.", options.SessionId ?? threadId);
                threadId = returnedId.ToString("D");
                var returnedCwd = StringAt(threadResponse, "cwd");
                if (returnedCwd is not null && !SamePath(returnedCwd, workspace))
                    return Fail("Codex resumed the session in a different workspace. The existing session was preserved; detach it explicitly to change workspaces.", options.SessionId ?? threadId);
                if (options.SessionId is { Length: > 0 } expected && !Guid.TryParse(expected, out _))
                    return Fail("The saved Codex session id is invalid; detach the session before continuing.", options.SessionId);
                else if (options.SessionId is { Length: > 0 } && !string.Equals(threadId, Guid.Parse(options.SessionId).ToString("D"), StringComparison.OrdinalIgnoreCase))
                    return Fail("Codex resumed a different thread than the saved session. The existing session was preserved; detach it explicitly to start a new one.", options.SessionId);

                turnFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var turnParameters = new Dictionary<string, object?>
                {
                    ["threadId"] = threadId,
                    ["input"] = new[] { new { type = "text", text = prompt } },
                    ["cwd"] = workspace,
                    ["approvalPolicy"] = "on-request",
                    ["sandboxPolicy"] = new { type = "workspaceWrite", writableRoots = new[] { workspace }, networkAccess = false }
                };
                if (model is not null) turnParameters["model"] = model;
                if (effort is not null) turnParameters["effort"] = effort;
                turnRequestSent = true;
                var turnStart = await client.RequestAsync("turn/start", turnParameters, cancellationToken).ConfigureAwait(false);
                turnId = StringAt(turnStart, "turn", "id") ?? StringAt(turnStart, "turnId");
                var completed = await WaitForTurnAsync(turnFinished.Task, process, cancellationToken).ConfigureAwait(false);
                var status = StringAt(completed, "status") ?? StringAt(completed, "state") ?? "completed";
                var isSuccess = !status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("interrupted", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
                    && notificationError is null;
                var stderr = await process.StandardError.ConfigureAwait(false);
                var turnError = StringAt(completed, "error", "message");
                var error = isSuccess ? stderr : turnError ?? (string.IsNullOrWhiteSpace(output.Value) ? stderr : output.Value);
                processScope.KeepProcess = isSuccess;
                var usage = CreateUsage(usageEvents, threadId, options.SessionId is not null, turnId);
                var result = new AgentRunResult(isSuccess, isSuccess ? 0 : -1, output.Value, error)
                {
                    Usage = usage,
                    Metrics = new AgentExecutionMetrics
                    {
                        ProcessStartMilliseconds = processStartMilliseconds,
                        InitializeMilliseconds = initializeMilliseconds,
                        TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds,
                        QueueMilliseconds = queueMilliseconds,
                        ProcessReused = pooledLease?.Reused ?? false
                    }
                };
                return new(result, threadId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (threadId is not null && turnId is not null)
                {
                    using var interruptTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    try { await client.RequestAsync("turn/interrupt", new { threadId, turnId }, interruptTimeout.Token).ConfigureAwait(false); }
                    catch { }
                }
                process.KillTree();
                throw;
            }
            catch (Exception ex)
            {
                var stderr = await Task.WhenAny(process.StandardError, Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(false) == process.StandardError
                    ? await process.StandardError.ConfigureAwait(false) : "";
                var message = string.IsNullOrWhiteSpace(stderr) ? ex.Message : $"{ex.Message}{Environment.NewLine}{stderr}";
                return new(new AgentRunResult(false, -1, output.Value, message), threadId ?? options.SessionId);
            }
        }
    }

    private static async Task<JsonElement> WaitForTurnAsync(Task<JsonElement> completed, IAppServerProcess process, CancellationToken ct)
    {
        var winner = await Task.WhenAny(completed, process.ExitCode, process.Client.Closed).WaitAsync(ct).ConfigureAwait(false);
        if (winner == completed) return await completed.ConfigureAwait(false);
        if (winner == process.ExitCode)
        {
            var exit = await process.ExitCode.ConfigureAwait(false);
            throw new IOException($"Codex App Server exited before the turn completed (exit code {exit?.ToString() ?? "unknown"}).");
        }
        var failure = await process.Client.Closed.ConfigureAwait(false);
        throw new IOException("Codex App Server connection closed before the turn completed.", failure);
    }

    private static async Task InitializeAsync(IAppServerRpcClient client, CancellationToken ct)
    {
        await client.RequestAsync("initialize", new
        {
            clientInfo = new { name = "chat-agent-bridge", title = "Chat Agent Bridge", version = "0.1.0" },
            capabilities = new { experimentalApi = true }
        }, ct).ConfigureAwait(false);
        await client.NotifyAsync("initialized", cancellationToken: ct).ConfigureAwait(false);
    }

    private static string BuildPoolKey(BridgeSettings settings, string workspace, string? model, string? effort)
    {
        static string NormalizePath(string value) => OperatingSystem.IsWindows() ? value.ToUpperInvariant() : value;
        return string.Join("\u001f", NormalizePath(workspace), NormalizePath(Path.GetFullPath(settings.AgentExecutable)),
            settings.AgentProvider, settings.AgentMode, settings.AgentArguments, model ?? "", effort ?? "");
    }

    private static TokenCounters? ParseCounters(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        static long? Read(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number >= 0 ? number : null;
        return new(Read(value, "inputTokens"), Read(value, "outputTokens"), Read(value, "cachedInputTokens"), Read(value, "cacheWriteInputTokens"));
    }

    private static AgentUsage? CreateUsage(List<UsageEvent> events, string? threadId, bool resumed, string? turnId)
    {
        UsageEvent[] snapshot;
        lock (events) snapshot = events.ToArray();
        var currentIndex = Array.FindLastIndex(snapshot, e => threadId is not null && SameId(e.ThreadId, threadId)
            && turnId is not null && e.TurnId is not null && SameId(e.TurnId, turnId));
        if (currentIndex < 0) return null;
        var current = snapshot[currentIndex];
        TokenCounters? delta = null;
        var scope = "last-request";
        if (resumed && current.Total is { } total)
        {
            for (var i = currentIndex - 1; i >= 0; i--)
                if (SameId(snapshot[i].ThreadId, threadId!) && snapshot[i].Total is { } baseline
                    && snapshot[i].TurnId is not null && !SameId(snapshot[i].TurnId!, turnId!))
                { delta = total.TrySubtract(baseline); if (delta is not null) scope = "turn"; break; }
        }
        var counters = delta ?? current.Last;
        if (counters is null) return null;
        return new AgentUsage
        {
            InputTokens = counters.Input,
            OutputTokens = counters.Output,
            CachedInputTokens = counters.Cached,
            CacheWriteInputTokens = counters.CacheWrite,
            InputIncludesCache = true,
            Scope = scope,
            Source = scope == "turn" ? "thread-total-delta" : "thread/tokenUsage/updated:last",
            Model = current.Model
        };
    }

    private static async Task<AppServerServerResponse> HandleServerRequestAsync(AppServerServerRequest request,
        AppServerRunOptions options, string? activeThreadId, string? activeTurnId, CancellationToken cancellationToken)
    {
        var p = request.Params;
        var thread = StringAt(p, "threadId") ?? "";
        var turn = StringAt(p, "turnId") ?? "";
        var item = StringAt(p, "itemId") ?? "";
        if (activeThreadId is null || !SameId(activeThreadId, thread) || string.IsNullOrWhiteSpace(turn)
            || activeTurnId is null || !SameId(activeTurnId, turn))
            return AppServerServerResponse.FromError(-32002, "The request does not belong to the active Codex turn.");
        if (request.Method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var approval = new AppServerApproval(request.Method, thread, turn, item,
                StringAt(p, "reason") ?? StringAt(p, "description") ?? request.Method,
                StringAt(p, "command") ?? StringAt(p, "commandLine"), StringAt(p, "cwd") ?? StringAt(p, "workingDirectory"));
            var decision = AppServerDecision.Decline;
            if (options.ApprovalHandler is not null)
            {
                try { decision = await options.ApprovalHandler(approval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { decision = AppServerDecision.Decline; }
                catch { decision = AppServerDecision.Decline; }
            }
            return AppServerServerResponse.FromResult(new { decision = decision == AppServerDecision.Accept ? "accept" : "decline" });
        }
        if (request.Method == "item/permissions/requestApproval")
            return AppServerServerResponse.FromResult(new { permissions = new { }, scope = "turn" });
        if (request.Method == "item/tool/requestUserInput")
        {
            var questions = ParseQuestions(Property(p, "questions"));
            if (options.InputHandler is null) return AppServerServerResponse.FromError(-32800, "User input was cancelled.");
            IReadOnlyDictionary<string, string[]>? answers;
            try { answers = await options.InputHandler(new(thread, turn, item, questions), cancellationToken).ConfigureAwait(false); }
            catch { answers = null; }
            if (answers is null) return AppServerServerResponse.FromError(-32800, "User input was cancelled.");
            var response = answers.ToDictionary(pair => pair.Key, pair => new { answers = pair.Value }, StringComparer.Ordinal);
            return AppServerServerResponse.FromResult(new { answers = response });
        }
        return AppServerServerResponse.FromError(-32601, "Unsupported Codex App Server request.");
    }

    private static IReadOnlyList<AppServerQuestion> ParseQuestions(JsonElement value)
    {
        var result = new List<AppServerQuestion>();
        if (value.ValueKind != JsonValueKind.Array) return result;
        foreach (var question in value.EnumerateArray())
        {
            var options = new List<AppServerInputOption>();
            var optionsValue = Property(question, "options");
            if (optionsValue.ValueKind == JsonValueKind.Array)
                foreach (var option in optionsValue.EnumerateArray())
                    options.Add(new(StringAt(option, "label") ?? "", StringAt(option, "description") ?? ""));
            result.Add(new(StringAt(question, "id") ?? "", StringAt(question, "header") ?? "",
                StringAt(question, "question") ?? "", BoolAt(question, "isOther"), BoolAt(question, "isSecret"), options));
        }
        return result;
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static JsonElement Property(JsonElement value, string first, string second)
        => Property(Property(value, first), second);
    private static bool BoolAt(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;
    private static bool SamePath(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), comparison);
    }
    private static bool SameId(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static string? StringAt(JsonElement value, params string[] path)
    {
        foreach (var part in path) value = Property(value, part);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static Dictionary<string, object?> Add(Dictionary<string, object?> source, string key, object? value)
    { var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal) { [key] = value }; return copy; }
    private static AppServerRunResult Fail(string error, string? sessionId)
        => new(new AgentRunResult(false, -1, "", error), sessionId);

    private sealed class BoundedText(int max)
    {
        private readonly object _sync = new();
        private readonly System.Text.StringBuilder _builder = new();
        private string? _final;
        public void Append(string? text) { if (text is null) return; lock (_sync) { var count = Math.Min(text.Length, max - _builder.Length); if (count > 0) _builder.Append(text, 0, count); } }
        public void SetFinal(string? text) { if (text is null) return; lock (_sync) _final = text.Length <= max ? text : text[..max]; }
        public string Value { get { lock (_sync) return _final ?? _builder.ToString(); } }
    }

    private sealed record UsageEvent(string ThreadId, string? TurnId, TokenCounters? Last, TokenCounters? Total, string? Model);
    private sealed record TokenCounters(long? Input, long? Output, long? Cached, long? CacheWrite)
    {
        public TokenCounters? TrySubtract(TokenCounters baseline)
        {
            static long? Diff(long? current, long? prior) => current is long c && prior is long p && c >= p ? c - p : null;
            var result = new TokenCounters(Diff(Input, baseline.Input), Diff(Output, baseline.Output), Diff(Cached, baseline.Cached), Diff(CacheWrite, baseline.CacheWrite));
            return result.Input is null && result.Output is null && result.Cached is null && result.CacheWrite is null ? null : result;
        }
    }

    private sealed class ClientTurnAttachment(IAppServerRpcClient client, Action<string, JsonElement> handler) : IDisposable
    {
        public void Dispose()
        {
            client.NotificationReceived -= handler;
            client.ServerRequestHandler = null;
        }
    }

    private sealed class RunProcessScope(IAppServerProcess process, AppServerProcessPool.Lease? pooledLease) : IAsyncDisposable
    {
        public bool KeepProcess { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (pooledLease is not null) { await pooledLease.ReleaseAsync(KeepProcess).ConfigureAwait(false); return; }
            if (!KeepProcess) process.KillTree();
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }
}
