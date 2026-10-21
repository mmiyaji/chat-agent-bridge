using System.Reflection;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsDraftChangesTests
{
    private static readonly string[] AgentProperties =
    [
        nameof(BridgeSettings.WorkspaceRoot), nameof(BridgeSettings.AgentExecutable),
        nameof(BridgeSettings.AgentArguments), nameof(BridgeSettings.AgentMode),
        nameof(BridgeSettings.AgentProvider), nameof(BridgeSettings.AgentModel),
        nameof(BridgeSettings.AgentReasoningEffort), nameof(BridgeSettings.RequireApproval),
        nameof(BridgeSettings.AgentPermissionMode), nameof(BridgeSettings.AllowAgentNetwork),
        nameof(BridgeSettings.TrustedWorkspaceRoots)
    ];

    private static readonly string[] ChatProperties =
    [
        nameof(BridgeSettings.MattermostServerUrl), nameof(BridgeSettings.MattermostChannelId),
        nameof(BridgeSettings.MattermostToken), nameof(BridgeSettings.MattermostBypassProxy),
        nameof(BridgeSettings.PostConnectionNotice), nameof(BridgeSettings.SlackBotToken),
        nameof(BridgeSettings.SlackAppToken), nameof(BridgeSettings.MattermostAllowedUserIds),
        nameof(BridgeSettings.SlackAllowedUserIds), nameof(BridgeSettings.ShareApprovalsInChat),
        nameof(BridgeSettings.ShareAgentQuestionsInChat), nameof(BridgeSettings.ChatApprovalMode),
        nameof(BridgeSettings.ChatResultMode), nameof(BridgeSettings.SlackAllowedChannelIds),
        nameof(BridgeSettings.SlackAllowedThreadIds), nameof(BridgeSettings.MattermostAllowedThreadIds),
        nameof(BridgeSettings.FileAttachmentApprovalMode), nameof(BridgeSettings.FileAttachmentMaxSizeMb),
        nameof(BridgeSettings.FileAttachmentMaxCount), nameof(BridgeSettings.FileAttachmentMaxTotalSizeMb)
    ];

    public static IEnumerable<object[]> SettingsProperties()
    {
        foreach (var property in AgentProperties) yield return [property, true, false, false];
        foreach (var property in ChatProperties) yield return [property, false, true, false];
        yield return [nameof(BridgeSettings.AutoConnectProvider), false, false, true];
    }

    [Fact]
    public void EveryPersistedSettingMustHaveAnExplicitChangeGroup()
    {
        var classified = SettingsProperties().Select(row => (string)row[0]).ToArray();
        var persisted = typeof(BridgeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name).ToArray();
        Assert.Equal(classified.Length, classified.Distinct().Count());
        Assert.Equal(persisted.Order(), classified.Order());
    }

    [Theory]
    [MemberData(nameof(SettingsProperties))]
    public void EachIndividualSettingChangesOnlyItsOwnGroup(string propertyName, bool agent, bool chat, bool general)
    {
        var saved = new BridgeSettings();
        var draft = saved with { };
        var property = typeof(BridgeSettings).GetProperty(propertyName)!;
        var original = property.GetValue(draft);
        object replacement = property.PropertyType == typeof(bool) ? !(bool)original! :
            property.PropertyType == typeof(int) ? (int)original! + 1 : "changed";
        property.SetValue(draft, replacement);

        var changes = SettingsDraftChanges.Compare(saved, draft);

        Assert.Equal(new SettingsDraftChanges(agent, chat, general), changes);
        Assert.True(changes.HasChanges);
        property.SetValue(draft, original);
        Assert.False(SettingsDraftChanges.Compare(saved, draft).HasChanges);
    }

    [Fact]
    public void UnchangedValuesAndEffectiveWhitespaceNormalizationStayClean()
    {
        var saved = new BridgeSettings
        {
            WorkspaceRoot = " workspace ", AgentExecutable = " codex ", AgentArguments = " exec --json - ",
            AgentModel = " model ", AgentReasoningEffort = " high ",
            MattermostServerUrl = " https://example.com ", MattermostChannelId = " channel "
        };
        var normalized = saved with
        {
            WorkspaceRoot = "workspace", AgentExecutable = "codex", AgentArguments = "exec --json -",
            AgentModel = "model", AgentReasoningEffort = "high",
            MattermostServerUrl = "https://example.com", MattermostChannelId = "channel"
        };

        Assert.False(SettingsDraftChanges.Compare(saved, saved with { }).HasChanges);
        Assert.False(SettingsDraftChanges.Compare(saved, normalized).HasChanges);
        Assert.False(SettingsDraftChanges.Compare(new(), new() { AgentModel = " ", AgentReasoningEffort = " " }).HasChanges);
    }

    [Theory]
    [InlineData(12.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RawInvalidAttachmentNumbersCannotDisappearThroughDraftClamping(double value)
    {
        var saved = new BridgeSettings { FileAttachmentMaxSizeMb = 12 };
        Assert.Equal(new SettingsDraftChanges(false, true, false),
            SettingsDraftChanges.Compare(saved, saved, value, saved.FileAttachmentMaxCount, saved.FileAttachmentMaxTotalSizeMb));
        Assert.True(SettingsDraftChanges.Compare(saved, saved, 12, value, saved.FileAttachmentMaxTotalSizeMb).Chat);
        Assert.True(SettingsDraftChanges.Compare(saved, saved, 12, saved.FileAttachmentMaxCount, value).Chat);
    }

    [Fact]
    public async Task SavingOnlyGeneralSettingsPreservesAgentAndChatDrafts()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var saved = new BridgeSettings();
        await store.SaveAsync(saved);
        var draft = saved with { AgentModel = "model", SlackBotToken = "draft-token", AutoConnectProvider = "slack" };

        var persisted = await store.LoadAsync();
        await store.SaveAsync(persisted with { AutoConnectProvider = draft.AutoConnectProvider });
        persisted = await store.LoadAsync();

        Assert.Equal(new SettingsDraftChanges(true, true, false), SettingsDraftChanges.Compare(persisted, draft));
        Assert.Null(persisted.AgentModel);
        Assert.Empty(persisted.SlackBotToken);
    }

    [Fact]
    public async Task RejectedSaveKeepsPersistedBaselineAndDraftChanges()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var saved = new BridgeSettings();
        await store.SaveAsync(saved);
        var draft = saved with { AgentModel = "model", FileAttachmentMaxSizeMb = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(draft));

        Assert.Equal(saved, await store.LoadAsync());
        Assert.Equal(new SettingsDraftChanges(true, true, false), SettingsDraftChanges.Compare(saved, draft));
        Assert.False(SettingsDraftChanges.Compare(saved, await store.LoadAsync()).HasChanges);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
