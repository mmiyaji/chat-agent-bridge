using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

[CollectionDefinition("ModelCatalogProcess", DisableParallelization = true)]
public sealed class ModelCatalogProcessCollection;

[Collection("ModelCatalogProcess")]
public sealed class ModelCatalogServiceTests
{
    [Fact]
    public async Task CodexCatalogPaginatesFiltersHiddenAndNeverStartsAThreadOrTurn()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient(async (method, parameters, _) =>
        {
            if (method == "initialize") return Result("{}");
            Assert.Equal("model/list", method);
            Assert.Equal(100, parameters!.Value.GetProperty("limit").GetInt32());
            Assert.False(parameters.Value.GetProperty("includeHidden").GetBoolean());
            if (parameters.Value.TryGetProperty("cursor", out var cursor))
            {
                Assert.Equal("page-2", cursor.GetString());
                return Result("""{"data":[{"id":"fixture-codex-large","displayName":"Fixture Codex Large","description":"Long context","isDefault":false,"hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"high","description":"Deep"},{"reasoningEffort":"xhigh","description":"Maximum"}],"defaultReasoningEffort":"high"}],"nextCursor":null}""");
            }
            return Result("""{"data":[{"id":"fixture-codex-small","displayName":"Fixture Codex Small","description":"Quick","isDefault":true,"hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast"},{"reasoningEffort":"medium","description":"Balanced"}]},{"id":"hidden","displayName":"Hidden","hidden":true}],"nextCursor":"page-2"}""");
        });
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process));

        var models = await service.GetModelsAsync(Settings(workspace.Path));

        Assert.Collection(models,
            model => { Assert.Equal("fixture-codex-small", model.Id); Assert.Equal("Fixture Codex Small (fixture-codex-small)", model.SelectionLabel); Assert.True(model.IsDefault); Assert.Equal(new[] { "low", "medium" }, model.SupportedReasoningEfforts.Select(item => item.Id)); },
            model => { Assert.Equal("fixture-codex-large", model.Id); Assert.Equal("Long context", model.Description); Assert.Equal("high", model.DefaultReasoningEffort); Assert.Equal("xhigh · Maximum", model.SupportedReasoningEfforts[1].SelectionLabel); });
        Assert.Equal(new[] { "initialize", "model/list", "model/list" }, client.RequestedMethods);
        Assert.Equal(new[] { "initialized" }, client.Notifications);
        Assert.True(client.Disposed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CatalogDoesNotInventEffortsWhenTheCliOmitsThem()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient((method, _, _) => Task.FromResult(method == "initialize"
            ? Result("{}") : Result("""{"data":[{"id":"no-effort-model"}],"nextCursor":null}""")));
        var service = new ModelCatalogService(new FakeFactory(new FakeProcess(client)));
        var model = Assert.Single(await service.GetModelsAsync(Settings(workspace.Path)));
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Null(model.DefaultReasoningEffort);
    }

    [Fact]
    public async Task CodexCatalogRejectsRepeatedCursorAndCleansUpProcess()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient((method, _, _) => Task.FromResult(method == "initialize"
            ? Result("{}") : Result("""{"data":[],"nextCursor":"same"}""")));
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process));

        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => service.GetModelsAsync(Settings(workspace.Path)));

        Assert.Contains("repeated", error.Message);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CodexCatalogFailureDoesNotExposeCliErrorTextAndDisposesChild()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient((method, _, _) => method == "initialize"
            ? Task.FromResult(Result("{}")) : Task.FromException<JsonElement>(new InvalidOperationException("api_key=do-not-leak")));
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process));

        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => service.GetModelsAsync(Settings(workspace.Path)));

        Assert.DoesNotContain("do-not-leak", error.ToString());
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CodexCatalogFailsClosedWhenResponseSchemaIsInvalid()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient((method, _, _) => Task.FromResult(method == "initialize"
            ? Result("{}") : Result("""{"data":"unexpected","nextCursor":null}""")));
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process));

        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => service.GetModelsAsync(Settings(workspace.Path)));

        Assert.Contains("invalid model catalog", error.Message);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CodexCatalogSkipsModelValuesThatCannotBePassedToTheCli()
    {
        using var workspace = new TemporaryWorkspace();
        var invalidId = new string('x', 257);
        var page = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "opaque-row-id", model = invalidId, displayName = "Invalid model value", hidden = false },
                new { id = "fixture-codex-small", model = "fixture-codex-small", displayName = "Fixture Codex Small", hidden = false }
            },
            nextCursor = (string?)null
        });
        var client = new FakeClient((method, _, _) => Task.FromResult(method == "initialize" ? Result("{}") : Result(page)));
        var service = new ModelCatalogService(new FakeFactory(new FakeProcess(client)));

        var models = await service.GetModelsAsync(Settings(workspace.Path));

        var model = Assert.Single(models);
        Assert.Equal("fixture-codex-small", model.Id);
    }

    [Fact]
    public async Task CatalogHonorsCallerCancellationAndCleansUpChild()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeClient(async (method, _, token) =>
        {
            if (method == "initialize") return Result("{}");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return default;
        });
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process));
        var task = service.GetModelsAsync(Settings(workspace.Path), cancellation.Token);
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CatalogHasAnOverallTimeoutAndCleansUpChild()
    {
        using var workspace = new TemporaryWorkspace();
        var client = new FakeClient(async (method, _, token) =>
        {
            if (method == "initialize") return Result("{}");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return default;
        });
        var process = new FakeProcess(client);
        var service = new ModelCatalogService(new FakeFactory(process), TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.GetModelsAsync(Settings(workspace.Path)));

        Assert.Contains("time limit", error.Message);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task ClaudeFixtureAnswersNestedInitializeResponseWithoutSendingAUserMessage()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_PERFORMANCE_E2E"), "1", StringComparison.Ordinal))
            return;
        var directory = Environment.GetEnvironmentVariable("CAB_PERFORMANCE_FIXTURE_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(File.Exists(Path.Combine(directory!, ".cab-offline-performance-fixture")));
        var executable = Path.Combine(directory!, "claude.exe");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Set CAB_PERFORMANCE_FIXTURE_DIRECTORY to the guarded offline fixture directory.");
        Assert.True(Path.IsPathFullyQualified(executable), "The fixture executable path must be absolute.");
        Assert.True(File.Exists(executable), $"Fixture executable was not found: {executable}");
        using var workspace = new TemporaryWorkspace();
        var settings = new BridgeSettings
        {
            WorkspaceRoot = workspace.Path,
            AgentExecutable = executable!,
            AgentProvider = AgentProfile.Claude
        };

        var models = await new ModelCatalogService().GetModelsAsync(settings).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Collection(models,
            model => { Assert.Equal("fixture-claude-small", model.Id); Assert.Equal("Fixture Claude Small", model.DisplayName); Assert.Equal(new[] { "low", "medium" }, model.SupportedReasoningEfforts.Select(item => item.Id)); Assert.Null(model.DefaultReasoningEffort); },
            model => { Assert.Equal("fixture-claude-large", model.Id); Assert.Equal("Fixture Claude Large", model.DisplayName); Assert.Equal(new[] { "high", "max" }, model.SupportedReasoningEfforts.Select(item => item.Id)); });
    }

    [Fact]
    public async Task ClaudeFixtureRejectsAnOversizedProtocolLineAndKillsTheProcess()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_PERFORMANCE_E2E"), "1", StringComparison.Ordinal))
            return;
        var directory = Environment.GetEnvironmentVariable("CAB_PERFORMANCE_FIXTURE_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(File.Exists(Path.Combine(directory!, ".cab-offline-performance-fixture")));
        var executable = Path.Combine(directory!, "claude.exe");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Set CAB_PERFORMANCE_FIXTURE_DIRECTORY to the guarded offline fixture directory.");
        Assert.True(Path.IsPathFullyQualified(executable), "The fixture executable path must be absolute.");
        var previous = Environment.GetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_OVERSIZED");
        Environment.SetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_OVERSIZED", "1");
        try
        {
            using var workspace = new TemporaryWorkspace();
            var settings = new BridgeSettings
            {
                WorkspaceRoot = workspace.Path,
                AgentExecutable = executable!,
                AgentProvider = AgentProfile.Claude
            };
            var error = await Assert.ThrowsAsync<ModelCatalogException>(() =>
                new ModelCatalogService().GetModelsAsync(settings).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("oversized", error.Message);
        }
        finally { Environment.SetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_OVERSIZED", previous); }
    }

    [Fact]
    public async Task ClaudeFixtureRejectsMismatchedNestedRequestId()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_PERFORMANCE_E2E"), "1", StringComparison.Ordinal))
            return;
        var directory = Environment.GetEnvironmentVariable("CAB_PERFORMANCE_FIXTURE_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(File.Exists(Path.Combine(directory!, ".cab-offline-performance-fixture")));
        var executable = Path.Combine(directory!, "claude.exe");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Set CAB_PERFORMANCE_FIXTURE_DIRECTORY to the guarded offline fixture directory.");
        var previous = Environment.GetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_MISMATCH");
        Environment.SetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_MISMATCH", "1");
        try
        {
            using var workspace = new TemporaryWorkspace();
            var settings = new BridgeSettings { WorkspaceRoot = workspace.Path, AgentExecutable = executable!, AgentProvider = AgentProfile.Claude };
            var error = await Assert.ThrowsAsync<ModelCatalogException>(() => new ModelCatalogService().GetModelsAsync(settings));
            Assert.Contains("mismatched", error.Message);
        }
        finally { Environment.SetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_MISMATCH", previous); }
    }

    private static BridgeSettings Settings(string workspace) => new()
    {
        WorkspaceRoot = workspace,
        AgentProvider = AgentProfile.Codex,
        AgentExecutable = "codex"
    };

    private static JsonElement Result(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-model-catalog-" + Guid.NewGuid().ToString("N"));
        public TemporaryWorkspace() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class FakeFactory(FakeProcess process) : IAppServerProcessFactory
    {
        public Task<IAppServerProcess> StartAsync(string executable, string workspace, CancellationToken cancellationToken = default)
            => Task.FromResult<IAppServerProcess>(process);
    }

    private sealed class FakeProcess(FakeClient client) : IAppServerProcess
    {
        public IAppServerRpcClient Client => client;
        public Task<int?> ExitCode => Task.FromResult<int?>(0);
        public Task<string> StandardError => Task.FromResult("");
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public void KillTree() => Killed = true;
        public async ValueTask DisposeAsync() { Disposed = true; await client.DisposeAsync(); }
    }

    private sealed class FakeClient(Func<string, JsonElement?, CancellationToken, Task<JsonElement>> handler) : IAppServerRpcClient
    {
        public event Action<string, JsonElement>? NotificationReceived { add { } remove { } }
        public Func<AppServerServerRequest, CancellationToken, Task<AppServerServerResponse>>? ServerRequestHandler { get; set; }
        public Task<Exception?> Closed => Task.FromResult<Exception?>(null);
        public List<string> RequestedMethods { get; } = [];
        public List<string> Notifications { get; } = [];
        public bool Disposed { get; private set; }
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        {
            RequestedMethods.Add(method);
            JsonElement? json = parameters is null ? null : JsonSerializer.SerializeToElement(parameters);
            return handler(method, json, cancellationToken);
        }
        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Notifications.Add(method); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
