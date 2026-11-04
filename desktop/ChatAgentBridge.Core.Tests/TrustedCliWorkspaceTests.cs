using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class TrustedCliWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-launch-contract-" + Guid.NewGuid().ToString("N"));
    private const string Prompt = "fixture-launch-contract";

    private BridgeSettings Settings(string provider)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "Documents")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(_root, "portable-install")).FullName;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "ChatAgentBridge.TestAgent"))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0");
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(install, "ChatAgentBridge.TestAgent" + suffix), true);
        var executable = Path.Combine(install, provider + ".exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
        return new BridgeSettings { WorkspaceRoot = workspace, TrustedWorkspaceRoots = workspace,
            AgentProvider = provider, AgentExecutable = executable, AgentArguments = AgentProfile.DefaultArguments(provider) };
    }

    [Theory]
    [InlineData("inherit", false)]
    [InlineData("read-only", false)]
    [InlineData("workspace-write", false)]
    [InlineData("full-access", false)]
    [InlineData("read-only", true)]
    public async Task CodexExecAllowsTrustedNonGitWorkspaceWithoutChangingPermissions(string permission, bool resume)
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = Settings(AgentProfile.Codex) with { AgentPermissionMode = permission };
        if (resume) settings = settings with { AgentArguments = "exec resume --json " + Guid.NewGuid().ToString("D") + " -" };
        Assert.False(Directory.Exists(Path.Combine(settings.WorkspaceRoot, ".git")));
        var expected = AgentProfile.BuildInvocation(settings).Arguments;
        var result = await new AgentRunner().RunAsync(settings, Prompt);
        Assert.True(result.Success, result.Error);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(settings.WorkspaceRoot, "launch-contract.json")));
        Assert.Equal(settings.WorkspaceRoot, contract.RootElement.GetProperty("workspace").GetString());
        var actual = contract.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Single(actual, x => x == "--skip-git-repo-check");
        Assert.Equal(expected, actual.Where(x => x != "--skip-git-repo-check").ToArray());
    }

    [Theory]
    [InlineData(AgentProfile.Codex)]
    [InlineData(AgentProfile.Claude)]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    [InlineData(AgentProfile.Antigravity)]
    [InlineData(AgentProfile.Kiro)]
    [InlineData(AgentProfile.Qwen)]
    [InlineData(AgentProfile.Kimi)]
    public async Task RevokedWorkspaceIsRejectedBeforeAnyCliStarts(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = Settings(provider) with { TrustedWorkspaceRoots = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner().RunAsync(settings, Prompt));
        Assert.False(File.Exists(Path.Combine(settings.WorkspaceRoot, "launch-contract.json")));
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.Claude)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task HeadlessLaunchUsesWorkspaceAndPreservesPermissionArguments(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = Settings(provider);
        var expected = AgentProfile.BuildInvocation(settings).Arguments;
        var result = await new AgentRunner().RunAsync(settings, Prompt);
        Assert.True(result.Success, result.Error);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(settings.WorkspaceRoot, "launch-contract.json")));
        Assert.Equal(settings.WorkspaceRoot, contract.RootElement.GetProperty("workspace").GetString());
        Assert.Equal(expected, contract.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        if (provider == AgentProfile.Gemini)
            Assert.Equal("true", contract.RootElement.GetProperty("geminiTrust").GetString());
        Assert.DoesNotContain("--allow-all", expected);
        Assert.DoesNotContain("--dangerously-skip-permissions", expected);
    }

    [Fact]
    public async Task CodexDoesNotDuplicateExplicitGitCheckFlag()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = Settings(AgentProfile.Codex) with { AgentArguments = "exec --skip-git-repo-check --json -" };
        Assert.True((await new AgentRunner().RunAsync(settings, Prompt)).Success);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(settings.WorkspaceRoot, "launch-contract.json")));
        Assert.Single(contract.RootElement.GetProperty("arguments").EnumerateArray(), x => x.GetString() == "--skip-git-repo-check");
    }

    [Fact]
    public void ManualRunTrustsDefaultWorkspaceAndAcceptsTrustedParent()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "Documents", "work")).FullName;
        var settings = new BridgeSettings { WorkspaceRoot = workspace };
        Assert.Equal(workspace, CliProbeService.ValidateWorkspace(settings)); // legacy CLI probe contract
        Assert.Equal(workspace, CliProbeService.ValidateWorkspace(settings with { TrustedWorkspaceRoots = Path.GetDirectoryName(workspace)! }));
        Assert.Throws<CliWorkspaceTrustException>(() => CliProbeService.ValidateWorkspace(settings with { TrustedWorkspaceRoots = Path.Combine(_root, "other") }));
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
