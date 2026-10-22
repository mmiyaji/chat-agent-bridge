using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatFollowUpPolicyTests
{
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
        Assert.False(ChatFollowUpPolicy.CanSteer(current with { ChatFollowUpMode = "queue" }, active, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current with { ChatFollowUpMode = "unknown" }, active, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { ChatFollowUpMode = "queue" }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { AgentProvider = AgentProfile.Claude }, SlackOwner, SlackOwner));
        Assert.False(ChatFollowUpPolicy.CanSteer(current, active with { AgentMode = "exec" }, SlackOwner, SlackOwner));
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
}
