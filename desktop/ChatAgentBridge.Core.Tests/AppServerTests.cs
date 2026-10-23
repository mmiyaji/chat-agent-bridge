using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingActiveTurnInterruptsItAndNeverReusesItsProcess(bool reuseProcesses)
    {
        using var temp = new TempDirectory();
        var factory = new FakeProcessFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<AgentRunProgress>();
        factory.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turnId = "turn-1" });
            client.RaiseNotification("item/agentMessage/delta", new
            {
                threadId = FakeClient.ThreadId, turnId = "turn-1", itemId = "partial", delta = "partial before cancellation"
            });
            started.TrySetResult();
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(factory, enableProcessReuse: reuseProcesses);
        using var cancellation = new CancellationTokenSource();
        var settings = new BridgeSettings { WorkspaceRoot = temp.Path };
        var run = runner.RunAsync(settings, "cancel this request",
            new AppServerRunOptions(Progress: new CancellationProgress(progress)), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelledProcess = factory.LastProcess!;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancelledProcess.WasKilled);
        var interrupt = Assert.Single(factory.Client.Requests, request => request.Method == "turn/interrupt");
        Assert.Equal(FakeClient.ThreadId, interrupt.Parameters.GetProperty("threadId").GetString());
        Assert.Equal("turn-1", interrupt.Parameters.GetProperty("turnId").GetString());
        Assert.Contains(progress, update => update.Text == "partial before cancellation");

        factory.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turnId = "turn-1" });
            client.RaiseNotification("turn/completed", new
            {
                threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" }
            });
            return Task.CompletedTask;
        };
        var next = await runner.RunAsync(settings, "next independent request").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.Result.Success);
        Assert.Equal(2, factory.StartCount);
        Assert.NotSame(cancelledProcess, factory.LastProcess);
        Assert.False(next.Result.Metrics!.ProcessReused);
    }

    private sealed class CancellationProgress(List<AgentRunProgress> updates) : IProgress<AgentRunProgress>
    {
        public void Report(AgentRunProgress value) => updates.Add(value);
    }

    [Fact]
    public async Task ActiveTurnHandleSteersOnlyItsTurnAndBecomesStaleAtCompletion()
    {
        using var temp = new TempDirectory();
        var factory = new FakeProcessFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IActiveAgentTurn? handle = null;
        var callbackValues = new List<IActiveAgentTurn?>();
        factory.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turnId = "turn-1" });
            started.TrySetResult();
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(factory);
        var run = runner.RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "initial prompt",
            new AppServerRunOptions(ActiveTurnChanged: active => { callbackValues.Add(active); if (active is not null) handle = active; }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(handle);
        Assert.Equal(AgentTurnInputResult.Accepted, await handle!.AddInputAsync("follow-up"));

        var steer = Assert.Single(factory.Client.Requests, request => request.Method == "turn/steer");
        Assert.Equal(FakeClient.ThreadId, steer.Parameters.GetProperty("threadId").GetString());
        Assert.Equal("turn-1", steer.Parameters.GetProperty("expectedTurnId").GetString());
        var input = Assert.Single(steer.Parameters.GetProperty("input").EnumerateArray());
        Assert.Equal("text", input.GetProperty("type").GetString());
        Assert.Equal("follow-up", input.GetProperty("text").GetString());
        Assert.Equal(new[] { "expectedTurnId", "input", "threadId" }, steer.Parameters.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Single(factory.Client.Requests, request => request.Method == "turn/start");

        factory.Client.RaiseNotification("turn/completed", new
        {
            threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" }
        });
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTurnInputResult.NotAccepted, await handle.AddInputAsync("too late"));
        Assert.Contains(callbackValues, value => value is null);
        Assert.Single(factory.Client.Requests, request => request.Method == "turn/steer");
    }

    [Fact]
    public async Task ActiveTurnHandleSerializesInputsAndDistinguishesRejectedFromUnknownDelivery()
    {
        using var temp = new TempDirectory();
        var factory = new FakeProcessFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IActiveAgentTurn? handle = null;
        var inFlight = 0;
        var maxInFlight = 0;
        var concurrencyGate = new object();
        factory.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turnId = "turn-1" });
            started.TrySetResult();
            return Task.CompletedTask;
        };
        factory.Client.OnTurnSteer = async ct =>
        {
            var current = Interlocked.Increment(ref inFlight);
            lock (concurrencyGate) maxInFlight = Math.Max(maxInFlight, current);
            try { await Task.Delay(20, ct); }
            finally { Interlocked.Decrement(ref inFlight); }
        };
        using var runner = new CodexAppServerRunner(factory);
        var run = runner.RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "initial prompt",
            new AppServerRunOptions(ActiveTurnChanged: active => { if (active is not null) handle = active; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(handle);

        var serializedResults = await Task.WhenAll(
            handle!.AddInputAsync("one"), handle.AddInputAsync("two"), handle.AddInputAsync("three"));
        Assert.All(serializedResults, result => Assert.Equal(AgentTurnInputResult.Accepted, result));
        Assert.Equal(1, maxInFlight);

        factory.Client.OnTurnSteer = _ => throw new AppServerRpcException(-32601, "Method not found: turn/steer");
        Assert.Equal(AgentTurnInputResult.NotAccepted, await handle.AddInputAsync("unsupported"));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        factory.Client.OnTurnSteer = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        Assert.Equal(AgentTurnInputResult.DeliveryUnknown, await handle.AddInputAsync("possibly delivered", cancellation.Token));

        factory.Client.RaiseNotification("turn/completed", new
        {
            threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" }
        });
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TurnCompletedBeforeTurnStartResponseDoesNotPublishHandle()
    {
        using var temp = new TempDirectory();
        var factory = new FakeProcessFactory();
        var activations = new List<IActiveAgentTurn?>();
        factory.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turnId = "turn-1" });
            client.RaiseNotification("turn/completed", new
            {
                threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" }
            });
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(factory);

        var result = await runner.RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "fast prompt",
            new AppServerRunOptions(ActiveTurnChanged: active => activations.Add(active))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Result.Success);
        Assert.DoesNotContain(activations, active => active is not null);
        Assert.Contains(activations, active => active is null);
    }

    [Fact]
    public async Task ActiveTurnHandleValidatesReplyAndKeepsSerializationGateUntilTimedOutRpcSettles()
    {
        var client = new FakeClient();
        var mismatchedReplyHandle = new ActiveAgentTurn(client, FakeClient.ThreadId, "turn-1", TimeSpan.FromMilliseconds(100));
        client.TurnSteerResponse = new { turnId = "different-turn" };
        Assert.Equal(AgentTurnInputResult.DeliveryUnknown, await mismatchedReplyHandle.AddInputAsync("wrong reply"));
        mismatchedReplyHandle.Deactivate();

        client.TurnSteerResponse = new { turnId = "turn-1" };
        var heldRpc = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnTurnSteer = _ => heldRpc.Task;
        var handle = new ActiveAgentTurn(client, FakeClient.ThreadId, "turn-1", TimeSpan.FromMilliseconds(20));
        Assert.Equal(AgentTurnInputResult.DeliveryUnknown, await handle.AddInputAsync("uncertain"));

        client.OnTurnSteer = _ => Task.CompletedTask;
        var followingInput = handle.AddInputAsync("after rpc settles");
        Assert.False(followingInput.IsCompleted);
        heldRpc.SetResult();
        Assert.Equal(AgentTurnInputResult.Accepted, await followingInput.WaitAsync(TimeSpan.FromSeconds(2)));
        handle.Deactivate();
    }

    [Fact]
    public async Task ClearingIdleProcessesPreservesActiveLeaseAndAllowsFreshLaunchAndReuse()
    {
        using var temp = new TempDirectory();
        using var pool = new AppServerProcessPool();
        var factory = new FakeProcessFactory();
        var active = await pool.AcquireAsync("same-settings", factory, "codex", temp.Path,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        var firstProcess = (FakeProcess)active.Process;

        pool.ClearIdle();
        Assert.False(firstProcess.WasKilled);
        await active.ReleaseAsync(keep: true);
        pool.ClearIdle();
        Assert.True(firstProcess.WasKilled);

        var fresh = await pool.AcquireAsync("same-settings", factory, "codex", temp.Path,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        var freshProcess = fresh.Process;
        Assert.NotSame(firstProcess, freshProcess);
        Assert.False(fresh.Reused);
        await fresh.ReleaseAsync(keep: true);
        var reused = await pool.AcquireAsync("same-settings", factory, "codex", temp.Path,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Same(freshProcess, reused.Process);
        Assert.True(reused.Reused);
        Assert.Equal(2, factory.StartCount);
        await reused.ReleaseAsync(keep: true);
    }

    [Fact]
    public async Task FailedPooledTurnAndChangedModelRequireFreshProcesses()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        var count = 0;
        fake.Client.OnTurnStart = (client, _) =>
        {
            var turnId = "turn-" + (++count);
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = turnId } });
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = turnId, status = count == 2 ? "failed" : "completed" } });
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(fake, enableProcessReuse: true);
        var settings = new BridgeSettings { WorkspaceRoot = temp.Path, AgentModel = "fixture-a" };
        var first = await runner.RunAsync(settings, "one").WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await runner.RunAsync(settings, "two").WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = await runner.RunAsync(settings, "three").WaitAsync(TimeSpan.FromSeconds(5));
        var changed = await runner.RunAsync(settings with { AgentModel = "fixture-b" }, "four").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Result.Success);
        Assert.False(failure.Result.Success);
        Assert.True(failure.Result.Metrics!.ProcessReused);
        Assert.False(recovered.Result.Metrics!.ProcessReused);
        Assert.False(changed.Result.Metrics!.ProcessReused);
        Assert.Equal(3, fake.StartCount);
    }

    [Fact]
    public async Task ReusesInitializedProcessForSequentialTurnsWithSameSettings()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        var calls = 0;
        fake.Client.OnTurnStart = (client, _) =>
        {
            var turn = $"turn-{++calls}";
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = turn } });
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = turn, status = "completed" } });
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(fake, enableProcessReuse: true);
        var settings = new BridgeSettings { WorkspaceRoot = temp.Path, AgentModel = "gpt-5.4" };

        var first = await runner.RunAsync(settings, "first").WaitAsync(TimeSpan.FromSeconds(5));
        var second = await runner.RunAsync(settings, "second").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(first.Result.Success);
        Assert.True(second.Result.Success);
        Assert.Equal(1, fake.StartCount);
        Assert.Equal(1, fake.Client.Requests.Count(r => r.Method == "initialize"));
        Assert.False(first.Result.Metrics!.ProcessReused);
        Assert.True(second.Result.Metrics!.ProcessReused);
    }

    [Theory]
    [InlineData("inherit", "workspace-write", "on-request", "workspaceWrite", false)]
    [InlineData("read-only", "read-only", "never", "readOnly", false)]
    [InlineData("workspace-write", "workspace-write", "never", "workspaceWrite", true)]
    [InlineData("full-access", "danger-full-access", "never", "dangerFullAccess", false)]
    [InlineData("ask", "workspace-write", "on-request", "workspaceWrite", false)]
    public async Task PermissionModeIsAppliedToAppServerThreadAndTurn(string mode, string sandbox, string approval,
        string sandboxType, bool networkAllowed)
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        fake.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1" } });
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" } });
            return Task.CompletedTask;
        };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings
        {
            WorkspaceRoot = temp.Path,
            AgentPermissionMode = mode,
            AllowAgentNetwork = mode == "workspace-write"
        }, "prompt").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Result.Success);
        var start = fake.Client.Requests.Single(r => r.Method == "thread/start").Parameters;
        Assert.Equal(sandbox, start.GetProperty("sandbox").GetString());
        Assert.Equal(approval, start.GetProperty("approvalPolicy").GetString());
        var turn = fake.Client.Requests.Single(r => r.Method == "turn/start").Parameters;
        Assert.Equal(approval, turn.GetProperty("approvalPolicy").GetString());
        var policy = turn.GetProperty("sandboxPolicy");
        Assert.Equal(sandboxType, policy.GetProperty("type").GetString());
        if (sandboxType == "workspaceWrite")
            Assert.Equal(networkAllowed, policy.GetProperty("networkAccess").GetBoolean());
    }

    [Fact]
    public async Task PermissionChangesRequireFreshPooledProcess()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        var calls = 0;
        fake.Client.OnTurnStart = (client, _) =>
        {
            var turn = $"turn-{++calls}";
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = turn } });
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = turn, status = "completed" } });
            return Task.CompletedTask;
        };
        using var runner = new CodexAppServerRunner(fake, enableProcessReuse: true);
        var settings = new BridgeSettings { WorkspaceRoot = temp.Path };
        await runner.RunAsync(settings, "first").WaitAsync(TimeSpan.FromSeconds(5));
        var changed = await runner.RunAsync(settings with { AgentPermissionMode = "full-access" }, "second")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(changed.Result.Metrics!.ProcessReused);
        Assert.Equal(2, fake.StartCount);
    }

    [Fact]
    public async Task IgnoresUsageSnapshotsFromOtherThreadsWhenComputingResumedTurnDelta()
    {
        using var temp = new TempDirectory();
        var sessionId = Guid.NewGuid().ToString("D");
        var fake = new FakeProcessFactory { ThreadIdOverride = sessionId };
        fake.Client.OnTurnStart = (client, _) =>
        {
            client.RaiseNotification("thread/tokenUsage/updated", new
            {
                threadId = Guid.NewGuid().ToString("D"), turnId = "foreign-turn",
                tokenUsage = new { last = new { inputTokens = 1L }, total = new { inputTokens = 9000L } }
            });
            client.RaiseNotification("thread/tokenUsage/updated", new
            {
                threadId = sessionId, turnId = "previous-turn",
                tokenUsage = new { last = new { inputTokens = 70L }, total = new { inputTokens = 100L } }
            });
            client.RaiseNotification("turn/started", new { threadId = sessionId, turn = new { id = "turn-1" } });
            client.RaiseNotification("thread/tokenUsage/updated", new
            {
                threadId = sessionId, turnId = "turn-1",
                tokenUsage = new { last = new { inputTokens = 20L }, total = new { inputTokens = 120L } }
            });
            client.RaiseNotification("turn/completed", new { threadId = sessionId, turn = new { id = "turn-1", status = "completed" } });
            return Task.CompletedTask;
        };

        var result = await new CodexAppServerRunner(fake).RunAsync(
            new BridgeSettings { WorkspaceRoot = temp.Path }, "resume",
            new AppServerRunOptions(SessionId: sessionId)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Result.Success);
        Assert.Equal("turn", result.Result.Usage!.Scope);
        Assert.Equal(20, result.Result.Usage.InputTokens);
    }

    [Fact]
    public async Task RunsTurnWithApprovalAndInputAndUsesRestrictedWorkspacePolicy()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        fake.Client.OnTurnStart = async (client, ct) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1" } });
            var approval = await client.RaiseAsync("item/commandExecution/requestApproval", new
            {
                threadId = FakeClient.ThreadId, turnId = "turn-1", itemId = "cmd-1", command = "echo safe", cwd = temp.Path, reason = "test"
            }, ct);
            Assert.Equal("accept", approval.GetProperty("decision").GetString());
            var input = await client.RaiseAsync("item/tool/requestUserInput", new
            {
                threadId = FakeClient.ThreadId, turnId = "turn-1", itemId = "question-1",
                questions = new[] { new { id = "choice", header = "Choice", question = "Pick", isOther = false, isSecret = false,
                    options = new[] { new { label = "A", description = "First" } } } }
            }, ct);
            Assert.Equal("alpha", input.GetProperty("answers").GetProperty("choice").GetProperty("answers")[0].GetString());
            client.RaiseNotification("item/completed", new { threadId = FakeClient.ThreadId, turnId = "turn-1", item = new { type = "agentMessage", text = "done" } });
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "completed" } });
        };
        var runner = new CodexAppServerRunner(fake);
        var approvals = 0;
        var inputs = 0;
        var result = await runner.RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path, AgentModel = "gpt-5.4", AgentReasoningEffort = "xhigh" }, "hello", new AppServerRunOptions(
            ApprovalHandler: (a, _) => { approvals++; Assert.Equal("echo safe", a.Command); return Task.FromResult(AppServerDecision.Accept); },
            InputHandler: (q, _) => { inputs++; Assert.Equal("Pick", q.Questions[0].Question); return Task.FromResult<IReadOnlyDictionary<string, string[]>?>(new Dictionary<string, string[]> { ["choice"] = ["alpha"] }); }));

        Assert.True(result.Result.Success);
        Assert.Equal("done", result.Result.Output);
        Assert.Equal(FakeClient.ThreadId, result.SessionId);
        Assert.Equal(1, approvals);
        Assert.Equal(1, inputs);
        Assert.Contains(fake.Client.Requests, request => request.Method == "initialize");
        Assert.Contains(fake.Client.Requests, request => request.Method == "initialized");
        var start = fake.Client.Requests.Single(r => r.Method == "thread/start");
        Assert.Equal("on-request", start.Parameters.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspace-write", start.Parameters.GetProperty("sandbox").GetString());
        var turn = fake.Client.Requests.Single(r => r.Method == "turn/start");
        Assert.False(turn.Parameters.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        Assert.Equal("gpt-5.4", turn.Parameters.GetProperty("model").GetString());
        Assert.Equal("xhigh", turn.Parameters.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task MissingOrThrowingApprovalHandlerDeclinesAndFailedTurnIsNonzeroFailure()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        fake.Client.OnTurnStart = async (client, ct) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1" } });
            var response = await client.RaiseAsync("item/fileChange/requestApproval", new { threadId = FakeClient.ThreadId, turnId = "turn-1", itemId = "i" }, ct);
            Assert.Equal("decline", response.GetProperty("decision").GetString());
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "failed", error = new { message = "declined" } } });
        };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "x",
            new AppServerRunOptions(ApprovalHandler: (_, _) => throw new InvalidOperationException("dialog closed")));
        Assert.False(result.Result.Success);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Equal("declined", result.Result.Error);
    }

    [Fact]
    public async Task ResumeMismatchFailsClosedAndKeepsRequestedSession()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory { ThreadIdOverride = Guid.NewGuid().ToString("D") };
        var requested = Guid.NewGuid().ToString("D");
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "x",
            new AppServerRunOptions(SessionId: requested));
        Assert.False(result.Result.Success);
        Assert.Contains("different thread", result.Result.Error);
        Assert.Equal(requested, result.SessionId);
        Assert.DoesNotContain(fake.Client.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task InputCancellationReturnsProtocolErrorRatherThanFabricatedAnswer()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        fake.Client.OnTurnStart = async (client, ct) =>
        {
            client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1" } });
            var response = await client.RaiseAsync("item/tool/requestUserInput", new { threadId = FakeClient.ThreadId, turnId = "turn-1", itemId = "i", questions = Array.Empty<object>() }, ct);
            Assert.Equal(-32800, response.GetProperty("__errorCode").GetInt32());
            client.RaiseNotification("turn/completed", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1", status = "failed" } });
        };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "x",
            new AppServerRunOptions(InputHandler: (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Result.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnStartFailure_reportsWhetherPromptWasAccepted(bool accepted)
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory();
        fake.Client.OnTurnStart = (client, _) =>
        {
            if (accepted)
                client.RaiseNotification("turn/started", new { threadId = FakeClient.ThreadId, turn = new { id = "turn-1" } });
            throw new InvalidOperationException("turn/start failed");
        };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "prompt");
        Assert.False(result.Result.Success);
        Assert.Equal(FakeClient.ThreadId, result.SessionId);
        Assert.Equal(accepted, result.PromptAccepted);
    }

    [Fact]
    public async Task WrongWorkspaceBeforeTurn_reportsPromptNotAccepted()
    {
        using var temp = new TempDirectory();
        var fake = new FakeProcessFactory { ReturnedWorkspace = Path.Combine(temp.Path, "wrong-workspace") };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "prompt");
        Assert.False(result.Result.Success);
        Assert.False(result.PromptAccepted);
        Assert.DoesNotContain(fake.Client.Requests, request => request.Method == "turn/start");
    }

    private sealed class FakeProcessFactory : IAppServerProcessFactory
    {
        public FakeClient Client { get; } = new();
        public string? ThreadIdOverride { get; init; }
        public string? ReturnedWorkspace { get; init; }
        public int StartCount { get; private set; }
        public FakeProcess? LastProcess { get; private set; }
        public Task<IAppServerProcess> StartAsync(string executable, string workspace, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Client.Workspace = ReturnedWorkspace ?? workspace;
            Client.ThreadIdOverride = ThreadIdOverride;
            LastProcess = new FakeProcess(Client);
            return Task.FromResult<IAppServerProcess>(LastProcess);
        }
    }

    private sealed class FakeProcess(FakeClient client) : IAppServerProcess
    {
        public bool WasKilled { get; private set; }
        public IAppServerRpcClient Client => client;
        public Task<int?> ExitCode { get; } = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public Task<string> StandardError => Task.FromResult("");
        public void KillTree() => WasKilled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient : IAppServerRpcClient
    {
        public static string ThreadId { get; } = Guid.NewGuid().ToString("D");
        public string? ThreadIdOverride { get; set; }
        public string Workspace { get; set; } = "";
        public List<(string Method, JsonElement Parameters)> Requests { get; } = [];
        public Func<FakeClient, CancellationToken, Task>? OnTurnStart { get; set; }
        public Func<CancellationToken, Task>? OnTurnSteer { get; set; }
        public object TurnSteerResponse { get; set; } = new { turnId = "turn-1" };
        public event Action<string, JsonElement>? NotificationReceived;
        public Func<AppServerServerRequest, CancellationToken, Task<AppServerServerResponse>>? ServerRequestHandler { get; set; }
        public Task<Exception?> Closed { get; } = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToElement(parameters ?? new { });
            Requests.Add((method, json));
            object response = method switch
            {
                "thread/start" or "thread/resume" => new { thread = new { id = ThreadIdOverride ?? ThreadId }, cwd = Workspace },
                "turn/start" => new { turn = new { id = "turn-1", status = "inProgress" } },
                "turn/steer" => TurnSteerResponse,
                _ => new { }
            };
            if (method == "turn/start" && OnTurnStart is not null) await OnTurnStart(this, cancellationToken);
            if (method == "turn/steer" && OnTurnSteer is not null) await OnTurnSteer(cancellationToken);
            return JsonSerializer.SerializeToElement(response);
        }
        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Requests.Add((method, JsonSerializer.SerializeToElement(parameters ?? new { }))); return Task.CompletedTask; }
        public async Task<JsonElement> RaiseAsync(string method, object parameters, CancellationToken ct)
        {
            var request = new AppServerServerRequest(JsonSerializer.SerializeToElement("server-id"), method, JsonSerializer.SerializeToElement(parameters));
            Assert.NotNull(ServerRequestHandler);
            var response = await ServerRequestHandler!(request, ct);
            if (response.Error is { } error) return JsonSerializer.SerializeToElement(new { __errorCode = error.Code });
            return JsonSerializer.SerializeToElement(response.Result);
        }
        public void RaiseNotification(string method, object parameters) => NotificationReceived?.Invoke(method, JsonSerializer.SerializeToElement(parameters));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-appserver-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
