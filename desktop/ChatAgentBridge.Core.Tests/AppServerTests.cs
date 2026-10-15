using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppServerTests
{
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
        var result = await runner.RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path, AgentModel = "gpt-5.4" }, "hello", new AppServerRunOptions(
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
            client.RaiseNotification("turn/completed", new { turn = new { id = "t", status = "failed" } });
        };
        var result = await new CodexAppServerRunner(fake).RunAsync(new BridgeSettings { WorkspaceRoot = temp.Path }, "x",
            new AppServerRunOptions(InputHandler: (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(null)));
        Assert.False(result.Result.Success);
    }

    private sealed class FakeProcessFactory : IAppServerProcessFactory
    {
        public FakeClient Client { get; } = new();
        public string? ThreadIdOverride { get; init; }
        public Task<IAppServerProcess> StartAsync(string executable, string workspace, CancellationToken cancellationToken = default)
        {
            Client.Workspace = workspace;
            Client.ThreadIdOverride = ThreadIdOverride;
            return Task.FromResult<IAppServerProcess>(new FakeProcess(Client));
        }
    }

    private sealed class FakeProcess(FakeClient client) : IAppServerProcess
    {
        public IAppServerRpcClient Client => client;
        public Task<int?> ExitCode => Task.FromResult<int?>(null);
        public Task<string> StandardError => Task.FromResult("");
        public void KillTree() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient : IAppServerRpcClient
    {
        public static string ThreadId { get; } = Guid.NewGuid().ToString("D");
        public string? ThreadIdOverride { get; set; }
        public string Workspace { get; set; } = "";
        public List<(string Method, JsonElement Parameters)> Requests { get; } = [];
        public Func<FakeClient, CancellationToken, Task>? OnTurnStart { get; set; }
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
                _ => new { }
            };
            if (method == "turn/start" && OnTurnStart is not null) await OnTurnStart(this, cancellationToken);
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
