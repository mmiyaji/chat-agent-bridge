using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class PerformanceProcessTests
{
    private static string? FixtureDirectory()
    {
        if (Environment.GetEnvironmentVariable("CAB_ENABLE_PERFORMANCE_E2E") != "1") return null;
        var directory = Environment.GetEnvironmentVariable("CAB_PERFORMANCE_FIXTURE_DIRECTORY");
        Assert.True(directory is not null && Path.IsPathFullyQualified(directory));
        Assert.True(File.Exists(Path.Combine(directory!, ".cab-offline-performance-fixture")), "Use the offline fixture directory, never an installed agent CLI.");
        return directory;
    }

    [Fact]
    public async Task OfflineClaudeProcessResumesExplicitSessionAndReportsUsage()
    {
        if (FixtureDirectory() is not { } fixture) return;
        var directory = Path.Combine(Path.GetTempPath(), "cab-claude-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SessionStore(directory);
            var runner = new SessionAgentRunner(store);
            var settings = new BridgeSettings { AgentProvider = "claude", AgentExecutable = Path.Combine(fixture, "claude.exe"), WorkspaceRoot = directory };
            var key = new SessionKey("mattermost", "offline-channel", "offline-thread");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var first = await runner.RunAsync(key, settings, "first offline request", timeout.Token);
            Assert.True(first.Success, first.Error);
            Assert.Contains("claude-e2e: new", first.Claude!.FinalMessage);
            var binding = await store.FindAsync(key);
            Assert.NotNull(binding);
            var second = await runner.RunAsync(key, settings, "second offline request", timeout.Token);
            Assert.True(second.Success, second.Error);
            Assert.Contains("claude-e2e: resumed", second.Claude!.FinalMessage);
            Assert.Equal(binding.SessionId, second.Claude.SessionId);
            Assert.Equal(80L, second.Usage!.TotalInputTokens);
            Assert.Equal(75d, second.Usage.CacheHitPercent);
            Assert.NotNull(second.Metrics!.ProcessStartMilliseconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OfflineAppServerReusesInitializedProcessAndReportsScopedUsage()
    {
        if (FixtureDirectory() is not { } fixture) return;
        var directory = Path.Combine(Path.GetTempPath(), "cab-pool-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var runner = new CodexAppServerRunner(new CodexAppServerProcessFactory(), enableProcessReuse: true);
            var settings = new BridgeSettings { AgentExecutable = Path.Combine(fixture, "codex.exe"), AgentMode = "app-server", WorkspaceRoot = directory };
            var options = new AppServerRunOptions(
                ApprovalHandler: (_, _) => Task.FromResult(AppServerDecision.Accept),
                InputHandler: (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(new Dictionary<string, string[]> { ["choice"] = ["alpha"] }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var first = await runner.RunAsync(settings, "first offline request", options, timeout.Token);
            Assert.True(first.Result.Success, first.Result.Error);
            var second = await runner.RunAsync(settings, "second offline request", options with { SessionId = first.SessionId }, timeout.Token);
            Assert.True(second.Result.Success, second.Result.Error);
            Assert.Equal(first.SessionId, second.SessionId);
            Assert.False(first.Result.Metrics!.ProcessReused);
            Assert.True(second.Result.Metrics!.ProcessReused);
            Assert.Equal(100L, second.Result.Usage!.InputTokens);
            Assert.Equal(75L, second.Result.Usage.CachedInputTokens);
            Assert.Contains(second.Result.Usage.Scope, new[] { "turn", "last-request" });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
