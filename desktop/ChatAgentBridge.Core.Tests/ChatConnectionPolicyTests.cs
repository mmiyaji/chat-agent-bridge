using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatConnectionPolicyTests
{
    private static readonly BridgeSettings Mattermost = new()
    {
        MattermostServerUrl = "https://chat.example/base",
        MattermostChannelId = "channel-1",
        MattermostToken = "mm-token"
    };

    private static readonly BridgeSettings Slack = new()
    {
        SlackBotToken = "xoxb-bot",
        SlackAppToken = "xapp-app"
    };

    [Theory]
    [InlineData("execution")]
    [InlineData("followup")]
    [InlineData("allowlist")]
    public void MattermostIgnoresNonConnectionSettings(string setting)
    {
        var changed = setting switch
        {
            "execution" => Mattermost with { AgentExecutable = "other-agent", AgentModel = "model-b", RequireApproval = false },
            "followup" => Mattermost with { ChatFollowUpMode = "steer", ChatResultMode = "auto" },
            "allowlist" => Mattermost with { MattermostAllowedUserIds = "other-user", MattermostAllowedThreadIds = "thread-2" },
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };

        Assert.True(ChatConnectionPolicy.Matches("mattermost", changed, Mattermost));
    }

    [Fact]
    public void MattermostMatchesCanonicalEquivalentOrigins()
    {
        var equivalent = Mattermost with { MattermostServerUrl = "HTTPS://CHAT.EXAMPLE:443/base/" };

        Assert.True(ChatConnectionPolicy.Matches("mattermost", equivalent, Mattermost));
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("token")]
    [InlineData("origin")]
    [InlineData("proxy")]
    public void MattermostConnectionChangesDoNotMatch(string change)
    {
        var changed = change switch
        {
            "channel" => Mattermost with { MattermostChannelId = "channel-2" },
            "token" => Mattermost with { MattermostToken = "other-token" },
            "origin" => Mattermost with { MattermostServerUrl = "https://other.example/base" },
            "proxy" => Mattermost with { MattermostBypassProxy = true },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Assert.False(ChatConnectionPolicy.Matches("mattermost", changed, Mattermost));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("https://chat.example/base?query=value")]
    public void InvalidMattermostOriginFailsClosed(string origin)
    {
        Assert.False(ChatConnectionPolicy.Matches("mattermost", Mattermost with { MattermostServerUrl = origin }, Mattermost));
        Assert.False(ChatConnectionPolicy.Matches("mattermost", Mattermost, Mattermost with { MattermostServerUrl = origin }));
    }

    [Fact]
    public void SlackComparesOnlyItsTwoTokens()
    {
        var changed = Slack with
        {
            MattermostServerUrl = "not a URL",
            MattermostChannelId = "different-channel",
            MattermostToken = "different-token",
            MattermostBypassProxy = true,
            SlackAllowedUserIds = "different-user",
            SlackAllowedChannelIds = "different-channel",
            AgentExecutable = "different-agent",
            ChatFollowUpMode = "steer"
        };

        Assert.True(ChatConnectionPolicy.Matches("slack", changed, Slack));
        Assert.False(ChatConnectionPolicy.Matches("slack", changed with { SlackBotToken = "other-bot" }, Slack));
        Assert.False(ChatConnectionPolicy.Matches("slack", changed with { SlackAppToken = "other-app" }, Slack));
    }

    [Fact]
    public void MattermostIgnoresSlackConnectionFields()
    {
        var changed = Mattermost with { SlackBotToken = "different-bot", SlackAppToken = "different-app" };

        Assert.True(ChatConnectionPolicy.Matches("mattermost", changed, Mattermost));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void UnknownProvidersAreRejected(string provider)
    {
        Assert.False(ChatConnectionPolicy.Matches(provider, Mattermost, Mattermost));
        Assert.False(ChatConnectionPolicy.Matches(provider, Slack, Slack));
    }
}
