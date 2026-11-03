using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class QwenKimiCliTests : IDisposable
{
    private const string Id = "11111111-2222-4333-8444-555555555555";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-qwen-kimi-" + Guid.NewGuid().ToString("N"));
    private BridgeSettings Settings(string provider) => new() { AgentProvider = provider, AgentExecutable = provider,
        AgentArguments = provider == AgentProfile.Qwen ? AgentProfile.DefaultQwenArguments : AgentProfile.DefaultKimiArguments,
        WorkspaceRoot = _directory, TrustedWorkspaceRoots = _directory,
        AgentPermissionMode = provider == AgentProfile.Kimi ? "full-access" : "inherit" };

    [Fact]
    public void KimiRequiresExplicitFullAccessAndLetsCliCreateNewSession()
    {
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings("kimi") with { AgentPermissionMode = "inherit" }));
        var first = AgentProfile.BuildInvocation(Settings("kimi"));
        Assert.DoesNotContain("--session", first.Arguments);
        Assert.DoesNotContain("--continue", first.Arguments);
        Assert.DoesNotContain("--print", first.Arguments);
        var draft = Settings("kimi") with { AgentArguments = "--print --output-format stream-json" };
        Assert.Equal(first.Arguments, AgentProfile.BuildInvocation(draft).Arguments);
    }

    [Theory]
    [InlineData("qwen", "--resume")]
    [InlineData("kimi", "--session")]
    public void ModelAndAuthorizedSessionAreSeparateArguments(string provider, string selector)
    {
        var invocation = AgentProfile.BuildInvocation(Settings(provider) with { AgentModel = "model-x", AuthorizedCliSessionId = Id });
        Assert.Contains("--model", invocation.Arguments);
        Assert.Equal(new[] { selector, provider == "kimi" ? "session_" + Id : Id }, invocation.Arguments.TakeLast(2));
        Assert.DoesNotContain("--yolo", invocation.Arguments);
        Assert.False(AgentProfile.HasModelCatalog(provider));
    }

    [Theory]
    [InlineData("qwen", "--resume latest")]
    [InlineData("qwen", "--yolo")]
    [InlineData("qwen", "--output-format stream-json --include-directories C:\\other")]
    [InlineData("kimi", "--session somebody-else")]
    [InlineData("kimi", "--print --auto")]
    [InlineData("kimi", "--print -p injected")]
    public void ConfiguredSelectorsAndHiddenApprovalFlagsCannotBypassPolicy(string provider, string arguments)
        => Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings(provider) with { AgentArguments = arguments }));

    [Fact]
    public void QwenRequiresSuccessfulTerminalEventAndConsistentParentId()
    {
        var first = """{"type":"system","subtype":"session_start","session_id":"11111111-2222-4333-8444-555555555555","model":"model-x"}""";
        var terminal = """{"type":"result","subtype":"success","session_id":"11111111-2222-4333-8444-555555555555","is_error":false,"result":"done"}""";
        Assert.True(CliSessionOutputParser.Parse(first, "qwen").TurnFailed);
        var parsed = CliSessionOutputParser.Parse(first + "\n" + terminal, "qwen");
        Assert.False(parsed.TurnFailed);
        Assert.Equal("done", parsed.FinalMessage);
        Assert.True(CliSessionOutputParser.Parse(first + "\n" + terminal.Replace(Id, Guid.NewGuid().ToString("D")), "qwen").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(first + "\n" + terminal.Replace("\"is_error\":false", "\"is_error\":true"), "qwen").TurnFailed);
        var child = """{"type":"result","parent_tool_use_id":"child","session_id":"22222222-2222-4333-8444-555555555555","subtype":"success","result":"hidden"}""";
        Assert.Equal("done", CliSessionOutputParser.Parse(first + "\n" + child + "\n" + terminal, "qwen").FinalMessage);
    }

    [Fact]
    public void KimiRequiresNativeSessionHintAndExcludesThinkingAndTools()
    {
        var output = """{"role":"assistant","content":[{"type":"think","think":"private"},{"type":"text","text":"done"}]}""";
        Assert.True(CliSessionOutputParser.Parse(output, "kimi").TurnFailed);
        var hint = "{\"role\":\"meta\",\"type\":\"session.resume_hint\",\"session_id\":\"session_" + Id + "\"}";
        Assert.True(CliSessionOutputParser.Parse(output, "kimi", Id).TurnFailed);
        var parsed = CliSessionOutputParser.Parse(output + "\n" + hint, "kimi", Id);
        Assert.False(parsed.TurnFailed);
        Assert.Equal(Id, parsed.SessionId);
        Assert.Equal("done", parsed.FinalMessage);
        Assert.Null(parsed.Usage);
        Assert.False(CliSessionOutputParser.Parse("{\"role\":\"meta\",\"type\":\"system.version\",\"version\":\"2.4.1\"}\n" + output + "\n" + hint, "kimi").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(hint, "kimi").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(output + "\n" + hint.Replace(Id, "22222222-2222-4333-8444-555555555555"), "kimi", Id).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(output + "\n" + hint.Replace("session_", ""), "kimi").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(output + "\n" + hint + "\n" + output, "kimi").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(output + "\n{\"role\":\"meta\",\"type\":\"unknown\"}\n" + hint, "kimi").TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(output + "\n{broken", "kimi", Id).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse("""{"role":"tool","content":"secret output"}""", "kimi", Id).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse("""{"role":"assistant","content":"running","tool_calls":[{"function":{"name":"Shell","arguments":"secret"}}]}""", "kimi", Id).TurnFailed);
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("kimi")]
    public async Task SettingsConnectionsAndChatCommandsRoundTrip(string provider)
    {
        var settings = Settings(provider);
        var store = new SettingsStore(_directory);
        await store.SaveAsync(settings);
        Assert.Equal(provider, (await store.LoadAsync()).AgentProvider);
        var connections = new AgentConnectionStore(_directory);
        await connections.SaveVerifiedAsync(new(provider, provider, settings.AgentArguments, "exec", "model-x", null,
            settings.AgentPermissionMode, false, provider + " fixture 1.0", DateTimeOffset.UtcNow));
        Assert.Equal(provider, (await connections.FindAsync(provider))!.Provider);
        Assert.True(AgentChatCommand.TryParse($"!cab agent {provider} summary", out var command));
        Assert.True(command.IsValid);
        Assert.Equal(provider, command.Provider);
    }

    [Fact]
    public void QwenNpmShimUsesPackageMetadataWithoutExecutingScripts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var package = Path.Combine(_directory, "node_modules", "@qwen-code", "qwen-code");
        Directory.CreateDirectory(package);
        var shim = Path.Combine(_directory, "qwen.cmd");
        File.WriteAllText(shim, "must never execute this script");
        File.WriteAllText(Path.Combine(_directory, "node.exe"), "fixture");
        File.WriteAllText(Path.Combine(package, "package.json"), """{"name":"@qwen-code/qwen-code","bin":{"qwen":"cli.js"}}""");
        File.WriteAllText(Path.Combine(package, "cli.js"), "// fixture");
        var resolution = NodeCliPackageResolver.Resolve(shim, "qwen");
        Assert.NotNull(resolution);
        Assert.Equal(Path.Combine(package, "cli.js"), resolution.EntryPoint);
        Assert.Equal("@qwen-code/qwen-code", resolution.PackageName);
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("kimi")]
    public async Task OfflineProcessProbesRunsResumesAndSeparatesUsers(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
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
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix));
        var executable = Path.Combine(_directory, provider + ".exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable);
        var settings = Settings(provider) with { AgentExecutable = executable, AgentModel = "fast" };
        Assert.True((await new CliProbeService().CheckAsync(settings)).ProviderRecognized);
        var store = new SessionStore(_directory);
        var runner = new SessionAgentRunner(store, new AgentRunner());
        var key = new SessionKey("slack", "channel", "thread", "owner", "slack");
        var prompt = "--auto hello 日本語\n--model injected \"quoted\" & echo data";
        var first = await runner.RunAsync(key, settings, prompt);
        Assert.True(first.Success, first.Error);
        Assert.DoesNotContain("private-reasoning", first.Output);
        Assert.Contains(prompt, first.Output);
        Assert.Equal(provider, ConversationAgentService.ProviderOf((await store.FindAsync(key))!));
        await store.UpdateModelAsync(key, "deep");
        var second = await runner.RunAsync(key, settings, "follow-up");
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.CliSession!.SessionId, second.CliSession!.SessionId);
        Assert.Contains("model=deep", second.Output);
        var other = await runner.RunAsync(key with { UserId = "other" }, settings, "separate");
        Assert.True(other.Success, other.Error);
        Assert.NotEqual(first.CliSession.SessionId, other.CliSession!.SessionId);
        if (provider == "kimi")
        {
            var legacy = Path.Combine(_directory, "kimi-legacy.exe");
            File.Copy(executable, legacy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CliProbeService().CheckAsync(settings with { AgentExecutable = legacy }));
            var failed = await new AgentRunner().RunAsync(settings, "fixture-fail");
            Assert.False(failed.Success);
            var tooLong = await new AgentRunner().RunAsync(settings, new string('x', 12_001));
            Assert.False(tooLong.Success);
            Assert.Equal(-1, tooLong.ExitCode);
        }
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_directory);
}
