using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentLiveProgressTests
{
    [Fact]
    public async Task CustomCli_StreamsStdoutAndStderrBeforeExit_AndObserverFailuresDoNotChangeResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryDirectory();
        var script = Path.Combine(workspace.Path, "emit.ps1");
        await File.WriteAllTextAsync(script,
            "[Console]::Out.WriteLine('live stdout'); [Console]::Out.Flush(); " +
            "[Console]::Error.WriteLine('live stderr'); [Console]::Error.Flush(); " +
            "Start-Sleep -Milliseconds 900; [Console]::Out.WriteLine('final stdout')");
        var updates = new ConcurrentQueue<AgentRunProgress>();
        var bothStreams = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AgentRunner();
        var run = runner.RunAsync(new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = "powershell.exe",
            AgentArguments = $"-NoProfile -NonInteractive -File \"{script}\""
        }, "prompt", new InlineProgress(update =>
        {
            updates.Enqueue(update);
            if (updates.Any(value => value.Stream == "stdout" && value.Text.Contains("live stdout", StringComparison.Ordinal)) &&
                updates.Any(value => value.Stream == "stderr" && value.Text.Contains("live stderr", StringComparison.Ordinal)))
                bothStreams.TrySetResult();
        }));

        await bothStreams.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Contains("final stdout", result.Output, StringComparison.Ordinal);
        Assert.Contains("live stderr", result.Error, StringComparison.Ordinal);

        var exceptionResult = await runner.RunAsync(new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = "powershell.exe",
            AgentArguments = "-NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine('ok')\""
        }, "prompt", new InlineProgress(_ => throw new InvalidOperationException("observer failure")));
        Assert.True(exceptionResult.Success, exceptionResult.Error);
        Assert.Contains("ok", exceptionResult.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexJson_ReportsReadableEventsWithoutReasoningOrRawJson_AndKeepsFinalResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryDirectory();
        var script = Path.Combine(workspace.Path, "emit.ps1");
        var host = Path.Combine(workspace.Path, "codex.exe");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        File.Copy(powershell, host);
        await File.WriteAllTextAsync(script,
            "@'\n" +
            "{\"type\":\"turn.started\"}\n" +
            "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"echo safe\"}}\n" +
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"human answer\"}}\n" +
            "{\"type\":\"reasoning\",\"text\":\"hidden reasoning marker\"}\n" +
            "{\"type\":\"auth\",\"token\":\"secret-marker\"}\n" +
            "{\"type\":\"turn.completed\"}\n" +
            "'@ | ForEach-Object { [Console]::Out.WriteLine($_); [Console]::Out.Flush(); Start-Sleep -Milliseconds 80 }");
        var updates = new ConcurrentQueue<AgentRunProgress>();
        var result = await new AgentRunner().RunAsync(new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = host,
            AgentProvider = AgentProfile.Codex,
            AgentArguments = $"-NoProfile -NonInteractive -File \"{script}\""
        }, "prompt", new InlineProgress(updates.Enqueue));

        Assert.True(result.Success, result.Error);
        Assert.Equal("human answer", result.Codex?.FinalMessage);
        var text = string.Join("\n", updates.Select(update => update.Text));
        Assert.Contains("human answer", text, StringComparison.Ordinal);
        Assert.Contains(CoreText.Get("ProgressRunningCommand", "echo safe"), text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden reasoning marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudePartialStream_PreservesWhitespaceSkipsThinkingAndAvoidsFullMessageDuplicate()
    {
        var parser = new AgentRunProgressParser(AgentProfile.Claude);
        var output = new List<AgentRunProgress>();
        output.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}"));
        output.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello \"}}}"));
        output.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hidden thought\"}}}"));
        output.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"world\"}}}"));
        output.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"name\":\"Bash\"}}}"));
        var completed = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello world\"},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"echo hi\"}}]}}";
        output.AddRange(parser.ParseLine(completed));
        output.AddRange(parser.ParseLine("{\"type\":\"result\",\"is_error\":true}"));

        Assert.Equal(new[] { "hello ", "world" }, output.Where(update => update.Stream == "stdout").Select(update => update.Text));
        Assert.DoesNotContain(output, update => update.Text.Contains("hidden thought", StringComparison.Ordinal));
        var activity = string.Concat(output.Where(update => update.Stream == "activity").Select(update => update.Text));
        Assert.Contains(CoreText.Get("ProgressUsingTool", "Bash") + Environment.NewLine, activity, StringComparison.Ordinal);
        Assert.Contains(CoreText.Get("ProgressClaudeError") + Environment.NewLine, activity, StringComparison.Ordinal);
        Assert.Equal("authoritative final", ClaudeOutputParser.Parse(completed + "\n{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"authoritative final\"}").FinalMessage);

        var fallbackParser = new AgentRunProgressParser(AgentProfile.Claude);
        fallbackParser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}");
        var fallback = fallbackParser.ParseLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"fallback text\"}]}}" );
        Assert.Contains(fallback, update => update.Stream == "activity" && update.Text == "fallback text" + Environment.NewLine);
        var toolResult = fallbackParser.ParseLine("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":[{\"type\":\"text\",\"text\":\"result line\"}]}]}}");
        Assert.Contains(toolResult, update => update.Text == CoreText.Get("ProgressToolResult", "result line") + Environment.NewLine);
        Assert.Empty(fallbackParser.ParseLine("{\"type\":\"system\",\"subtype\":\"status\"}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppServer_StreamsOnlyActiveTurnAndDoesNotDuplicateCompletedMessage(bool withDelta)
    {
        using var workspace = new TemporaryDirectory();
        var factory = new FakeAppServerFactory(async client =>
        {
            var thread = FakeAppServerClient.ThreadId;
            client.EmitStandardError("live app-server stderr");
            client.Emit("turn/started", new { threadId = thread, turnId = "active-turn" });
            client.Emit("item/agentMessage/delta", new { threadId = Guid.NewGuid().ToString("D"), turnId = "active-turn", itemId = "wrong-thread", delta = "wrong thread" });
            client.Emit("item/agentMessage/delta", new { threadId = thread, turnId = "other-turn", itemId = "wrong-turn", delta = "wrong turn" });
            client.Emit("item/agentMessage/delta", new { turnId = "active-turn", itemId = "missing-thread", delta = "missing identity" });
            client.Emit("item/started", new { threadId = thread, turnId = "active-turn", item = new { id = "cmd-1", type = "commandExecution", command = "echo safe" } });
            if (withDelta)
            {
                client.Emit("item/agentMessage/delta", new { threadId = thread, turnId = "active-turn", itemId = "message-1", delta = "hello " });
                client.Emit("item/agentMessage/delta", new { threadId = thread, turnId = "active-turn", itemId = "message-1", delta = "world" });
            }
            client.Emit("item/completed", new { threadId = thread, turnId = "active-turn", item = new { id = "message-1", type = "agentMessage", text = withDelta ? "hello world" : "fallback text" } });
            client.Emit("turn/completed", new { threadId = thread, turnId = "active-turn", turn = new { id = "active-turn", status = "completed" } });
            await Task.CompletedTask;
        });
        var updates = new ConcurrentQueue<AgentRunProgress>();
        var runner = new CodexAppServerRunner(factory);
        var result = await runner.RunAsync(new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = "codex",
            AgentProvider = AgentProfile.Codex,
            AgentMode = "app-server"
        }, "prompt", new AppServerRunOptions(Progress: new InlineProgress(updates.Enqueue)));

        Assert.True(result.Result.Success, result.Result.Error);
        Assert.Equal(withDelta ? "hello world" : "fallback text", result.Result.Output);
        var updatesArray = updates.ToArray();
        Assert.DoesNotContain(updatesArray, update => update.Text.Contains("wrong thread", StringComparison.Ordinal) ||
            update.Text.Contains("wrong turn", StringComparison.Ordinal) || update.Text.Contains("missing identity", StringComparison.Ordinal));
        Assert.Contains(CoreText.Get("ProgressRunningCommand", "echo safe") + Environment.NewLine, string.Concat(updatesArray.Where(update => update.Stream == "activity").Select(update => update.Text)), StringComparison.Ordinal);
        Assert.Contains("live app-server stderr", string.Concat(updatesArray.Where(update => update.Stream == "stderr").Select(update => update.Text)), StringComparison.Ordinal);
        var assistantText = updatesArray.Where(update => update.Stream == "stdout").Select(update => update.Text).ToArray();
        Assert.Equal(withDelta ? "hello world" : "fallback text" + Environment.NewLine, string.Concat(assistantText));
    }

    [Fact]
    public async Task AppServer_ProgressLocalizesKnownItemTypesInsteadOfShowingProtocolNames()
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            using var workspace = new TemporaryDirectory();
            var factory = new FakeAppServerFactory(client =>
            {
                var thread = FakeAppServerClient.ThreadId;
                client.Emit("turn/started", new { threadId = thread, turnId = "active-turn" });
                client.Emit("item/started", new { threadId = thread, turnId = "active-turn", item = new { id = "file-1", type = "fileChange" } });
                client.Emit("item/completed", new { threadId = thread, turnId = "active-turn", item = new { id = "file-1", type = "fileChange" } });
                client.Emit("turn/completed", new { threadId = thread, turnId = "active-turn", turn = new { id = "active-turn", status = "completed" } });
                return Task.CompletedTask;
            });
            var updates = new ConcurrentQueue<AgentRunProgress>();
            var result = await new CodexAppServerRunner(factory).RunAsync(new BridgeSettings
            {
                WorkspaceRoot = workspace.Path,
                AgentExecutable = "codex",
                AgentProvider = AgentProfile.Codex,
                AgentMode = "app-server"
            }, "prompt", new AppServerRunOptions(Progress: new InlineProgress(updates.Enqueue)));

            Assert.True(result.Result.Success, result.Result.Error);
            var activity = string.Concat(updates.Where(update => update.Stream == "activity").Select(update => update.Text));
            Assert.Contains(CoreText.Get("ProgressStartingItem", CoreText.Get("ProgressItemFileChange")), activity, StringComparison.Ordinal);
            Assert.Contains(CoreText.Get("ProgressFinishedItem", CoreText.Get("ProgressItemFileChange")), activity, StringComparison.Ordinal);
            Assert.DoesNotContain("fileChange", activity, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task AppServer_DetachesPreviousStderrProgressHandlerBeforeReusedTurn()
    {
        using var workspace = new TemporaryDirectory();
        var factory = new FakeAppServerFactory(CompleteFakeTurn("first turn stderr"));
        using var runner = new CodexAppServerRunner(factory, enableProcessReuse: true);
        var first = new ConcurrentQueue<AgentRunProgress>();
        var settings = new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = "codex",
            AgentProvider = AgentProfile.Codex,
            AgentMode = "app-server"
        };
        var result1 = await runner.RunAsync(settings, "first", new AppServerRunOptions(Progress: new InlineProgress(first.Enqueue)));
        Assert.True(result1.Result.Success, result1.Result.Error);

        factory.Client.EmitStandardError("late previous turn stderr");
        factory.OnTurnStart = CompleteFakeTurn("second turn stderr");
        var second = new ConcurrentQueue<AgentRunProgress>();
        var result2 = await runner.RunAsync(settings, "second", new AppServerRunOptions(Progress: new InlineProgress(second.Enqueue)));
        Assert.True(result2.Result.Success, result2.Result.Error);
        var secondStderr = string.Concat(second.Where(update => update.Stream == "stderr").Select(update => update.Text));
        Assert.Contains("second turn stderr", secondStderr, StringComparison.Ordinal);
        Assert.DoesNotContain("late previous turn stderr", secondStderr, StringComparison.Ordinal);
        Assert.DoesNotContain("late previous turn stderr", string.Concat(first.Where(update => update.Stream == "stderr").Select(update => update.Text)), StringComparison.Ordinal);
    }

    private static Func<FakeAppServerClient, Task> CompleteFakeTurn(string stderr)
        => client =>
        {
            var thread = FakeAppServerClient.ThreadId;
            client.Emit("turn/started", new { threadId = thread, turnId = "active-turn" });
            client.EmitStandardError(stderr);
            client.Emit("turn/completed", new { threadId = thread, turnId = "active-turn", turn = new { id = "active-turn", status = "completed" } });
            return Task.CompletedTask;
        };

    private sealed class InlineProgress(Action<AgentRunProgress> report) : IProgress<AgentRunProgress>
    {
        public void Report(AgentRunProgress value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // Windows can briefly retain the copied CLI image after process exit.
            // Retry only cleanup of this fixture's generated directory, then surface a persistent failure.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception exception) when (attempt < 19 &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class FakeAppServerFactory(Func<FakeAppServerClient, Task> onTurnStart) : IAppServerProcessFactory
    {
        public FakeAppServerClient Client { get; } = new();
        public Func<FakeAppServerClient, Task> OnTurnStart { get => Client.OnTurnStart ?? onTurnStart; set => Client.OnTurnStart = value; }
        public Task<IAppServerProcess> StartAsync(string executable, string workspace, CancellationToken cancellationToken = default)
        {
            Client.Workspace = workspace;
            Client.OnTurnStart = OnTurnStart;
            return Task.FromResult<IAppServerProcess>(new FakeAppServerProcess(Client));
        }
    }

    private sealed class FakeAppServerProcess : IAppServerProcess, IAppServerProgressSource
    {
        private readonly FakeAppServerClient _client;
        public FakeAppServerProcess(FakeAppServerClient client)
        {
            _client = client;
            client.Process = this;
        }
        public IAppServerRpcClient Client => _client;
        public event Action<string>? StandardErrorReceived;
        public Task<int?> ExitCode { get; } = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public Task<string> StandardError => Task.FromResult("");
        public void KillTree() { }
        public void EmitStandardError(string text) => StandardErrorReceived?.Invoke(text);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAppServerClient : IAppServerRpcClient
    {
        public static string ThreadId { get; } = Guid.NewGuid().ToString("D");
        public string Workspace { get; set; } = "";
        public Func<FakeAppServerClient, Task>? OnTurnStart { get; set; }
        public FakeAppServerProcess? Process { get; set; }
        public event Action<string, JsonElement>? NotificationReceived;
        public Func<AppServerServerRequest, CancellationToken, Task<AppServerServerResponse>>? ServerRequestHandler { get; set; }
        public Task<Exception?> Closed { get; } = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        {
            object response = method switch
            {
                "thread/start" => new { thread = new { id = ThreadId }, cwd = Workspace },
                "turn/start" => new { turn = new { id = "active-turn", status = "inProgress" } },
                _ => new { }
            };
            if (method == "turn/start" && OnTurnStart is not null) await OnTurnStart(this);
            return JsonSerializer.SerializeToElement(response);
        }
        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Emit(string method, object parameters) => NotificationReceived?.Invoke(method, JsonSerializer.SerializeToElement(parameters));
        public void EmitStandardError(string text) => Process?.EmitStandardError(text);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
