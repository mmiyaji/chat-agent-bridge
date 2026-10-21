using System.Reflection;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsSensitiveChangesTests
{
    public static IEnumerable<object[]> ClassifiedSettings()
    {
        foreach (var name in new[] { nameof(BridgeSettings.AgentExecutable), nameof(BridgeSettings.AgentArguments),
                     nameof(BridgeSettings.AgentProvider), nameof(BridgeSettings.AgentMode) })
            yield return [name, nameof(SettingsSensitiveChanges.CliCommand)];
        foreach (var name in new[] { nameof(BridgeSettings.AgentPermissionMode), nameof(BridgeSettings.AllowAgentNetwork),
                     nameof(BridgeSettings.RequireApproval) })
            yield return [name, nameof(SettingsSensitiveChanges.AgentPermissions)];
        yield return [nameof(BridgeSettings.TrustedWorkspaceRoots), nameof(SettingsSensitiveChanges.TrustedWorkspaces)];
        foreach (var name in new[] { nameof(BridgeSettings.ChatApprovalMode), nameof(BridgeSettings.SlackAllowedChannelIds),
                     nameof(BridgeSettings.SlackAllowedThreadIds), nameof(BridgeSettings.MattermostAllowedThreadIds),
                     nameof(BridgeSettings.MattermostAllowedUserIds), nameof(BridgeSettings.SlackAllowedUserIds) })
            yield return [name, nameof(SettingsSensitiveChanges.ChatAccess)];
        foreach (var name in new[] { nameof(BridgeSettings.ChatResultMode), nameof(BridgeSettings.ShareApprovalsInChat),
                     nameof(BridgeSettings.ShareAgentQuestionsInChat) })
            yield return [name, nameof(SettingsSensitiveChanges.ChatSharing)];
        foreach (var name in new[] { nameof(BridgeSettings.FileAttachmentApprovalMode), nameof(BridgeSettings.FileAttachmentMaxSizeMb),
                     nameof(BridgeSettings.FileAttachmentMaxCount), nameof(BridgeSettings.FileAttachmentMaxTotalSizeMb) })
            yield return [name, nameof(SettingsSensitiveChanges.FileAttachments)];
        foreach (var name in new[] { nameof(BridgeSettings.MattermostServerUrl), nameof(BridgeSettings.MattermostChannelId),
                     nameof(BridgeSettings.MattermostToken), nameof(BridgeSettings.SlackBotToken), nameof(BridgeSettings.SlackAppToken) })
            yield return [name, nameof(SettingsSensitiveChanges.ConnectionIdentity)];
        foreach (var name in new[] { nameof(BridgeSettings.WorkspaceRoot), nameof(BridgeSettings.AgentModel),
                     nameof(BridgeSettings.AgentReasoningEffort), nameof(BridgeSettings.AutoConnectProvider),
                     nameof(BridgeSettings.MattermostBypassProxy), nameof(BridgeSettings.PostConnectionNotice) })
            yield return [name, ""];
    }

    [Fact]
    public void EveryPersistedSettingRequiresAnExplicitSensitivityDecision()
    {
        var classified = ClassifiedSettings().Select(row => (string)row[0]).ToArray();
        var persisted = typeof(BridgeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);
        Assert.Equal(classified.Length, classified.Distinct().Count());
        Assert.Equal(persisted.Order(), classified.Order());
    }

    [Theory]
    [MemberData(nameof(ClassifiedSettings))]
    public void EachSettingChangesOnlyItsExpectedConfirmationCategory(string propertyName, string category)
    {
        var saved = new BridgeSettings();
        var draft = saved with { };
        var property = typeof(BridgeSettings).GetProperty(propertyName)!;
        var original = property.GetValue(saved);
        object replacement = property.PropertyType == typeof(bool) ? !(bool)original! :
            property.PropertyType == typeof(int) ? (int)original! + 1 : "changed";
        property.SetValue(draft, replacement);

        var actual = SettingsSensitiveChanges.Compare(saved, draft);
        var categories = typeof(SettingsSensitiveChanges).GetProperties()
            .Where(item => item.PropertyType == typeof(bool) && item.Name != nameof(SettingsSensitiveChanges.HasChanges))
            .Where(item => (bool)item.GetValue(actual)!).Select(item => item.Name).ToArray();

        Assert.Equal(category.Length == 0 ? [] : new[] { category }, categories);
        Assert.Equal(category.Length > 0, actual.HasChanges);
        Assert.Equal(category.Length == 0, SettingsSensitiveChanges.Equivalent(saved, draft));
        property.SetValue(draft, original);
        Assert.True(SettingsSensitiveChanges.Equivalent(saved, draft));
    }

    [Fact]
    public void OrdinaryEditsCannotInvalidateAnUnconfirmedSensitiveProjection()
    {
        var saved = new BridgeSettings();
        var rejected = saved with { AgentPermissionMode = "full-access", SlackBotToken = "draft-token" };
        var ordinaryEdit = rejected with
        {
            AgentModel = "model", AgentReasoningEffort = "high", WorkspaceRoot = "another-workspace",
            AutoConnectProvider = "slack", MattermostBypassProxy = true, PostConnectionNotice = true
        };

        Assert.True(SettingsSensitiveChanges.Equivalent(rejected, ordinaryEdit));
        Assert.True(SettingsSensitiveChanges.Compare(saved, ordinaryEdit).HasChanges);
        Assert.False(SettingsSensitiveChanges.Equivalent(rejected, ordinaryEdit with { AllowAgentNetwork = true }));
        Assert.True(SettingsSensitiveChanges.Equivalent(saved, ordinaryEdit with
        {
            AgentPermissionMode = saved.AgentPermissionMode, SlackBotToken = saved.SlackBotToken
        }));
    }

    [Fact]
    public void EffectiveCommandAndDestinationWhitespaceDoesNotRequireConfirmation()
    {
        var saved = new BridgeSettings { MattermostServerUrl = "https://example.com", MattermostChannelId = "channel" };
        var draft = saved with
        {
            AgentExecutable = " codex ", AgentArguments = " exec --json - ",
            MattermostServerUrl = " https://example.com ", MattermostChannelId = " channel "
        };
        Assert.True(SettingsSensitiveChanges.Equivalent(saved, draft));
    }

    [Fact]
    public void CategoryResultContainsNoCredentialOrSettingsValues()
    {
        var changes = SettingsSensitiveChanges.Compare(new(), new()
        {
            MattermostToken = "private-mattermost-token", SlackBotToken = "private-slack-token",
            SlackAppToken = "private-app-token", TrustedWorkspaceRoots = "private-workspace"
        });

        Assert.All(typeof(SettingsSensitiveChanges).GetProperties(), property => Assert.Equal(typeof(bool), property.PropertyType));
        Assert.DoesNotContain("private", changes.ToString(), StringComparison.Ordinal);
        Assert.True(changes.ConnectionIdentity);
        Assert.True(changes.TrustedWorkspaces);
    }
}
