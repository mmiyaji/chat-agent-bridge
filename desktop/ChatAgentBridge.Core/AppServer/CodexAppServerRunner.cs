using System.Text.Json;

namespace ChatAgentBridge.Core.AppServer;

/// <summary>Runs one Codex turn over the native App Server JSONL protocol.</summary>
public sealed class CodexAppServerRunner : IAppServerRunner
{
    private const int MaxOutputCharacters = 1_000_000;
    private readonly IAppServerProcessFactory _processFactory;

    public CodexAppServerRunner(IAppServerProcessFactory? processFactory = null)
        => _processFactory = processFactory ?? new CodexAppServerProcessFactory();

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

        using var workspaceLease = await AgentWorkspaceGate.AcquireAsync(workspace, cancellationToken).ConfigureAwait(false);
        IAppServerProcess process;
        try { process = await _processFactory.StartAsync(settings.AgentExecutable, workspace, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { return new(new AgentRunResult(false, -1, "", ex.Message), options.SessionId); }

        await using (process.ConfigureAwait(false))
        {
            var client = process.Client;
            var output = new BoundedText(MaxOutputCharacters);
            string? threadId = null;
            string? turnId = null;
            using var immediateKill = cancellationToken.Register(process.KillTree);
            TaskCompletionSource<JsonElement>? turnFinished = null;
            Exception? notificationError = null;
            client.NotificationReceived += (method, parameters) =>
            {
                try
                {
                    var eventThreadId = StringAt(parameters, "threadId");
                    if (threadId is not null && eventThreadId is not null && !SameId(threadId, eventThreadId)) return;
                    var eventTurnId = StringAt(parameters, "turnId") ?? StringAt(parameters, "turn", "id");
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
            client.ServerRequestHandler = (request, token) => HandleServerRequestAsync(request, options, threadId, turnId, token);

            try
            {
                await client.RequestAsync("initialize", new
                {
                    clientInfo = new { name = "chat-agent-bridge", title = "Chat Agent Bridge", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                }, cancellationToken).ConfigureAwait(false);
                await client.NotifyAsync("initialized", cancellationToken: cancellationToken).ConfigureAwait(false);

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
                var turnParameters = new
                {
                    threadId,
                    input = new[] { new { type = "text", text = prompt } },
                    cwd = workspace,
                    approvalPolicy = "on-request",
                    sandboxPolicy = new { type = "workspaceWrite", writableRoots = new[] { workspace }, networkAccess = false }
                };
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
                return new(new AgentRunResult(isSuccess, isSuccess ? 0 : -1, output.Value, error), threadId);
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
}
