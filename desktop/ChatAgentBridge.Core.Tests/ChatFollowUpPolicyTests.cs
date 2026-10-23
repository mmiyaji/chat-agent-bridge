using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatFollowUpPolicyTests
{
    [Theory]
    [InlineData("folder C:\\work", true)]
    [InlineData("/folder", true)]
    [InlineData("!cab agent claude fresh", true)]
    [InlineData("!cab agent codex summary", true)]
    [InlineData("!cab agent C:\\custom.exe fresh", true)]
    [InlineData("!cab agent のログを調べて", false)]
    [InlineData("次はテストを実行して", false)]
    public void ConfigurationCommandsNeverBecomeLiveAgentInput(string prompt, bool expected) =>
        Assert.Equal(expected, ChatFollowUpPolicy.IsConfigurationCommand(prompt));

    private static readonly SessionKey SlackOwner = new("slack", "C1", "1.0", "U1", "bot");

    [Theory]
    [InlineData("queue", "queue")]
    [InlineData("steer", "steer")]
    [InlineData("unknown", "queue")]
    [InlineData(null, "queue")]
    public void NormalizeUsesQueueAsTheSafeDefault(string? value, string expected) =>
        Assert.Equal(expected, ChatFollowUpPolicy.Normalize(value));

    [Fact]
    public async Task MissingAndUnknownPersistedModesLoadAsQueue()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"),
            "{\"SchemaVersion\":1,\"ChatFollowUpMode\":\"unknown\"}");

        Assert.Equal(ChatFollowUpPolicy.Queue, (await store.LoadAsync()).ChatFollowUpMode);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), "{\"SchemaVersion\":1}");
        Assert.Equal(ChatFollowUpPolicy.Queue, (await store.LoadAsync()).ChatFollowUpMode);
    }

    [Fact]
    public async Task SavePersistsOnlyTheNormalizedMode()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(directory.Path);

        await store.SaveAsync(new BridgeSettings { ChatFollowUpMode = "unknown" });

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(ChatFollowUpPolicy.Queue, json.RootElement.GetProperty("ChatFollowUpMode").GetString());
    }

    [Fact]
    public void CanSteerOnlyWhenBothSettingsAndActiveIdentityAllowIt()
    {
        var current = SteeringSettings;
        var active = SteeringSettings;

        Assert.True(ChatFollowUpPolicy.CanSteer(current, active, SlackOwner, SlackOwner));
        Assert.True(ChatFollowUpPolicy.CanSteer(current, active with { ChatApprovalMode = "auto" }, SlackOwner, SlackOwner));
        var claude = active with { AgentProvider = AgentProfile.Claude, AgentMode = "exec" };
        Assert.True(ChatFollowUpPolicy.CanSteer(claude, claude, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(claude, claude with { ChatApprovalMode = "always" }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(claude, claude, SlackOwner with { UserId = "U2" }, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current with { ChatFollowUpMode = "queue" }, active, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current with { ChatFollowUpMode = "unknown" }, active, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { ChatFollowUpMode = "queue" }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { AgentProvider = AgentProfile.Claude }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { AgentMode = "exec" }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { AgentProvider = AgentProfile.Gemini, AgentMode = "exec" }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { ChatApprovalMode = "always" }, SlackOwner, SlackOwner));
    }

    [Theory]
    [MemberData(nameof(DifferentConversationIdentities))]
    public void CanSteerRequiresTheSameProviderChannelThreadUserAndOrigin(SessionKey incoming, SessionKey active) =>
        Assert.False(ChatFollowUpPolicy.CanSteer(SteeringSettings, SteeringSettings, incoming, active));

    public static IEnumerable<object[]> DifferentConversationIdentities()
    {
        yield return [SlackOwner, SlackOwner with { UserId = "U2" }];
        yield return [SlackOwner, SlackOwner with { ThreadId = "2.0" }];
        yield return [SlackOwner, SlackOwner with { ChannelId = "C2" }];
        yield return [SlackOwner, SlackOwner with { Origin = "app" }];
        yield return [SlackOwner with { Provider = "mattermost" }, SlackOwner];
    }

    private static BridgeSettings SteeringSettings => new()
    {
        ChatFollowUpMode = ChatFollowUpPolicy.Steer,
        AgentProvider = AgentProfile.Codex,
        AgentMode = "app-server",
        ChatApprovalMode = "first"
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    [Theory]
    [InlineData("codex", false, "always", true, true, true)]
    [InlineData("codex", true, "first", true, false, false)]
    [InlineData("codex", true, "auto", true, false, false)]
    [InlineData("codex", false, "first", true, true, false)]
    [InlineData("claude", false, "always", true, false, true)]
    [InlineData("claude", false, "first", true, false, false)]
    [InlineData("gemini", false, "first", false, false, false)]
    [InlineData("custom", false, "always", false, false, false)]
    public void SteerRequirementsListOnlyWhatMustChange(string provider, bool appServer, string approval,
        bool supported, bool needsAppServer, bool needsApproval)
    {
        var plan = ChatFollowUpPolicy.GetSteerRequirements(provider, appServer, approval);
        Assert.Equal(supported, plan.ProviderSupported);
        Assert.Equal(needsAppServer, plan.RequiresAppServer);
        Assert.Equal(needsApproval, plan.RequiresApprovalChange);
        Assert.Equal(needsAppServer || needsApproval, plan.HasChanges);
    }

    [Fact]
    public void SteerApprovalFallbackIsAccepted() =>
        Assert.False(ChatFollowUpPolicy.GetSteerRequirements("claude", false, ChatFollowUpPolicy.SteerApprovalMode).RequiresApprovalChange);
}
