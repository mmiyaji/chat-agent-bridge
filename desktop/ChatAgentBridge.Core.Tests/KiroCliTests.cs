using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class KiroCliTests : IDisposable
{
    private const string Id = "11111111-2222-4333-8444-555555555555";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-kiro-" + Guid.NewGuid().ToString("N"));
    private BridgeSettings Settings() => new() { AgentProvider = AgentProfile.Kiro, AgentExecutable = "kiro-cli",
        AgentArguments = AgentProfile.DefaultKiroArguments, WorkspaceRoot = _directory, TrustedWorkspaceRoots = _directory };

    [Fact]
    public void InvocationUsesHeadlessStdinWithoutImplicitToolTrust()
    {
        var invocation = AgentProfile.BuildInvocation(Settings() with { AgentExecutable = "codex", AgentModel = "model-x", AuthorizedCliSessionId = Id });
        Assert.Equal("kiro-cli", invocation.Executable);
        Assert.Equal(new[] { "chat", "--v2", "--no-interactive", "--output-format", "stream-json", "--model", "model-x", "--resume-id", Id }, invocation.Arguments);
        Assert.DoesNotContain("--trust-all-tools", invocation.Arguments);
        Assert.DoesNotContain("--resume", invocation.Arguments);
    }

    [Theory]
    [InlineData("chat --resume")]
    [InlineData("chat --resume-id latest")]
    [InlineData("chat --cloud")]
    [InlineData("chat --trust-all-tools")]
    [InlineData("chat --no-interactive other-prompt")]
    public void UnmanagedSelectorsAndPermissionEscalationAreRejected(string arguments)
        => Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings() with { AgentArguments = arguments }));

    [Fact]
    public async Task SettingsDraftVerificationAndConnectionRoundTrip()
    {
        var settings = Settings();
        var store = new SettingsStore(_directory);
        await store.SaveAsync(settings);
        Assert.Equal(AgentProfile.Kiro, (await store.LoadAsync()).AgentProvider);
        var connections = new AgentConnectionStore(_directory);
        await connections.SaveVerifiedAsync(new(AgentProfile.Kiro, "kiro-cli", AgentProfile.DefaultKiroArguments, "exec", "model-x", null, "inherit", false, "Kiro CLI 2.33", DateTimeOffset.UtcNow));
        Assert.Equal("model-x", (await connections.FindAsync("kiro"))!.ApplyTo(settings).AgentModel);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentPermissionMode = "full-access" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentReasoningEffort = "high" }));
    }

    [Fact]
    public void ModelsComeFromJsonWithoutConvertingCreditMultipliersToPrices()
    {
        var models = ModelCatalogService.ParseKiroModels("""{"default_model":"auto","models":[{"id":"auto","displayName":"Auto","rate_multiplier":1},{"id":"model-x"},{"id":"model-x"},{"id":"--trust-all-tools"}]}""");
        Assert.Equal(new[] { "auto", "model-x" }, models.Select(model => model.Id));
        Assert.True(models[0].IsDefault);
        Assert.All(models, model => Assert.Empty(model.SupportedReasoningEfforts));
        Assert.Throws<ModelCatalogException>(() => ModelCatalogService.ParseKiroModels("not JSON"));
    }

    private static string Stream(string id = Id) => """{"type":"runStarted","data":{"sessionId":"__ID__"}}""".Replace("__ID__", id)
        + "\n" + """{"type":"sessionUpdate","data":{"sessionId":"__ID__","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello"}}}}""".Replace("__ID__", id)
        + "\n" + """{"type":"runFinished","data":{"finalText":"hello","stopReason":"end_turn"}}"""
        + "\n" + """{"type":"metadata","data":{"meteringUsage":[{"value":0.2,"unit":"credit"}]}}""";

    [Fact]
    public void StructuredOutputRequiresAnUnambiguousSessionAndTerminalEvent()
    {
        var parsed = CliSessionOutputParser.Parse(Stream(), AgentProfile.Kiro);
        Assert.Equal(Id, parsed.SessionId);
        Assert.Equal("hello", parsed.FinalMessage);
        Assert.True(parsed.HasResult);
        Assert.False(parsed.TurnFailed);
        Assert.Null(parsed.Usage);
        Assert.True(CliSessionOutputParser.Parse(Stream("latest"), AgentProfile.Kiro).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse("""{"type":"runStarted","data":{"sessionId":"11111111-2222-4333-8444-555555555555"}}""", AgentProfile.Kiro).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(Stream() + "\n" + """{"type":"runStarted","data":{"sessionId":"99999999-2222-4333-8444-555555555555"}}""", AgentProfile.Kiro).TurnFailed);
    }

    [Fact]
    public async Task ResumeIsOwnerScopedAndUsesTheSavedId()
    {
        Directory.CreateDirectory(_directory);
        var store = new SessionStore(_directory);
        var fake = new RecordingRunner();
        var runner = new SessionAgentRunner(store, fake);
        var key = new SessionKey("mattermost", "channel", "thread", "owner", new string('a', 64));
        Assert.True((await runner.RunAsync(key, Settings(), "first")).Success);
        Assert.True((await runner.RunAsync(key, Settings() with { AgentModel = "other-model" }, "second")).Success);
        Assert.Equal(Id, fake.Last!.AuthorizedCliSessionId);
        Assert.Equal(AgentProfile.Kiro, ConversationAgentService.ProviderOf((await store.FindAsync(key))!));
        Assert.True((await runner.RunAsync(key with { UserId = "other" }, Settings(), "separate")).Success);
        Assert.Null(fake.Last!.AuthorizedCliSessionId);
    }

    private sealed class RecordingRunner : IAgentRunner
    {
        public BridgeSettings? Last { get; private set; }
        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            Last = settings;
            return Task.FromResult(new AgentRunResult(true, 0, "", "") { CliSession = CliSessionOutputParser.Parse(Stream(), AgentProfile.Kiro) });
        }
    }

    [Fact]
    public void ChatSwitchAndProbeRecognizeKiro()
    {
        Assert.True(AgentChatCommand.TryParse("!cab agent kiro summary", out var command));
        Assert.True(command.IsValid);
        Assert.Equal(AgentProfile.Kiro, command.Provider);
        Assert.True(CliProbeService.RecognizesProvider(AgentProfile.Kiro, "kiro-cli 2.33"));
        Assert.False(CliProbeService.RecognizesProvider(AgentProfile.Kiro, "gemini 2.33"));
    }

    [Fact]
    public async Task OfflineProcessChecksCatalogAndStreamsTwoTurnsWithModelChange()
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
        var executable = Path.Combine(_directory, "kiro-cli.exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable);
        var settings = Settings() with { AgentExecutable = executable, AgentModel = "fixture-kiro-fast" };
        Assert.True((await new CliProbeService().CheckAsync(settings)).ProviderRecognized);
        Assert.Equal(2, (await new ModelCatalogService().GetModelsAsync(settings)).Count);
        var runner = new SessionAgentRunner(new SessionStore(_directory), new AgentRunner());
        var key = new SessionKey("slack", "channel", "thread", "user", "slack");
        var first = await runner.RunAsync(key, settings, "first 日本語");
        Assert.True(first.Success, first.Error);
        Assert.Contains("new; model=fixture-kiro-fast; prompt=first 日本語", first.Output);
        var second = await runner.RunAsync(key, settings with { AgentModel = "fixture-kiro-deep" }, "second");
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.CliSession!.SessionId, second.CliSession!.SessionId);
        Assert.Contains("resumed; model=fixture-kiro-deep; prompt=second", second.Output);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
