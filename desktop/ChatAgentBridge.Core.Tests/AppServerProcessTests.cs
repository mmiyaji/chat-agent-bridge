using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppServerProcessTests
{
    [Fact]
    public async Task OfflineFixtureExercisesRealStdioApprovalInputAndResume()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAB_ENABLE_APP_SERVER_FIXTURE_E2E"), "1", StringComparison.Ordinal))
            return;

        var executable = Environment.GetEnvironmentVariable("CAB_APP_SERVER_FIXTURE_EXE");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Set CAB_APP_SERVER_FIXTURE_EXE to the absolute path of the renamed offline fixture host.");
        Assert.True(Path.IsPathFullyQualified(executable), "The fixture executable path must be absolute.");
        Assert.True(File.Exists(executable), $"Fixture executable was not found: {executable}");

        using var workspace = new TemporaryWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var runner = new CodexAppServerRunner();
        var settings = new BridgeSettings { WorkspaceRoot = workspace.Path, AgentExecutable = executable! };
        const string japanesePrompt = "日本語のapp-server prompt 😀🚀";
        var first = await runner.RunAsync(settings, japanesePrompt, new AppServerRunOptions(
            ApprovalHandler: (approval, _) =>
            {
                Assert.Equal("item/commandExecution/requestApproval", approval.Method);
                Assert.Equal(workspace.Path, approval.WorkingDirectory);
                return Task.FromResult(AppServerDecision.Accept);
            },
            InputHandler: (input, _) =>
            {
                Assert.Single(input.Questions);
                return Task.FromResult<IReadOnlyDictionary<string, string[]>?>(
                    new Dictionary<string, string[]> { ["choice"] = ["alpha"] });
            }), timeout.Token).WaitAsync(TimeSpan.FromSeconds(40));

        Assert.True(first.Result.Success, first.Result.Error);
        Assert.Contains($"app-server-e2e: approved; answer=alpha; prompt={japanesePrompt}", first.Result.Output);
        Assert.True(Guid.TryParse(first.SessionId, out _));

        var resumed = await runner.RunAsync(settings, "second prompt", new AppServerRunOptions(
            SessionId: first.SessionId,
            ApprovalHandler: (_, _) => Task.FromResult(AppServerDecision.Decline)), timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(40));

        Assert.False(resumed.Result.Success);
        Assert.NotEqual(0, resumed.Result.ExitCode);
        Assert.Contains("fixture approval declined", resumed.Result.Error);
        Assert.Equal(first.SessionId, resumed.SessionId);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-appserver-e2e-" + Guid.NewGuid().ToString("N"));
        public TemporaryWorkspace() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
