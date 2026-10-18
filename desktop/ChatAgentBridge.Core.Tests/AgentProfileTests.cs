using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentProfileTests
{
    [Fact]
    public void DefaultCodexProfileAddsModelAsAnArgumentWithoutShellInterpolation()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings { AgentModel = "gpt-5.4" });
        Assert.Equal("codex", invocation.Executable);
        Assert.Equal(new[] { "exec", "--json", "-", "--model", "gpt-5.4" }, invocation.Arguments);
    }

    [Fact]
    public void CodexModelReplacesAnyLegacyModelFlagAndLeavesOtherArgumentsUntouched()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentArguments = "exec --json --model old --sandbox read-only -",
            AgentModel = "new model"
        });
        Assert.Equal(new[] { "exec", "--json", "--sandbox", "read-only", "-", "--model", "new model" }, invocation.Arguments);
    }

    [Fact]
    public void ClaudeProfileUsesStreamJsonAndSeparateModelArguments()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = "claude", AgentModel = "sonnet" });
        Assert.Equal("claude", invocation.Executable);
        Assert.Equal(new[] { "--print", "--output-format", "stream-json", "--verbose", "--model", "sonnet" }, invocation.Arguments);
    }

    [Fact]
    public void ReasoningEffortIsForwardedAsSeparateProviderArgumentsAndExistingConfigIsPreserved()
    {
        var codex = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentArguments = "exec --json - -c model_reasoning_effort=old -c model=fixture",
            AgentReasoningEffort = " HIGH "
        });
        Assert.Equal(new[] { "exec", "--json", "-", "-c", "model=fixture", "-c", "model_reasoning_effort=\"high\"" }, codex.Arguments);
        var claude = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = "claude", AgentReasoningEffort = "xhigh" });
        Assert.Equal(new[] { "--print", "--output-format", "stream-json", "--verbose", "--effort", "xhigh" }, claude.Arguments);
    }

    [Theory]
    [InlineData("x\" && whoami", null)]
    [InlineData("UPPER", "upper")]
    [InlineData("  future_2-level ", "future_2-level")]
    public void ReasoningEffortNormalizationAcceptsFutureTokensSafely(string value, string? expected)
    {
        if (expected is null) Assert.Throws<ArgumentException>(() => AgentProfile.NormalizeReasoningEffort(value));
        else Assert.Equal(expected, AgentProfile.NormalizeReasoningEffort(value));
    }

    [Fact]
    public void LegacyCustomExecutableAndArgumentsRemainUntouched()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentExecutable = "powershell.exe",
            AgentArguments = "-NoProfile -File \"C:\\folder with spaces\\run.ps1\""
        });
        Assert.Equal("custom", invocation.Provider);
        Assert.Equal(new[] { "-NoProfile", "-File", "C:\\folder with spaces\\run.ps1" }, invocation.Arguments);
    }

    [Fact]
    public void CustomProfilesRejectModelInsteadOfSilentlyIgnoringIt()
        => Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentProvider = "custom", AgentExecutable = "my-agent", AgentArguments = "run", AgentModel = "model-x"
        }));

    [Theory]
    [InlineData("read-only", "sandbox_mode=\"read-only\"")]
    [InlineData("workspace-write", "sandbox_mode=\"workspace-write\"")]
    [InlineData("full-access", "sandbox_mode=\"danger-full-access\"")]
    public void ManagedCodexPermissionModeOverridesCustomPermissionFlags(string mode, string sandboxConfig)
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentArguments = "exec --json --full-auto --sandbox danger-full-access -s=read-only --ask-for-approval on-request -a on-request -c approval_policy=on-request -c sandbox_mode=danger-full-access -c model=fixture -",
            AgentPermissionMode = mode
        });
        Assert.DoesNotContain("--sandbox", invocation.Arguments);
        Assert.DoesNotContain("--full-auto", invocation.Arguments);
        Assert.DoesNotContain("--ask-for-approval", invocation.Arguments);
        Assert.DoesNotContain("-a", invocation.Arguments);
        Assert.DoesNotContain("on-request", invocation.Arguments);
        Assert.Contains("-c", invocation.Arguments);
        Assert.Contains("model=fixture", invocation.Arguments);
        Assert.Contains(sandboxConfig, invocation.Arguments);
        Assert.Contains("approval_policy=\"never\"", invocation.Arguments);
        Assert.DoesNotContain("approval_policy=\"on-request\"", invocation.Arguments);
        Assert.Equal(1, invocation.Arguments.Count(value => value.Contains("sandbox_mode=", StringComparison.Ordinal)));
    }

    [Fact]
    public void ManagedWorkspaceWriteCodexNetworkOptionIsExplicitAndOverridesLegacyValue()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentArguments = "exec --json - -c sandbox_workspace_write.network_access=true",
            AgentPermissionMode = "workspace-write",
            AllowAgentNetwork = false
        });
        Assert.Contains("sandbox_workspace_write.network_access=false", invocation.Arguments);
    }

    [Fact]
    public void ManagedCodexPermissionConfigWorksForExecResumeArguments()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentArguments = "exec resume 00000000-0000-0000-0000-000000000000 -",
            AgentPermissionMode = "read-only"
        });
        Assert.Equal("exec", invocation.Arguments[0]);
        Assert.Equal("resume", invocation.Arguments[1]);
        Assert.Contains("sandbox_mode=\"read-only\"", invocation.Arguments);
        Assert.Contains("approval_policy=\"never\"", invocation.Arguments);
        Assert.DoesNotContain("--sandbox", invocation.Arguments);
        Assert.DoesNotContain("--ask-for-approval", invocation.Arguments);
    }

    [Fact]
    public void ClaudePermissionModeUsesProviderSupportedFlagsAndAskFailsActionably()
    {
        var readOnly = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = "claude", AgentPermissionMode = "read-only" });
        Assert.Equal(new[] { "--permission-mode", "plan" }, readOnly.Arguments.TakeLast(2));
        var full = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = "claude", AgentPermissionMode = "full-access" });
        Assert.Contains("--dangerously-skip-permissions", full.Arguments);
        var error = Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings
        { AgentProvider = "claude", AgentPermissionMode = "ask" }));
        Assert.Contains("host control RPC", error.Message);
    }

    [Fact]
    public void NonInheritPermissionModeCannotBeAppliedToCustomProfile()
        => Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings
        { AgentProvider = "custom", AgentExecutable = "my-agent", AgentPermissionMode = "read-only" }));
}
