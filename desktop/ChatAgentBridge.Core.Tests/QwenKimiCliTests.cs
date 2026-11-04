using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
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
    public void KimiUsesAcpAndSupportsInheritAndFullAccess()
    {
        var inherited = AgentProfile.BuildInvocation(Settings("kimi") with { AgentPermissionMode = "inherit" });
        Assert.Equal(new[] { "acp" }, inherited.Arguments);
        var first = AgentProfile.BuildInvocation(Settings("kimi"));
        Assert.Equal(new[] { "acp" }, first.Arguments);
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
        if (provider == "kimi") Assert.DoesNotContain("--model", invocation.Arguments);
        else Assert.Contains("--model", invocation.Arguments);
        if (provider == "kimi") Assert.Equal(new[] { "acp" }, invocation.Arguments);
        else Assert.Equal(new[] { selector, Id }, invocation.Arguments.TakeLast(2));
        Assert.DoesNotContain("--yolo", invocation.Arguments);
        Assert.False(AgentProfile.HasModelCatalog(provider));
    }

    [Fact]
    public async Task KimiAcpPermissionRequestsRequireHandlerAndHonorDecision()
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
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix), true);
        var executable = Path.Combine(_directory, "kimi.exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
        var settings = Settings("kimi") with { AgentExecutable = executable, AgentPermissionMode = "inherit" };
        var runner = new AgentRunner();
        var unauthenticated = await runner.RunAsync(settings, "fixture-auth-required");
        Assert.False(unauthenticated.Success);
        Assert.Equal(ChatAgentBridge.Core.Localization.CoreText.Get("CliAccountUnsupported"), unauthenticated.Error);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC_SENTINEL", unauthenticated.Error);
        var noHandler = await runner.RunAsync(settings, "fixture-permission");
        Assert.False(noHandler.Success);
        var approved = await runner.RunWithOptionsAsync(settings, "fixture-permission", new AppServerRunOptions(
            ApprovalHandler: (approval, _) => Task.FromResult(approval.Method == "session/request_permission" ? AppServerDecision.Accept : AppServerDecision.Decline)));
        Assert.True(approved.Success, approved.Error);
        Assert.Contains("kimi-acp", approved.Output);
        Assert.Contains("mode=default", approved.Output);
        var declined = await runner.RunWithOptionsAsync(settings, "fixture-permission", new AppServerRunOptions(
            ApprovalHandler: (_, _) => Task.FromResult(AppServerDecision.Decline)));
        Assert.False(declined.Success);
    }

    [Fact]
    public async Task KimiAcpRejectsForeignEventsAndIncompleteOrOversizedTurnsButIgnoresUnknownNotifications()
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
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix), true);
        var executable = Path.Combine(_directory, "kimi.exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
        var settings = Settings("kimi") with { AgentExecutable = executable, AgentPermissionMode = "inherit" };
        var runner = new AgentRunner();

        var unknown = await runner.RunAsync(settings, "__CAB_SCENARIO_UNKNOWN_NOTIFICATION__");
        Assert.True(unknown.Success, unknown.Error);
        var wrongUpdate = await runner.RunAsync(settings, "__CAB_SCENARIO_WRONG_UPDATE__");
        Assert.False(wrongUpdate.Success);
        Assert.DoesNotContain("wrong session leak", wrongUpdate.Output);
        var wrongPermission = await runner.RunAsync(settings with { AgentPermissionMode = "full-access" }, "__CAB_SCENARIO_WRONG_PERMISSION__");
        Assert.False(wrongPermission.Success);
        var missingStop = await runner.RunAsync(settings, "__CAB_SCENARIO_MISSING_STOP__");
        Assert.False(missingStop.Success);
        var oversized = await runner.RunAsync(settings, "__CAB_SCENARIO_OVERSIZED_TEXT__");
        Assert.False(oversized.Success);
        Assert.True(oversized.Output.Length <= 1_000_000);
        var longStderr = await runner.RunAsync(settings, "__CAB_SCENARIO_LONG_STDERR__");
        Assert.True(longStderr.Success, longStderr.Error);
        Assert.DoesNotContain(new string('e', 256), longStderr.Error);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(settings, "__CAB_SCENARIO_CANCEL__", cancellation.Token));
    }

    [Fact]
    public async Task KimiAcpDispatchesReverseRequestWhoseIdCollidesWithPromptRequest()
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
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix), true);
        var executable = Path.Combine(_directory, "kimi.exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
        var settings = Settings("kimi") with { AgentExecutable = executable, AgentPermissionMode = "inherit" };
        var result = await new AgentRunner().RunWithOptionsAsync(settings, "__CAB_SCENARIO_ID_COLLISION__",
            new AppServerRunOptions(ApprovalHandler: (_, _) => Task.FromResult(AppServerDecision.Accept)));
        Assert.True(result.Success, result.Error);
        Assert.Contains("kimi-acp", result.Output);
    }

    [Fact]
    public async Task KimiAcpEnforcesRequestedModeAndValidInitializeIdentity()
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
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_directory, "ChatAgentBridge.TestAgent" + suffix), true);
        var runner = new AgentRunner();
        foreach (var fixture in new[] { "kimi-no-yolo", "kimi-badidentity", "kimi-noprotocol" })
        {
            var executable = Path.Combine(_directory, fixture + ".exe");
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, true);
            var permission = fixture == "kimi-no-yolo" ? "full-access" : "inherit";
            var result = await runner.RunAsync(Settings("kimi") with { AgentExecutable = executable, AgentPermissionMode = permission }, "mode probe");
            Assert.False(result.Success);
            Assert.Empty(result.Output);
        }
    }

    [Fact]
    public async Task LegacyKimiCliJsonlSessionBindingCannotBeResumedAsAcp()
    {
        Directory.CreateDirectory(_directory);
        var store = new SessionStore(_directory);
        var key = new SessionKey("slack", "channel", "thread", "owner", "slack");
        var legacy = new SessionBinding(key, _directory, "kimi", "kimi-code-jsonl-v2:legacy", Id, DateTimeOffset.UtcNow);
        await store.SaveAsync(legacy);
        var settings = Settings("kimi") with { AgentExecutable = "kimi", AgentPermissionMode = "inherit" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SessionAgentRunner(store).RunAsync(key, settings, "must not resume"));
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
        if (provider == "kimi") Assert.StartsWith("kimi-acp-v1:", (await store.FindAsync(key))!.AgentArguments, StringComparison.Ordinal);
        await store.UpdateModelAsync(key, "deep");
        var second = await runner.RunAsync(key, settings, "follow-up");
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.CliSession!.SessionId, second.CliSession!.SessionId);
        Assert.Contains("model=deep", second.Output);
        if (provider == "kimi") Assert.Contains("mode=yolo", second.Output);
        var other = await runner.RunAsync(key with { UserId = "other" }, settings, "separate");
        Assert.True(other.Success, other.Error);
        Assert.NotEqual(first.CliSession.SessionId, other.CliSession!.SessionId);
        if (provider == "kimi")
        {
            var failed = await new AgentRunner().RunAsync(settings, "fixture-fail");
            Assert.True(failed.Success, failed.Error);
            var longPromptText = "MEMORY_SENTINEL_84eb " + new string('x', 12_001);
            var longPrompt = await new AgentRunner().RunAsync(settings, longPromptText);
            Assert.True(longPrompt.Success, longPrompt.Error);
            Assert.Contains("MEMORY_SENTINEL_84eb", longPrompt.Output);
            Assert.DoesNotContain("MEMORY_SENTINEL_84eb", string.Join(" ", AgentProfile.BuildInvocation(settings).Arguments));
            Assert.DoesNotContain("load replay must not escape", second.Output);
        }
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_directory);
}
