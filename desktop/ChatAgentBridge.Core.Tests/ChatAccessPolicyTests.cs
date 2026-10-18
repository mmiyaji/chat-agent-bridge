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
}
