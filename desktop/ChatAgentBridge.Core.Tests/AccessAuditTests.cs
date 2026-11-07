using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class AccessAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-access-audit-" + Guid.NewGuid().ToString("N"));

    public AccessAuditTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("access_requested", AuditAccessSubject.ChatRequestMethod)]
    [InlineData("access_allowed", AuditAccessSubject.ChatRequestMethod)]
    [InlineData("access_allowed_once", AuditAccessSubject.ChatRequestMethod)]
    [InlineData("access_denied", AuditAccessSubject.ChatRequestMethod)]
    [InlineData("allowed_user_added", AuditAccessSubject.SettingsMethod)]
    [InlineData("allowed_user_removed", AuditAccessSubject.SettingsMethod)]
    public async Task AccessEventsRoundTripWithWhoAndHow(string kind, string method)
    {
        var store = new AuditStore(_directory);
        await store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, null, "slack", "C1", "1.0")
        {
            Access = new AuditAccessSubject("U123", "Alice", "pc-user", method)
        });

        var saved = Assert.Single(await new AuditStore(_directory).ListAsync());
        Assert.Equal(kind, saved.Kind);
        Assert.Equal(new AuditAccessSubject("U123", "Alice", "pc-user", method), saved.Access);
    }

    [Fact]
    public async Task AccessEventsRequireSubjectAndOtherEventsRejectIt()
    {
        var store = new AuditStore(_directory);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "access_allowed", null, "slack", "C1", "1.0")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "received", null, "slack", "C1", "1.0")
            {
                Access = new AuditAccessSubject("U123", null, "pc-user", AuditAccessSubject.ChatRequestMethod)
            }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "access_denied", null, "slack", "C1", "1.0")
            {
                Access = new AuditAccessSubject("", null, "pc-user", AuditAccessSubject.ChatRequestMethod)
            }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "access_denied", null, "slack", "C1", "1.0")
            {
                Access = new AuditAccessSubject("U123", null, "pc-user", "chat")
            }));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public void AllowedUserIsAddedOnceInEachProvidersFormat()
    {
        var settings = new BridgeSettings { SlackAllowedUserIds = "U1", DiscordAllowedUserIds = "", MattermostAllowedUserIds = "" };

        var slack = ChatAccessPolicy.WithAllowedUser(settings, "slack", "U2");
        Assert.Equal(["U1", "U2"], ChatAccessPolicy.AllowedUserIds(slack, "slack"));
        Assert.Same(slack, ChatAccessPolicy.WithAllowedUser(slack, "slack", "U2"));

        var discord = ChatAccessPolicy.WithAllowedUser(settings, "discord", "123456789012345678");
        Assert.Equal(["123456789012345678"], ChatAccessPolicy.AllowedUserIds(discord, "discord"));
        Assert.True(ChatAccessPolicy.IsAllowed(discord, "discord", "123456789012345678"));

        var mattermost = ChatAccessPolicy.WithAllowedUser(settings, "mattermost", "abc123");
        Assert.True(ChatAccessPolicy.IsAllowed(mattermost, "mattermost", "abc123"));
        Assert.Throws<FormatException>(() => ChatAccessPolicy.WithAllowedUser(settings, "discord", "not-a-snowflake"));
    }

    [Fact]
    public void ScopeCheckIgnoresTheUserListButKeepsChannelAndServerLimits()
    {
        var settings = new BridgeSettings
        {
            SlackAllowedUserIds = "", SlackAllowedChannelIds = "C1",
            DiscordAllowedGuildIds = "42", DiscordAllowDirectMessages = false
        };

        Assert.True(ChatAccessPolicy.IsAllowedScope(settings, "slack", "C1", "1.0"));
        Assert.False(ChatAccessPolicy.IsAllowedScope(settings, "slack", "C2", "1.0"));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "slack", "U9", "C1", "1.0"));
        Assert.True(ChatAccessPolicy.IsAllowedScope(settings, "discord", "100", "100", "42"));
        Assert.False(ChatAccessPolicy.IsAllowedScope(settings, "discord", "100", "100", "43"));
        Assert.False(ChatAccessPolicy.IsAllowedScope(settings, "discord", "100", "100", guildId: null));
    }
}
