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
        WorkspaceRoot = Path.GetTempPath(), ChatApprovalMode = "first", ChatResultMode = "manual", SlackAllowedUserIds = "U1",
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
        Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { ChatResultMode = "auto" }, binding));
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { SlackBotToken = "changed" }, binding));
        Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, settings with { AgentModel = "different" }, binding));
    }

    [Fact]
    public void AlwaysAndAutomaticApprovalRemainSeparateFromToolPermissions()
    {
        Assert.True(ConversationExecutionPolicy.RequiresApproval(Owner, Settings with { ChatApprovalMode = "always" }, null));
        Assert.False(ConversationExecutionPolicy.RequiresApproval(Owner, Settings with { ChatApprovalMode = "auto", AgentPermissionMode = "read-only" }, null));
    }

    [Fact]
    public void SwitchCommandsAreEligibleForFirstOrAutomaticApprovalOnlyWhenOptedIn()
    {
        Assert.False(ConversationExecutionPolicy.AutoApprovesSwitchCommands(Settings with { ChatApprovalMode = "auto" }));
        Assert.True(ConversationExecutionPolicy.AutoApprovesSwitchCommands(Settings with { ChatApprovalMode = "auto", AutoApproveSwitchCommands = true }));
        Assert.True(ConversationExecutionPolicy.AutoApprovesSwitchCommands(Settings with { ChatApprovalMode = "first", AutoApproveSwitchCommands = true }));
        Assert.False(ConversationExecutionPolicy.AutoApprovesSwitchCommands(Settings with { ChatApprovalMode = "always", AutoApproveSwitchCommands = true }));
    }

    [Fact]
    public void SwitchCommandAutoApprovalDoesNotChangeTheFirstApprovalFingerprint()
    {
        // Existing first-approval consents must survive the new setting's default.
        Assert.Equal(ConversationExecutionPolicy.GetFingerprint(Owner, Settings),
            ConversationExecutionPolicy.GetFingerprint(Owner, Settings with { AutoApproveSwitchCommands = true }));
    }

    [Fact]
    public void SwitchCommandAutoApprovalIsASensitiveChatAccessChange()
    {
        var changes = SettingsSensitiveChanges.Compare(Settings with { ChatApprovalMode = "auto" },
            Settings with { ChatApprovalMode = "auto", AutoApproveSwitchCommands = true });
        Assert.True(changes.ChatAccess);
        Assert.True(SettingsDraftChanges.Compare(Settings, Settings with { AutoApproveSwitchCommands = true }).Chat);
    }

    [Fact]
    public void FirstApprovalSurvivesChangesThatDoNotAffectWhatTheCliMayDo()
    {
        var settings = Settings;
        var fingerprint = ConversationExecutionPolicy.GetFingerprint(Owner, settings);
        BridgeSettings[] unchanged =
        [
            settings with { AgentModel = "other-model", AgentReasoningEffort = "high" },
            settings with { ChatResultMode = "auto" },
            settings with { ChatApprovalMode = "always" },
            settings with { ChatFollowUpMode = ChatFollowUpPolicy.Steer },
            settings with { ShareApprovalsInChat = true, ShareAgentQuestionsInChat = true },
            settings with { SlackAllowedUserIds = "U1\nU9", SlackAllowedChannelIds = "C1", SlackAllowedThreadIds = "1.0" },
            settings with { MattermostAllowedUserIds = "user", MattermostAllowedThreadIds = "root" }
        ];
        foreach (var changed in unchanged)
            Assert.Equal(fingerprint, ConversationExecutionPolicy.GetFingerprint(Owner, changed));
    }

    [Fact]
    public void FirstApprovalIsRenewedWhenExecutionScopeChanges()
    {
        var settings = Settings;
        var fingerprint = ConversationExecutionPolicy.GetFingerprint(Owner, settings);
        BridgeSettings[] changedScopes =
        [
            settings with { WorkspaceRoot = Path.Combine(Path.GetTempPath(), "other") },
            settings with { AgentExecutable = "other-codex" },
            settings with { AgentArguments = "exec --json --other -" },
            settings with { AgentProvider = AgentProfile.Claude },
            settings with { AgentMode = "app-server" },
            settings with { AgentPermissionMode = "full-access" },
            settings with { AllowAgentNetwork = true },
            settings with { TrustedWorkspaceRoots = Path.GetTempPath() },
            settings with { SlackBotToken = "rotated" }
        ];
        foreach (var changed in changedScopes)
            Assert.NotEqual(fingerprint, ConversationExecutionPolicy.GetFingerprint(Owner, changed));
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
