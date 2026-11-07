namespace ChatAgentBridge.Core;

/// <summary>Checks whether saved chat settings contain the fields required to start a provider connection.</summary>
public static class ChatConnectionReadiness
{
    public static bool IsReady(BridgeSettings settings, string provider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return provider switch
        {
            "mattermost" => IsMattermostServerUrlValid(settings.MattermostServerUrl) &&
                !string.IsNullOrWhiteSpace(settings.MattermostToken) &&
                !string.IsNullOrWhiteSpace(settings.MattermostChannelId) &&
                AllowedUsersValid(settings.MattermostAllowedUserIds),
            "slack" => !string.IsNullOrWhiteSpace(settings.SlackBotToken) &&
                !string.IsNullOrWhiteSpace(settings.SlackAppToken) &&
                AllowedUsersValid(settings.SlackAllowedUserIds),
            "discord" => !string.IsNullOrWhiteSpace(settings.DiscordBotToken) &&
                DiscordIdsValid(settings.DiscordAllowedUserIds) &&
                (settings.DiscordAllowDirectMessages || HasDiscordIds(settings.DiscordAllowedGuildIds)),
            _ => false
        };
    }

    /// <summary>Applies the same URL rules used by Mattermost transport startup without making a network request.</summary>
    public static bool IsMattermostServerUrlValid(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host))) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        return true;
    }

    // An empty allowed-user list is valid: unknown users can only ask for access, approved on the PC.
    private static bool AllowedUsersValid(string value)
    {
        try { _ = ChatAccessPolicy.NormalizeAllowedUsers(value); return true; }
        catch (FormatException) { return false; }
    }

    private static bool DiscordIdsValid(string value)
    {
        try { _ = ChatAccessPolicy.NormalizeDiscordSnowflakes(value); return true; }
        catch (FormatException) { return false; }
    }

    private static bool HasDiscordIds(string value)
    {
        try { return ChatAccessPolicy.NormalizeDiscordSnowflakes(value).Length > 0; }
        catch (FormatException) { return false; }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}
