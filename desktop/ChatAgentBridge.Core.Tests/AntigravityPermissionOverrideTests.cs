using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AntigravityPermissionOverrideTests
{
    private static BridgeSettings Settings(string arguments, string permissionMode) => new()
    {
        AgentProvider = AgentProfile.Antigravity,
        AgentArguments = arguments,
        AgentPermissionMode = permissionMode
    };

    [Theory]
    [InlineData("--dangerously-skip-permissions=true")]
    [InlineData("--dangerously-skip-permissions=false")]
    [InlineData("--dangerously-skip-permissions=1")]
    [InlineData("--dangerously-skip-permissions=t")]
    [InlineData("--dangerously-skip-permissions=TRUE")]
    [InlineData("--dangerously-skip-permissions=invalid")]
    [InlineData("--dangerously-skip-permissions true")]
    [InlineData("--dangerously-skip-permissions false")]
    public void WorkspaceWriteRemovesAssignedSkipPermissionBooleans(string configuredFlag)
    {
        var invocation = AgentProfile.BuildInvocation(Settings(configuredFlag, "workspace-write"));

        Assert.Equal(new[] { "--output-format", "stream-json", "--mode", "accept-edits" }, invocation.Arguments);
    }

    [Fact]
    public void WorkspaceWriteRemovesBareSkipPermissionFlag()
    {
        var invocation = AgentProfile.BuildInvocation(Settings("--dangerously-skip-permissions", "workspace-write"));

        Assert.Equal(new[] { "--output-format", "stream-json", "--mode", "accept-edits" }, invocation.Arguments);
    }

    [Fact]
    public void ManagedModeRemovesDuplicatesAndMixedConfiguredModes()
    {
        var invocation = AgentProfile.BuildInvocation(Settings(
            "--mode plan --dangerously-skip-permissions=true --mode=accept-edits --dangerously-skip-permissions=false --dangerously-skip-permissions",
            "workspace-write"));

        Assert.Equal(new[] { "--output-format", "stream-json", "--mode", "accept-edits" }, invocation.Arguments);
    }

    [Fact]
    public void FullAccessReplacesFalseSkipPermissionOverrideWithCanonicalFlag()
    {
        var invocation = AgentProfile.BuildInvocation(Settings("--dangerously-skip-permissions=false --sandbox", "full-access"));

        Assert.Equal(new[] { "--sandbox", "--output-format", "stream-json", "--dangerously-skip-permissions" }, invocation.Arguments);
    }

    [Fact]
    public void InheritPreservesConfiguredPermissionArguments()
    {
        const string configured = "--mode plan --dangerously-skip-permissions=true --dangerously-skip-permissions=false";

        var invocation = AgentProfile.BuildInvocation(Settings(configured, "inherit"));

        Assert.Equal(new[]
        {
            "--mode", "plan", "--dangerously-skip-permissions=true", "--dangerously-skip-permissions=false",
            "--output-format", "stream-json"
        }, invocation.Arguments);
    }
}
