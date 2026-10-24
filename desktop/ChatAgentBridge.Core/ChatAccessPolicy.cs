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
            "discord" => NormalizeDiscordSnowflakes(settings.DiscordAllowedUserIds),
            _ => null
        };
        if (configured is null) return false;
        return NormalizeAllowedUsers(configured).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(userId, StringComparer.Ordinal);
    }

    public static bool IsAllowedRequest(BridgeSettings settings, string provider, string userId, string channelId, string threadId, string? guildId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsAllowed(settings, provider, userId)) return false;
        if (provider == "discord")
        {
            if (guildId is null)
            {
                if (!settings.DiscordAllowDirectMessages) return false;
            }
            else
            {
                var guilds = NormalizeDiscordSnowflakes(settings.DiscordAllowedGuildIds);
                if (guilds.Length == 0 || !Contains(guilds, guildId)) return false;
            }
            return ScopeContainsOrUnrestrictedDiscord(settings.DiscordAllowedChannelIds, channelId) &&
                ScopeContainsOrUnrestrictedDiscord(settings.DiscordAllowedThreadIds, threadId);
        }
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

    /// <summary>Normalizes Discord snowflakes, accepting only nonzero unsigned 64-bit decimal IDs.</summary>
    public static string NormalizeDiscordSnowflakes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var ids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        if (ids.Length > MaximumUsers) throw new FormatException($"A Discord ID list can contain at most {MaximumUsers} IDs.");
        var normalized = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (id.Length > 20 || id.Any(c => c is < '0' or > '9') ||
                !ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
                throw new FormatException("Discord IDs must be nonzero unsigned 64-bit decimal snowflakes.");
            normalized.Add(parsed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(",", normalized.Distinct(StringComparer.Ordinal));
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

    private static bool ScopeContainsOrUnrestrictedDiscord(string configured, string id)
    {
        var normalized = NormalizeDiscordSnowflakes(configured);
        return normalized.Length == 0 || Contains(normalized, id);
    }

    private static bool Contains(string normalized, string id) =>
        id.Length > 0 && normalized.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(id, StringComparer.Ordinal);
}
