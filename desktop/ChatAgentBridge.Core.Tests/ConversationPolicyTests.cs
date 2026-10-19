using ChatAgentBridge.Core.Sessions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner with { ThreadId = "2.0" }, settings, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner with { ChannelId = "C2" }, settings, binding));
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
    public void AgentQuestionSharingPreservesLegacyFingerprintWhenOffAndUsesNewScopeWhenOn()
    {
        var settings = Settings with
        {
            MattermostAllowedUserIds = "mm-user",
            MattermostChannelId = "mm-channel",
            MattermostAllowedThreadIds = "mm-thread",
            SlackAllowedChannelIds = "C1",
            SlackAllowedThreadIds = "1.0",
            ShareApprovalsInChat = true,
            MattermostToken = "mm-token",
            SlackBotToken = "bot-token",
            SlackAppToken = "app-token"
        };

        var expectedLegacy = LegacyFingerprint(Owner, settings);
        var actualOff = ConversationExecutionPolicy.GetFingerprint(Owner, settings with { ShareAgentQuestionsInChat = false });
        var actualOn = ConversationExecutionPolicy.GetFingerprint(Owner, settings with { ShareAgentQuestionsInChat = true });

        Assert.Equal(expectedLegacy, actualOff);
        Assert.Equal(Digest(actualOff + "\0share-agent-questions"), actualOn);
        Assert.NotEqual(actualOff, actualOn);
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

    private static string LegacyFingerprint(SessionKey key, BridgeSettings settings)
    {
        var payload = JsonSerializer.Serialize(new
        {
            key,
            Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot)),
            settings.AgentExecutable, settings.AgentArguments, settings.AgentProvider, settings.AgentMode,
            settings.AgentModel, settings.AgentReasoningEffort, settings.AgentPermissionMode,
            settings.AllowAgentNetwork, settings.TrustedWorkspaceRoots, settings.ChatApprovalMode,
            settings.ChatResultMode, settings.ShareApprovalsInChat,
            settings.MattermostAllowedUserIds, settings.SlackAllowedUserIds,
            settings.MattermostChannelId, settings.MattermostAllowedThreadIds,
            settings.SlackAllowedChannelIds, settings.SlackAllowedThreadIds,
            CredentialDigest = Digest(key.Provider == "slack"
                ? settings.SlackBotToken + "\0" + settings.SlackAppToken : settings.MattermostToken)
        });
        return Digest(payload);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
