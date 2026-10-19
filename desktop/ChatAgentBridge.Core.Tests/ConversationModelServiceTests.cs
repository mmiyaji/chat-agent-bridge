using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationModelServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-model-" + Guid.NewGuid().ToString("N"));
    private readonly SessionKey _key = ChatSessionScope.Create("mattermost", "channel", "thread", "owner", "https://example.test");
    private readonly BridgeSettings _settings = new()
    {
        WorkspaceRoot = Path.GetTempPath(), AgentProvider = "codex", AgentExecutable = "codex",
        AgentArguments = "exec --json -", AgentModel = "initial", AgentReasoningEffort = "high", ChatApprovalMode = "first"
    };
    private SessionStore Store => new(_directory);
    private static Task<bool> Allow(CancellationToken _) => Task.FromResult(true);
    private static Task<IReadOnlyList<ModelOption>> Models(BridgeSettings _, CancellationToken __) =>
        Task.FromResult<IReadOnlyList<ModelOption>>([new("alternate", "Alternate", "", false)]);

    [Theory]
    [InlineData("mattermost", "codex", "exec --json -")]
    [InlineData("slack", "codex", "app-server")]
    [InlineData("slack", "claude", "claude-stream-json-v1")]
    public async Task SelectionPersistsAndRetainsOnlyTheOwnersSession(string chat, string provider, string arguments)
    {
        var key = ChatSessionScope.Create(chat, "channel", "thread", "owner", "https://example.test");
        var binding = Binding(key, arguments);
        var stranger = key with { UserId = "other" };
        var otherThread = key with { ThreadId = "other-thread" };
        var otherOrigin = chat == "mattermost"
            ? ChatSessionScope.Create(chat, "channel", "thread", "owner", "https://other.test")
            : key with { ChannelId = "other-workspace-channel" };
        foreach (var scope in new[] { key, stranger, otherThread, otherOrigin })
            await Store.SaveAsync(binding with { Key = scope });
        var settings = _settings with { AgentProvider = provider, AgentExecutable = provider };
        await new ConversationModelService(Store, Models).ChangeAsync(key, settings, "alternate", Allow);
        var after = (await new SessionStore(_directory).FindAsync(key))!;
        Assert.Equal(binding.SessionId, after.SessionId);
        Assert.Equal(binding.WorkspaceRoot, after.WorkspaceRoot);
        Assert.Equal(binding.DisplayName, after.DisplayName);
        Assert.Equal("alternate", after.Model);
        Assert.Null(after.ReasoningEffort);
        Assert.Null(after.FirstApprovalFingerprint);
        Assert.True(ConversationExecutionPolicy.RequiresApproval(key,
            ConversationExecutionPolicy.GetEffectiveSettings(settings, after), after));
        foreach (var scope in new[] { stranger, otherThread, otherOrigin })
            Assert.Equal("initial", (await Store.FindAsync(scope))!.Model);
    }

    [Fact]
    public async Task CatalogIsReusedAndIsolatedByCliAndWorkspace()
    {
        var calls = 0;
        var clock = new TestClock();
        var service = new ConversationModelService(Store, (settings, ct) => { calls++; return Models(settings, ct); }, clock);
        await service.GetModelsAsync(_settings);
        await service.GetModelsAsync(_settings with { AgentModel = "alternate" });
        Assert.Equal(1, calls);
        await service.GetModelsAsync(_settings with { AgentExecutable = Path.Combine(_directory, "codex.exe") });
        Assert.Equal(2, calls);
        clock.Now = clock.Now.AddMinutes(6);
        await service.GetModelsAsync(_settings);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UnknownModelCannotChangeTheSession()
    {
        var binding = Binding(_key);
        await Store.SaveAsync(binding);
        await Assert.ThrowsAsync<ArgumentException>(() => new ConversationModelService(Store, Models)
            .ChangeAsync(_key, _settings, "not-from-cli", Allow));
        Assert.Equal(binding, await Store.FindAsync(_key));
    }

    [Fact]
    public async Task RevocationDuringDiscoveryPreventsChange()
    {
        var binding = Binding(_key);
        await Store.SaveAsync(binding);
        var allowed = true;
        var service = new ConversationModelService(Store, (s, ct) => { allowed = false; return Models(s, ct); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ChangeAsync(_key, _settings, "alternate",
            _ => Task.FromResult(allowed)));
        Assert.Equal(binding, await Store.FindAsync(_key));
    }

    [Fact]
    public async Task InitiallyDeniedDoesNotLaunchCliDiscovery()
    {
        var called = false;
        var service = new ConversationModelService(Store, (s, ct) => { called = true; return Models(s, ct); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ChangeAsync(_key, _settings, "alternate",
            _ => Task.FromResult(false)));
        Assert.False(called);
    }

    [Fact]
    public async Task ConcurrentConversationChangesAreNotOverwritten()
    {
        await Store.SaveAsync(Binding(_key));
        var service = new ConversationModelService(Store, async (s, ct) =>
        {
            await Store.UpdateModelAndEffortAsync(_key, "pc-selection", "low", ct);
            return await Models(s, ct);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeAsync(_key, _settings, "alternate", Allow));
        Assert.Equal("pc-selection", (await Store.FindAsync(_key))!.Model);
    }

    [Fact]
    public async Task DefaultRestoresSettingsWithoutDiscoveryOrLosingHistory()
    {
        var binding = Binding(_key);
        await Store.SaveAsync(binding);
        var service = new ConversationModelService(Store, (_, _) => throw new Exception("No discovery expected"));
        await service.ChangeAsync(_key, _settings, null, Allow);
        var saved = (await Store.FindAsync(_key))!;
        Assert.Equal(binding.SessionId, saved.SessionId);
        Assert.Null(saved.Model);
        Assert.Null(saved.ReasoningEffort);
        var effective = ConversationExecutionPolicy.GetEffectiveSettings(_settings, saved);
        Assert.Equal("initial", effective.AgentModel);
        Assert.Equal("high", effective.AgentReasoningEffort);
    }

    [Fact]
    public async Task OtherUsersCannotCreateOrModifyASessionBySelectingAModel()
    {
        await Store.SaveAsync(Binding(_key));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new ConversationModelService(Store, Models)
            .ChangeAsync(_key with { UserId = "stranger" }, _settings, "alternate", Allow));
        Assert.Single(await Store.ListAsync());
    }

    [Fact]
    public async Task ProviderMismatchAndUntrustedWorkspaceCannotLaunchDiscovery()
    {
        await Store.SaveAsync(Binding(_key));
        var service = new ConversationModelService(Store, (_, _) => throw new Exception("No discovery expected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeAsync(_key,
            _settings with { AgentProvider = "claude", AgentExecutable = "claude" }, "alternate", Allow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(_settings with
        {
            WorkspaceRoot = _directory, TrustedWorkspaceRoots = Path.Combine(_directory, "trusted-child")
        }));
    }

    private SessionBinding Binding(SessionKey key, string arguments = "exec --json -") => new(key,
        Path.GetFullPath(_settings.WorkspaceRoot), "codex", arguments, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow,
        "initial", "high", "first", "auto", "read-only", new string('a', 64), "My conversation");

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
