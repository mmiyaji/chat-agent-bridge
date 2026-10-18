using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationPolicyTests
{
    [Fact]
    public async Task FirstApprovalSurvivesStoreNormalizationAndReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-first-consent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(directory);
            var settings = Settings;
            await store.SaveAsync(new SessionBinding(Owner, settings.WorkspaceRoot, "codex", "exec --json -",
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow));
            await store.SetFirstApprovalFingerprintAsync(Owner, ConversationExecutionPolicy.GetFingerprint(Owner, settings));
            var reloaded = await new SessionStore(directory).FindAsync(Owner);
            Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, settings, reloaded));
            await store.UpdateExecutionPolicyAsync(Owner, "first", "manual", "inherit");
            Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings, await store.FindAsync(Owner)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static readonly SessionKey Owner = ChatSessionScope.Create("slack", "C1", "1.0", "U1");
    private static BridgeSettings Settings => new()
    {
        WorkspaceRoot = Path.GetTempPath(), ChatApprovalMode = "first", SlackAllowedUserIds = "U1",
        AgentExecutable = "codex", AgentPermissionMode = "inherit"
    };

    [Fact]
    public void FirstApprovalRequiresExactOwnerAndExecutionPolicy()
    {
        var settings = Settings;
        var binding = new SessionBinding(Owner, settings.WorkspaceRoot, "codex", "exec --json -",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            FirstApprovalFingerprint: ConversationExecutionPolicy.GetFingerprint(Owner, settings));
        Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, settings, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings, null));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner with { UserId = "U2" }, settings, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { AgentPermissionMode = "full-access" }, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { ChatResultMode = "auto" }, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { SlackBotToken = "changed" }, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { AgentModel = "different" }, binding));
    }

    [Fact]
    public void AlwaysAndAutomaticApprovalRemainSeparateFromToolPermissions()
    {
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, Settings with { ChatApprovalMode = "always" }, null));
        Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, Settings with { ChatApprovalMode = "auto", AgentPermissionMode = "read-only" }, null));
    }

    [Fact]
    public void ConversationOverridesDoNotEscalateUnspecifiedDefaults()
    {
        var binding = new SessionBinding(Owner, Settings.WorkspaceRoot, "codex", "exec --json -", Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow, ChatApprovalMode: "always", ChatResultMode: "none", AgentPermissionMode: "read-only");
        var effective = ConversationExecutionPolicy.GetEffectiveSettings(Settings, binding);
        Assert.Equal("always", effective.ChatApprovalMode);
        Assert.Equal("none", effective.ChatResultMode);
        Assert.Equal("read-only", effective.AgentPermissionMode);
        Assert.Equal("first", Settings.ChatApprovalMode);
    }
}
