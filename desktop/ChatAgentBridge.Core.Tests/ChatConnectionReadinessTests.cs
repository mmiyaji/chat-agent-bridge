using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatConnectionReadinessTests
{
    [Fact]
    public void NoProviderIsReadyWithDefaultSettings()
    {
        var settings = new BridgeSettings();

        Assert.False(ChatConnectionReadiness.IsReady(settings, "mattermost"));
        Assert.False(ChatConnectionReadiness.IsReady(settings, "slack"));
        Assert.False(ChatConnectionReadiness.IsReady(settings, "discord"));
    }

    [Fact]
    public void MattermostRequiresHttpsOrLoopbackHttpAndAllConnectionFields()
    {
        var complete = new BridgeSettings
        {
            MattermostServerUrl = "https://chat.example.com",
            MattermostToken = "token",
            MattermostChannelId = "channel",
            MattermostAllowedUserIds = "user"
        };

        Assert.True(ChatConnectionReadiness.IsReady(complete, "mattermost"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { MattermostChannelId = "" }, "mattermost"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { MattermostAllowedUserIds = "bad/id" }, "mattermost"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { MattermostServerUrl = "http://chat.example.com" }, "mattermost"));
        Assert.True(ChatConnectionReadiness.IsReady(complete with { MattermostServerUrl = "http://localhost:8065" }, "mattermost"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { MattermostServerUrl = "https://user:password@chat.example.com" }, "mattermost"));
    }

    [Fact]
    public void SlackRequiresBothTokensAndAnAllowedUser()
    {
        var complete = new BridgeSettings
        {
            SlackBotToken = "bot-token",
            SlackAppToken = "app-token",
            SlackAllowedUserIds = "U123"
        };

        Assert.True(ChatConnectionReadiness.IsReady(complete, "slack"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { SlackAppToken = "" }, "slack"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { SlackAllowedUserIds = "invalid/id" }, "slack"));
    }

    [Fact]
    public void DiscordRequiresTokenAllowedUserAndDirectMessagesOrServer()
    {
        var complete = new BridgeSettings
        {
            DiscordBotToken = "token",
            DiscordAllowedUserIds = "123456789012345678",
            DiscordAllowedGuildIds = "223456789012345678"
        };

        Assert.True(ChatConnectionReadiness.IsReady(complete, "discord"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { DiscordAllowedGuildIds = "" }, "discord"));
        Assert.True(ChatConnectionReadiness.IsReady(complete with { DiscordAllowDirectMessages = true, DiscordAllowedGuildIds = "" }, "discord"));
        Assert.False(ChatConnectionReadiness.IsReady(complete with { DiscordAllowedUserIds = "not-a-snowflake" }, "discord"));
    }
}
