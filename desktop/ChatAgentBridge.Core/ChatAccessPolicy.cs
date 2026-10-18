namespace ChatAgentBridge.Core;

/// <summary>Validates and applies exact user ID allowlists for chat providers.</summary>
public static class ChatAccessPolicy
{
    private const int MaximumUsers = 100;
    private const int MaximumIdLength = 128;

    public static string NormalizeAllowedUsers(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var ids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (ids.Length > MaximumUsers)
            throw new FormatException($"An allowed-user list can contain at most {MaximumUsers} IDs.");
        foreach (var id in ids)
        {
            if (id.Length > MaximumIdLength || id.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
                throw new FormatException("Allowed-user IDs must be at most 128 ASCII letters, digits, underscores, or hyphens.");
        }
        return string.Join(",", ids.Distinct(StringComparer.Ordinal));
    }

    public static bool IsAllowed(BridgeSettings settings, string provider, string userId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrEmpty(userId)) return false;
        var configured = provider switch
        {
            "mattermost" => settings.MattermostAllowedUserIds,
            "slack" => settings.SlackAllowedUserIds,
            _ => null
        };
        if (configured is null) return false;
        return NormalizeAllowedUsers(configured).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(userId, StringComparer.Ordinal);
    }

    public static bool IsAllowedRequest(BridgeSettings settings, string provider, string userId, string channelId, string threadId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsAllowed(settings, provider, userId)) return false;
        var channelScopes = provider == "slack" ? settings.SlackAllowedChannelIds : null;
        var threadScopes = provider switch
        {
            "slack" => settings.SlackAllowedThreadIds,
            "mattermost" => settings.MattermostAllowedThreadIds,
            _ => null
        };
        if (threadScopes is null) return false;
        return (channelScopes is null || ScopeContainsOrUnrestricted(channelScopes, channelId)) &&
            ScopeContainsOrUnrestricted(threadScopes, threadId);
    }

    public static string NormalizeScopes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var ids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        if (ids.Length > MaximumUsers) throw new FormatException($"A scope list can contain at most {MaximumUsers} IDs.");
        foreach (var id in ids)
            if (id.Length > MaximumIdLength || id.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')))
                throw new FormatException("Scope IDs must be at most 128 ASCII letters, digits, underscores, hyphens, or dots.");
        return string.Join(",", ids.Distinct(StringComparer.Ordinal));
    }

    private static bool ScopeContainsOrUnrestricted(string configured, string id)
    {
        var normalized = NormalizeScopes(configured);
        return normalized.Length == 0 || (id.Length > 0 && normalized.Split(',').Contains(id, StringComparer.Ordinal));
    }
}
