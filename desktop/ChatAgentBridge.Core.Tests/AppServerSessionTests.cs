using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppServerSessionTests
{
    [Fact]
    public async Task SessionRunnerPersistsMarkerAndResumesSavedId()
    {
        using var temp = new TempDirectory();
        var store = new SessionStore(temp.Path);
        var key = Key();
        var id = Guid.NewGuid();
        var fake = new FakeRunner(id.ToString("D"), id.ToString("D"));
        var runner = new AppServerSessionRunner(store, fake);

        Assert.True((await runner.RunAsync(key, Settings(temp.Path), "first" )).Success);
        var saved = await store.FindAsync(key);
        Assert.Equal("app-server", saved!.AgentArguments);
        Assert.Equal(id.ToString("D"), saved.SessionId);

        Assert.True((await runner.RunAsync(key, Settings(temp.Path), "follow-up")).Success);
        Assert.Null(fake.Calls[0].Options?.SessionId);
        Assert.Equal(id.ToString("D"), fake.Calls[1].Options?.SessionId);
        Assert.Equal("follow-up", fake.Calls[1].Prompt);
        Assert.Equal("app-server", fake.Calls[1].Settings.AgentMode);
        Assert.Equal(id.ToString("D"), (await store.FindAsync(key))!.SessionId);
    }

    [Fact]
    public async Task SessionRunnerPreservesBindingWhenReturnedIdIsMissingOrDifferent()
    {
        using var temp = new TempDirectory();
        var store = new SessionStore(temp.Path);
        var key = Key();
        var id = Guid.NewGuid();
        var fake = new FakeRunner(id.ToString("D"), null, Guid.NewGuid().ToString("D"));
        var runner = new AppServerSessionRunner(store, fake);
        Assert.True((await runner.RunAsync(key, Settings(temp.Path), "first")).Success);

        var missing = await runner.RunAsync(key, Settings(temp.Path), "missing id");
        Assert.False(missing.Success);
        Assert.Contains("valid session id", missing.Error);
        Assert.Equal(id.ToString("D"), (await store.FindAsync(key))!.SessionId);

        var different = await runner.RunAsync(key, Settings(temp.Path), "wrong id");
        Assert.False(different.Success);
        Assert.Contains("different session id", different.Error);
        Assert.Equal(id.ToString("D"), (await store.FindAsync(key))!.SessionId);
    }

    [Fact]
    public async Task SessionRunnerRefusesExecBindingBeforeCallingModelRunner()
    {
        using var temp = new TempDirectory();
        var store = new SessionStore(temp.Path);
        var key = Key();
        var savedId = Guid.NewGuid();
        await store.SaveAsync(new SessionBinding(key, Path.GetFullPath(temp.Path), "codex", "exec --json -",
            savedId.ToString("D"), DateTimeOffset.UtcNow));
        var fake = new FakeRunner(Guid.NewGuid().ToString("D"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AppServerSessionRunner(store, fake).RunAsync(key, Settings(temp.Path), "must not run"));
        Assert.Empty(fake.Calls);
        Assert.Equal(savedId.ToString("D"), (await store.FindAsync(key))!.SessionId);
    }

    [Fact]
    public async Task SessionRunnerRefusesChangedWorkspaceOrExecutableBeforeCallingModelRunner()
    {
        using var temp = new TempDirectory();
        var store = new SessionStore(temp.Path);
        var key = Key();
        var fake = new FakeRunner(Guid.NewGuid().ToString("D"));
        var runner = new AppServerSessionRunner(store, fake);
        Assert.True((await runner.RunAsync(key, Settings(temp.Path), "first")).Success);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(key,
            Settings(temp.Path) with { WorkspaceRoot = Path.Combine(temp.Path, "other") }, "changed workspace"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(key,
            Settings(temp.Path) with { AgentExecutable = "codex-next" }, "changed executable"));
        Assert.Single(fake.Calls);
    }

    private static BridgeSettings Settings(string workspace) => new()
    {
        AgentMode = "app-server", WorkspaceRoot = workspace, AgentExecutable = "codex"
    };

    private static SessionKey Key() => new("mattermost", "channel-1", "thread-1");

    private sealed class FakeRunner(params string?[] sessionIds) : IAppServerRunner
    {
        private int _next;
        public List<(BridgeSettings Settings, string Prompt, AppServerRunOptions? Options)> Calls { get; } = [];

        public Task<AppServerRunResult> RunAsync(BridgeSettings settings, string prompt, AppServerRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((settings, prompt, options));
            var sessionId = _next < sessionIds.Length ? sessionIds[_next] : Guid.NewGuid().ToString("D");
            _next++;
            return Task.FromResult(new AppServerRunResult(new AgentRunResult(true, 0, "completed", ""), sessionId));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-appsession-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}

public sealed class AgentModeSettingsTests
{
    [Fact]
    public async Task MissingAgentModeInSchemaOneDefaultsToExec()
    {
        using var temp = new SettingsDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"SchemaVersion\":1,\"WorkspaceRoot\":\"workspace\",\"RequireApproval\":true}");

        var settings = await new SettingsStore(temp.Path).LoadAsync();
        Assert.Equal("exec", settings.AgentMode);
    }

    [Fact]
    public async Task AppServerModeRoundTripsAndUnknownModesAreRejected()
    {
        using var temp = new SettingsDirectory();
        var store = new SettingsStore(temp.Path);
        await store.SaveAsync(new BridgeSettings { AgentMode = "app-server", WorkspaceRoot = "workspace" });
        Assert.Equal("app-server", (await store.LoadAsync()).AgentMode);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new BridgeSettings { AgentMode = "unknown" }));

        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"SchemaVersion\":1,\"AgentMode\":\"unknown\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private sealed class SettingsDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-agentmode-" + Guid.NewGuid().ToString("N"));
        public SettingsDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
