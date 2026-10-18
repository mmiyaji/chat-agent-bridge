using System.Collections.Concurrent;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class WorkspaceRunnerTests
{
    [Fact]
    public async Task SessionAgentRunner_UsesApprovedWorkspaceAndStartsFreshSessionForSelectedOwner()
    {
        using var temp = new TempDirectory();
        var original = Directory.CreateDirectory(Path.Combine(temp.Path, "original")).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "approved")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var selected = Key("alice");
        var other = Key("bob");
        var oldId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        await store.SaveAsync(Binding(selected, original, oldId));
        await store.SaveAsync(Binding(other, original, otherId));
        var settings = Settings(original) with { TrustedWorkspaceRoots = temp.Path };
        await store.ChangeWorkspaceAsync(selected, workspace, settings);
        var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(newId)));

        var result = await new SessionAgentRunner(store, fake).RunAsync(selected, settings, "new workspace turn");

        Assert.True(result.Success);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(workspace, call.Settings.WorkspaceRoot);
        Assert.Equal(temp.Path, call.Settings.TrustedWorkspaceRoots);
        Assert.Equal("exec --json -", call.Settings.AgentArguments);
        Assert.Equal(newId.ToString("D"), (await store.FindAsync(selected))!.SessionId);
        Assert.Equal(otherId.ToString("D"), (await store.FindAsync(other))!.SessionId);
        Assert.Equal(workspace, await store.FindWorkspaceAsync(selected));
    }

    [Fact]
    public async Task AppServerSessionRunner_UsesApprovedWorkspaceAndStartsFreshSessionForSelectedOwner()
    {
        using var temp = new TempDirectory();
        var original = Directory.CreateDirectory(Path.Combine(temp.Path, "original")).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "approved")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var selected = Key("alice");
        var other = Key("bob");
        var oldId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        await store.SaveAsync(Binding(selected, original, oldId, appServer: true));
        await store.SaveAsync(Binding(other, original, otherId, appServer: true));
        var settings = Settings(original) with { AgentMode = "app-server", TrustedWorkspaceRoots = temp.Path };
        await store.ChangeWorkspaceAsync(selected, workspace, settings);
        var fake = new FakeAppServerRunner(newId.ToString("D"));

        var result = await new AppServerSessionRunner(store, fake).RunAsync(selected, settings, "new workspace turn");

        Assert.True(result.Success);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(workspace, call.Settings.WorkspaceRoot);
        Assert.Equal(temp.Path, call.Settings.TrustedWorkspaceRoots);
        Assert.Null(call.Options?.SessionId);
        Assert.Equal(newId.ToString("D"), (await store.FindAsync(selected))!.SessionId);
        Assert.Equal(otherId.ToString("D"), (await store.FindAsync(other))!.SessionId);
        Assert.Equal(workspace, await store.FindWorkspaceAsync(selected));
    }

    [Fact]
    public async Task BothSessionRunners_RejectRevokedExplicitWorkspaceTrustBeforeInvocation()
    {
        using var temp = new TempDirectory();
        var original = Directory.CreateDirectory(Path.Combine(temp.Path, "original")).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(original, "approved")).FullName;
        var revokedRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "revoked")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var key = Key("alice");
        var settings = Settings(original) with { TrustedWorkspaceRoots = original };
        await store.ChangeWorkspaceAsync(key, workspace, settings);
        var revoked = settings with { TrustedWorkspaceRoots = revokedRoot };
        var exec = new FakeAgentRunner((_, _) => Task.FromResult(Success(Guid.NewGuid())));
        var appServer = new FakeAppServerRunner(Guid.NewGuid().ToString("D"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SessionAgentRunner(store, exec).RunAsync(key, revoked, "must not run"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AppServerSessionRunner(store, appServer).RunAsync(
            key, revoked with { AgentMode = "app-server" }, "must not run"));

        Assert.Empty(exec.Calls);
        Assert.Empty(appServer.Calls);
    }

    private static BridgeSettings Settings(string workspace) => new()
    {
        AgentMode = "exec", WorkspaceRoot = workspace, AgentExecutable = "codex", AgentArguments = "exec --json -"
    };

    private static SessionKey Key(string user) => new("slack", "channel-1", "thread-1", user, "slack");

    private static SessionBinding Binding(SessionKey key, string workspace, Guid id, bool appServer = false) => new(
        key, workspace, "codex", appServer ? "app-server" : "exec --json -", id.ToString("D"), DateTimeOffset.UtcNow);

    private static AgentRunResult Success(Guid id) => new(true, 0,
        $"{{\"type\":\"thread.started\",\"thread_id\":\"{id:D}\"}}\n{{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":\"done\"}}}}", "");

    private sealed class FakeAgentRunner(Func<BridgeSettings, string, Task<AgentRunResult>> run) : IAgentRunner
    {
        private readonly ConcurrentQueue<(BridgeSettings Settings, string Prompt)> _calls = new();
        public (BridgeSettings Settings, string Prompt)[] Calls => _calls.ToArray();
        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue((settings, prompt));
            return run(settings, prompt);
        }
    }

    private sealed class FakeAppServerRunner(string sessionId) : IAppServerRunner
    {
        public List<(BridgeSettings Settings, string Prompt, AppServerRunOptions? Options)> Calls { get; } = [];
        public Task<AppServerRunResult> RunAsync(BridgeSettings settings, string prompt, AppServerRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((settings, prompt, options));
            return Task.FromResult(new AppServerRunResult(new AgentRunResult(true, 0, "completed", ""), sessionId));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
