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

    [Theory]
    [InlineData(AgentProfile.Gemini, "gemini", "--output-format", "stream-json")]
    [InlineData(AgentProfile.GitHubCopilot, "copilot", "--output-format", "json")]
    public void BasicProviderProfilesUseStructuredArguments(string provider, string executable, string first, string second)
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider });

        Assert.Equal(executable, invocation.Executable);
        Assert.Equal(provider, invocation.Provider);
        Assert.Contains(first, invocation.Arguments);
        Assert.Contains(second, invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, argument => argument is "--yolo" or "--allow-all" or "--resume" or "--continue");
        Assert.Null(invocation.Model);
        Assert.Null(invocation.ReasoningEffort);
    }

    [Fact]
    public void BasicProviderArgumentsStaySeparateAndRequiredFlagsOverrideConflictingValues()
    {
        var promptLikeValue = "text; Write-Output unsafe";
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentProvider = AgentProfile.Gemini,
            AgentArguments = $"--output-format json --label \"{promptLikeValue}\""
        });

        Assert.Equal(new[] { "--label", promptLikeValue, "--output-format", "stream-json" }, invocation.Arguments);

        var copilot = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentProvider = AgentProfile.GitHubCopilot,
            AgentArguments = "--output-format json --silent"
        });
        Assert.Equal(1, copilot.Arguments.Count(argument => argument == "--output-format"));
        Assert.Equal("json", copilot.Arguments.Last());
        Assert.DoesNotContain("--silent", copilot.Arguments);
        Assert.Contains("--no-ask-user", copilot.Arguments);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public void BasicProvidersForwardModelButRejectEffortAndPermissionOverrides(string provider)
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider, AgentModel = "model-x" });
        Assert.Equal("model-x", invocation.Model);
        Assert.Equal(new[] { "--model", "model-x" }, invocation.Arguments.TakeLast(2));
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider, AgentReasoningEffort = "high" }));
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider, AgentPermissionMode = "workspace-write" }));
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public void StructuredProfilesPreserveNodeEntrypointAndReplaceLegacyOutputAndModel(string provider)
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentProvider = provider, AgentExecutable = "node.exe", AgentModel = "new-model",
            AgentArguments = "--no-warnings \"C:\\Program Files\\cli\\index.js\" --output-format=text --silent -s --model=old"
        });
        Assert.Equal("--no-warnings", invocation.Arguments[0]);
        Assert.Equal("C:\\Program Files\\cli\\index.js", invocation.Arguments[1]);
        Assert.DoesNotContain("--model=old", invocation.Arguments);
        Assert.Equal(new[] { "--model", "new-model" }, invocation.Arguments.TakeLast(2));
        if (provider == AgentProfile.GitHubCopilot)
        {
            Assert.DoesNotContain("-s", invocation.Arguments);
            Assert.DoesNotContain("--silent", invocation.Arguments);
            Assert.Contains("--no-ask-user", invocation.Arguments);
        }
    }

    [Theory]
    [InlineData("--resume 00000000-0000-0000-0000-000000000000")]
    [InlineData("--resume=latest")]
    [InlineData("-rlatest")]
    [InlineData("--continue")]
    [InlineData("--session-id=00000000-0000-0000-0000-000000000000")]
    [InlineData("-p arbitrary")]
    public void StructuredProfilesRejectConfiguredSessionAndPromptSelection(string arguments)
    {
        foreach (var provider in new[] { AgentProfile.Gemini, AgentProfile.GitHubCopilot })
            Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider, AgentArguments = arguments }));
    }

    [Theory]
    [InlineData("-sr00000000-0000-0000-0000-000000000000")]
    [InlineData("-dr00000000-0000-0000-0000-000000000000")]
    [InlineData("-spINLINE-PROMPT")]
    [InlineData("-sd")]
    [InlineData("-mattached-model")]
    [InlineData("-o=text")]
    public void StructuredProfilesRejectBundledAndAttachedShortOptions(string arguments)
    {
        foreach (var provider in new[] { AgentProfile.Gemini, AgentProfile.GitHubCopilot })
            Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(new BridgeSettings { AgentProvider = provider, AgentArguments = arguments }));
    }

    [Fact]
    public void GeminiReplacesSeparateShortOutputAliasAndModelWithoutLosingOtherArguments()
    {
        var invocation = AgentProfile.BuildInvocation(new BridgeSettings
        {
            AgentProvider = AgentProfile.Gemini, AgentArguments = "-o text --output-format=json -m old -d",
            AgentModel = "new-model"
        });
        Assert.Equal(new[] { "-d", "--output-format", "stream-json", "--model", "new-model" }, invocation.Arguments);
        Assert.DoesNotContain("-o", invocation.Arguments);
        Assert.DoesNotContain("text", invocation.Arguments);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public void StructuredProfilesResumeOnlyAuthorizedUuid(string provider)
    {
        var id = Guid.NewGuid().ToString("D");
        var settings = new BridgeSettings { AgentProvider = provider, AuthorizedCliSessionId = id };
        Assert.Equal(new[] { "--resume", id }, AgentProfile.BuildInvocation(settings).Arguments.TakeLast(2));
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(settings with { AuthorizedCliSessionId = "latest" }));
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(settings with { AgentModel = "--allow-all" }));
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
