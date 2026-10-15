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
}
