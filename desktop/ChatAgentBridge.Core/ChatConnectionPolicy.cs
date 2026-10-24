using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Determines whether live settings still identify the configured chat connection.</summary>
public static class ChatConnectionPolicy
{
    /// <summary>
    /// Compares only provider connection identity. Request execution, follow-up, model,
    /// permissions, and access-list settings are intentionally evaluated separately.
    /// </summary>
    public static bool Matches(string provider, BridgeSettings current, BridgeSettings connected)
    {
        if (current is null || connected is null) return false;

        return provider switch
        {
            "mattermost" => MatchesMattermost(current, connected),
            "slack" => string.Equals(current.SlackBotToken, connected.SlackBotToken, StringComparison.Ordinal) &&
                string.Equals(current.SlackAppToken, connected.SlackAppToken, StringComparison.Ordinal),
            "discord" => string.Equals(current.DiscordBotToken, connected.DiscordBotToken, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool MatchesMattermost(BridgeSettings current, BridgeSettings connected)
    {
        if (!string.Equals(current.MattermostChannelId, connected.MattermostChannelId, StringComparison.Ordinal) ||
            !string.Equals(current.MattermostToken, connected.MattermostToken, StringComparison.Ordinal) ||
            current.MattermostBypassProxy != connected.MattermostBypassProxy)
            return false;

        try
        {
            var currentOrigin = ChatSessionScope.Create("mattermost", "", "", "connection", current.MattermostServerUrl).Origin;
            var connectedOrigin = ChatSessionScope.Create("mattermost", "", "", "connection", connected.MattermostServerUrl).Origin;
            return string.Equals(currentOrigin, connectedOrigin, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // Invalid or missing server URLs cannot establish a connection identity.
            return false;
        }
    }
}
