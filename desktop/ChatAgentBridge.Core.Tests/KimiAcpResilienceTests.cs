using System.Diagnostics;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class KimiAcpResilienceTests : IDisposable
{
    private const string Id = "11111111-2222-4333-8444-555555555555";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-kimi-resilience-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadedSessionWithoutModesMustAcceptExplicitConfiguredModeBeforePrompt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyFixture("kimi-load-nomodes");
        var result = await Run(executable, "resume", TimeSpan.FromSeconds(3), fullAccess: false);
        Assert.True(result.Success, result.Error);
        Assert.Contains("mode=default", result.Output);

        executable = CopyFixture("kimi-load-nomodes-reject-mode");
        result = await Run(executable, "resume", TimeSpan.FromSeconds(3), fullAccess: false);
        Assert.False(result.Success);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task ApprovalWaitDoesNotConsumeExecutionBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyFixture("kimi");
        var result = await Run(executable, "__CAB_SCENARIO_HANG_AFTER_APPROVAL____CAB_SCENARIO_FINISH_AFTER_APPROVAL_DELAY__", TimeSpan.FromSeconds(3),
            approval: async (request, _) =>
            {
                Assert.Equal("rm -rf /tmp/example", request.Command);
                await Task.Delay(TimeSpan.FromMilliseconds(3500));
                return AppServerDecision.Accept;
            }, fullAccess: false);
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task ExternalCancellationInterruptsApprovalWaitAndKillsWorker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyFixture("kimi");
        using var cancellation = new CancellationTokenSource();
        var approvalShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Run(executable, "__CAB_SCENARIO_HANG_AFTER_APPROVAL__", TimeSpan.FromSeconds(5), cancellation.Token,
            async (_, _) =>
            {
                approvalShown.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return AppServerDecision.Accept;
            }, fullAccess: false);
        await approvalShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "kimi-hang.pid")));
        using var worker = Process.GetProcessById(pid);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task ApprovalWaitPausesButDoesNotResetExecutionBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyFixture("kimi");
        var approvalHandled = false;
        var result = await Run(executable, "__CAB_SCENARIO_HANG_AFTER_APPROVAL____CAB_SCENARIO_PRE_APPROVAL_DELAY____CAB_SCENARIO_FINISH_AFTER_APPROVAL_DELAY__", TimeSpan.FromSeconds(3),
            approval: async (_, _) =>
            {
                await Task.Delay(1500);
                approvalHandled = true;
                return AppServerDecision.Accept;
            }, fullAccess: false);
        Assert.False(result.Success);
        Assert.Equal(ChatAgentBridge.Core.Localization.CoreText.Get("KimiAcpTimeout"), result.Error);
        Assert.True(approvalHandled, "Fixture did not reach approval after consuming its pre-approval execution budget.");
    }

    private async Task<AgentRunResult> Run(string executable, string prompt, TimeSpan timeout,
        CancellationToken token = default,
        Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? approval = null,
        bool fullAccess = true)
    {
        var settings = new BridgeSettings { AgentProvider = "kimi", AgentExecutable = executable,
            AgentArguments = AgentProfile.DefaultKimiArguments, WorkspaceRoot = _directory,
            TrustedWorkspaceRoots = _directory, AgentPermissionMode = fullAccess ? "full-access" : "inherit",
            AuthorizedCliSessionId = Id };
        return await KimiAcpRunner.RunAsync(settings, _directory, prompt,
            new AppServerRunOptions(ApprovalHandler: approval), null, token, timeout);
    }

    private string CopyFixture(string name)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "ChatAgentBridge.TestAgent"))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0");
        Directory.CreateDirectory(_directory);
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix), true);
        var executable = Path.Combine(_directory, name + ".exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
        return executable;
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_directory);
}
