using System.Text.Json.Nodes;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationSwitchConsentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-switch-consent-" + Guid.NewGuid().ToString("N"));
    private readonly SessionKey _key = new("slack", "C1", "1.0", "U1", "slack");
    private SessionStore Store => new(_directory);
    private BridgeSettings Settings => new()
    {
        WorkspaceRoot = _directory, AgentProvider = AgentProfile.Codex, AgentExecutable = "codex",
        AgentArguments = "exec --json -", AgentMode = "exec", ChatApprovalMode = "first",
        AutoApproveSwitchCommands = true, SlackBotToken = "secret", AgentPermissionMode = "read-only"
    };

    public ConversationSwitchConsentTests() => Directory.CreateDirectory(Path.Combine(_directory, "next"));

    private async Task<SessionBinding> SaveSourceAsync(BridgeSettings settings, bool approved = true)
    {
        var binding = new SessionBinding(_key, _directory, "codex", "exec --json -", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            FirstApprovalFingerprint: approved ? ConversationExecutionPolicy.GetFingerprint(_key, settings) : null);
        await Store.SaveAsync(binding);
        return binding;
    }

    private async Task<bool> RequiresAsync(BridgeSettings settings, bool switchCommand = false, SessionKey? key = null)
    {
        var owner = key ?? _key;
        var store = Store;
        var binding = await store.FindExecutionBindingAsync(owner);
        var effective = ConversationExecutionPolicy.GetEffectiveSettings(await store.ApplyWorkspaceAsync(owner, settings), binding);
        return await store.RequiresApprovalAsync(owner, effective, binding, switchCommand);
    }

    private static AgentConnectionProfile Claude(string permission = "read-only") => new(
        AgentProfile.Claude, "claude", "", "exec", null, null, permission, false, "1.0", DateTimeOffset.UtcNow);

    private async Task SwitchAgentAsync(BridgeSettings settings, bool automatic = false)
    {
        var connections = new AgentConnectionStore(_directory);
        await connections.SaveVerifiedAsync(Claude());
        await new ConversationAgentService(Store, connections, new ConversationSummaryStore(_directory))
            .SwitchAsync((await Store.FindAsync(_key))!, settings, new(AgentProfile.Claude, false),
                _ => Task.FromResult(true), requireSwitchConsent: automatic);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FirstSwitchNeedsExactPriorConversationConsent(bool approved, bool expected)
    {
        Assert.True(await RequiresAsync(Settings, switchCommand: true));
        await SaveSourceAsync(Settings, approved);
        Assert.Equal(expected, await RequiresAsync(Settings, switchCommand: true));
        Assert.True(await RequiresAsync(Settings, switchCommand: true, key: _key with { UserId = "U2" }));
        Assert.True(await RequiresAsync(Settings, switchCommand: true, key: _key with { ThreadId = "2.0" }));
        Assert.True(await RequiresAsync(Settings with { SlackBotToken = "revoked" }, switchCommand: true));
    }

    [Theory]
    [InlineData("first", true, false)]
    [InlineData("first", false, true)]
    [InlineData("auto", true, false)]
    [InlineData("auto", false, true)]
    [InlineData("always", true, true)]
    public async Task FolderTransitionPreservesOnlyOptedInConsentAndSurvivesReload(string mode, bool optIn, bool expected)
    {
        var settings = Settings with { ChatApprovalMode = mode, AutoApproveSwitchCommands = optIn };
        await SaveSourceAsync(settings);
        await Store.ChangeWorkspaceAsync(_key, Path.Combine(_directory, "next"), settings);
        Assert.Null(await Store.FindExecutionBindingAsync(_key));
        // Auto always permits ordinary turns, while its switch option still governs later switches.
        Assert.Equal(mode == "auto" ? false : expected, await RequiresAsync(settings));
        Assert.Equal(expected, await RequiresAsync(settings, switchCommand: true));
        Assert.True(await RequiresAsync(Settings, key: _key with { UserId = "U2" }));
    }

    [Theory]
    [InlineData("first", true, false)]
    [InlineData("first", false, true)]
    [InlineData("auto", true, false)]
    [InlineData("auto", false, true)]
    [InlineData("always", true, true)]
    public async Task AgentTransitionAndFirstTargetTurnUseTheActualProfileScope(string mode, bool optIn, bool expected)
    {
        var settings = Settings with { ChatApprovalMode = mode, AutoApproveSwitchCommands = optIn };
        await SaveSourceAsync(settings);
        await SwitchAgentAsync(settings);
        Assert.Null(await Store.FindExecutionBindingAsync(_key));
        Assert.True((await Store.FindAgentSelectionAsync(_key))!.RequiresApproval);
        Assert.Equal(expected, await RequiresAsync(settings));
        Assert.Equal(expected, await RequiresAsync(settings, switchCommand: true));
        if (!expected)
        {
            var effective = await Store.ApplyWorkspaceAsync(_key, settings);
            await Store.SaveAsync(new SessionBinding(_key, effective.WorkspaceRoot, "claude", "claude-stream-json-v1",
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow));
            Assert.False(await RequiresAsync(settings));
            Assert.False(await RequiresAsync(settings, switchCommand: true));
        }
    }

    [Fact]
    public async Task TransitionConsentCanAuthorizeAnotherSwitchBeforeTheNextAgentTurn()
    {
        await SaveSourceAsync(Settings);
        await SwitchAgentAsync(Settings, automatic: true);
        await Store.ChangeWorkspaceAsync(_key, Path.Combine(_directory, "next"), Settings, requireSwitchConsent: true);
        Assert.False(await RequiresAsync(Settings));
        Assert.False(await RequiresAsync(Settings, switchCommand: true));
    }

    [Fact]
    public async Task ManualSwitchAndFailedInitialTurnDoNotCreateFirstConsent()
    {
        // A failed CLI can save its session id, but no successful PC-approved turn fingerprint.
        await SaveSourceAsync(Settings, approved: false);
        await SwitchAgentAsync(Settings);
        Assert.True(await RequiresAsync(Settings));
        Assert.True(await RequiresAsync(Settings, switchCommand: true));
        await Store.ChangeWorkspaceAsync(_key, Path.Combine(_directory, "next"), Settings);
        Assert.True(await RequiresAsync(Settings));
    }

    [Fact]
    public async Task ChangedPolicyAndRevokedOptionInvalidateTransferredConsent()
    {
        await SaveSourceAsync(Settings);
        await Store.ChangeWorkspaceAsync(_key, Path.Combine(_directory, "next"), Settings);
        foreach (var changed in new[]
        {
            Settings with { AutoApproveSwitchCommands = false }, Settings with { ChatApprovalMode = "always" },
            Settings with { AgentModel = "different" }, Settings with { AgentPermissionMode = "full-access" },
            Settings with { AllowAgentNetwork = true }, Settings with { ChatResultMode = "auto" },
            Settings with { SlackBotToken = "revoked" }, Settings with { SlackAllowedUserIds = "U2" },
            Settings with { TrustedWorkspaceRoots = Path.Combine(_directory, "next") }
        })
        {
            Assert.True(await RequiresAsync(changed));
            Assert.True(await RequiresAsync(changed, switchCommand: true));
        }
        await Store.RemoveAsync(_key);
        Assert.True(await RequiresAsync(Settings));
    }

    [Fact]
    public async Task RegisteredProfileChangesAndExplicitModelChangesRevokeAgentConsent()
    {
        await SaveSourceAsync(Settings);
        await SwitchAgentAsync(Settings);
        await new AgentConnectionStore(_directory).SaveVerifiedAsync(Claude("workspace-write"));
        Assert.True(await RequiresAsync(Settings));
        await new AgentConnectionStore(_directory).SaveVerifiedAsync(Claude());
        Assert.False(await RequiresAsync(Settings));
        await Store.UpdateModelAsync(_key, "changed");
        Assert.True(await RequiresAsync(Settings));
    }

    [Fact]
    public async Task AutomaticMutationsRevalidateConsentAndCancellationCannotGrantIt()
    {
        await SaveSourceAsync(Settings, approved: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store.ChangeWorkspaceAsync(_key,
            Path.Combine(_directory, "next"), Settings, requireSwitchConsent: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SwitchAgentAsync(Settings, automatic: true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ChangeWorkspaceAsync(_key,
            Path.Combine(_directory, "next"), Settings, cancelled.Token));
        Assert.True(await RequiresAsync(Settings));
        Assert.Null(await Store.FindAgentSelectionAsync(_key));
        Assert.Null(await Store.FindWorkspaceAsync(_key));
    }

    [Fact]
    public async Task UntrustedDestinationAndDeniedAccessNeverGrantConsent()
    {
        await SaveSourceAsync(Settings);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ChangeWorkspaceAsync(_key, Path.GetTempPath(), Settings));
        var connections = new AgentConnectionStore(_directory);
        await connections.SaveVerifiedAsync(Claude());
        var source = (await Store.FindAsync(_key))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ConversationAgentService(Store, connections,
            new ConversationSummaryStore(_directory)).SwitchAsync(source,
                Settings, new(AgentProfile.Claude, false), _ => Task.FromResult(false)));
        Assert.Null(await Store.FindAgentSelectionAsync(_key));
    }

    [Fact]
    public async Task InvalidPersistedConsentFailsClosed()
    {
        await SaveSourceAsync(Settings);
        await Store.ChangeWorkspaceAsync(_key, Path.Combine(_directory, "next"), Settings);
        var path = Path.Combine(_directory, "sessions.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["SwitchConsents"]![0]!["Fingerprint"] = null;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.ListAsync());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
