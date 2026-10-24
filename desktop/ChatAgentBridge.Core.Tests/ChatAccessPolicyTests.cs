using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatAccessPolicyTests
{
    [Fact]
    public void NormalizeAllowedUsers_SplitsAndDeduplicatesExactIds()
        => Assert.Equal("User_1,ID-2", ChatAccessPolicy.NormalizeAllowedUsers(" User_1;ID-2\nUser_1, "));

    [Fact]
    public void IsAllowed_UsesProviderSpecificOrdinalIdsAndDeniesEmptyOrUnknown()
    {
        var settings = new BridgeSettings { MattermostAllowedUserIds = "userA", SlackAllowedUserIds = "U123" };
        Assert.True(ChatAccessPolicy.IsAllowed(settings, "mattermost", "userA"));
        Assert.False(ChatAccessPolicy.IsAllowed(settings, "mattermost", "UserA"));
        Assert.True(ChatAccessPolicy.IsAllowed(settings, "slack", "U123"));
        Assert.False(ChatAccessPolicy.IsAllowed(settings, "slack", "userA"));
        Assert.False(ChatAccessPolicy.IsAllowed(settings, "slack", ""));
        Assert.False(ChatAccessPolicy.IsAllowed(settings, "other", "userA"));
        Assert.False(ChatAccessPolicy.IsAllowed(new BridgeSettings(), "mattermost", "userA"));
    }

    [Theory]
    [InlineData("@mention")]
    [InlineData("ユーザー")]
    [InlineData("a.b")]
    public void NormalizeAllowedUsers_RejectsInvalidCharacters(string value)
        => Assert.Throws<FormatException>(() => ChatAccessPolicy.NormalizeAllowedUsers(value));

    [Fact]
    public void NormalizeAllowedUsers_RejectsOverlongIdsAndMoreThanOneHundredIds()
    {
        Assert.Throws<FormatException>(() => ChatAccessPolicy.NormalizeAllowedUsers(new string('a', 129)));
        Assert.Throws<FormatException>(() => ChatAccessPolicy.NormalizeAllowedUsers(string.Join("\n", Enumerable.Range(0, 101).Select(i => "u" + i))));
    }

    [Fact]
    public void NormalizeScopes_DeduplicatesAndCapsThreadAllowlistAtOneHundredIds()
    {
        Assert.Equal("root1,root2", ChatAccessPolicy.NormalizeScopes("root1\nroot2\nroot1"));
        Assert.Equal(100, ChatAccessPolicy.NormalizeScopes(string.Join("\n", Enumerable.Range(0, 100).Select(i => $"root{i}")))
            .Split(',').Length);
        Assert.Throws<FormatException>(() => ChatAccessPolicy.NormalizeScopes(
            string.Join("\n", Enumerable.Range(0, 101).Select(i => $"root{i}"))));
    }

    [Fact]
    public void DiscordRequestsRequireAllowedUserAndGuildAndApplyParentChannelAndThreadScopes()
    {
        const string user = "123456789012345678";
        const string guild = "223456789012345678";
        const string parent = "323456789012345678";
        const string thread = "423456789012345678";
        var settings = new BridgeSettings
        {
            DiscordAllowedUserIds = user,
            DiscordAllowedGuildIds = guild,
            DiscordAllowedChannelIds = parent,
            DiscordAllowedThreadIds = thread
        };

        Assert.True(ChatAccessPolicy.IsAllowedRequest(settings, "discord", user, parent, thread, guild));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "discord", "523456789012345678", parent, thread, guild));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "discord", user, parent, thread, "623456789012345678"));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings with { DiscordAllowedGuildIds = "" }, "discord", user, parent, thread, guild));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "discord", user, "723456789012345678", thread, guild));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "discord", user, parent, "823456789012345678", guild));
    }

    [Fact]
    public void DiscordDirectMessagesRequireExplicitOptInAndHonorConfiguredScopes()
    {
        const string user = "123456789012345678";
        const string channel = "323456789012345678";
        var settings = new BridgeSettings { DiscordAllowedUserIds = user };
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "discord", user, channel, channel));
        Assert.True(ChatAccessPolicy.IsAllowedRequest(settings with { DiscordAllowDirectMessages = true }, "discord", user, channel, channel));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings with
        {
            DiscordAllowDirectMessages = true, DiscordAllowedChannelIds = "423456789012345678"
        }, "discord", user, channel, channel));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings with
        {
            DiscordAllowDirectMessages = true, DiscordAllowedGuildIds = "523456789012345678"
        }, "discord", user, channel, channel, "623456789012345678"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("１２３")]
    [InlineData("18446744073709551616")]
    [InlineData("123456789012345678x")]
    public void DiscordSnowflakeNormalizationRejectsInvalidNumbers(string value)
        => Assert.Throws<FormatException>(() => ChatAccessPolicy.NormalizeDiscordSnowflakes(value));
}
