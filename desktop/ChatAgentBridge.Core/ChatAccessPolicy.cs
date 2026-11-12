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

    public static bool IsAllowedRequest(BridgeSettings settings, string provider, string userId, string channelId, string threadId, string? guildId = null, bool isDirectMessage = false) =>
        IsAllowed(settings, provider, userId) && IsAllowedScope(settings, provider, channelId, threadId, guildId, isDirectMessage);

    public static bool CanApproveAccessRequest(BridgeSettings current, BridgeSettings connected,
        string provider, string channelId, string threadId, string? guildId, bool isDirectMessage = false) =>
        ChatConnectionPolicy.Matches(provider, current, connected) &&
        IsAllowedScope(current, provider, channelId, threadId, guildId, isDirectMessage) &&
        (provider != "slack" || !isDirectMessage || current.SlackAllowDirectMessages) &&
        (provider != "mattermost" || current.MattermostAllowDirectMessages || string.Equals(current.MattermostChannelId, channelId, StringComparison.Ordinal));

    /// <summary>Checks the server, channel and thread restrictions without the allowed-user list.</summary>
    public static bool IsAllowedScope(BridgeSettings settings, string provider, string channelId, string threadId, string? guildId = null, bool isDirectMessage = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (provider == "slack" && isDirectMessage) return settings.SlackAllowDirectMessages;
        // Mattermost verifies the channel type before setting this flag. A personal DM
        // does not belong to the shared-channel thread allowlist.
        if (provider == "mattermost" && isDirectMessage) return settings.MattermostAllowDirectMessages;
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

    /// <summary>Returns the saved allowed-user IDs for a chat provider.</summary>
    public static IReadOnlyList<string> AllowedUserIds(BridgeSettings settings, string provider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configured = provider switch
        {
            "mattermost" => NormalizeAllowedUsers(settings.MattermostAllowedUserIds),
            "slack" => NormalizeAllowedUsers(settings.SlackAllowedUserIds),
            "discord" => NormalizeDiscordSnowflakes(settings.DiscordAllowedUserIds),
            _ => throw new ArgumentException("Unsupported chat provider.", nameof(provider))
        };
        return configured.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Adds one user to a provider's allowed-user list, keeping the stored list format.</summary>
    public static BridgeSettings WithAllowedUser(BridgeSettings settings, string provider, string userId)
    {
        var ids = AllowedUserIds(settings, provider);
        if (ids.Contains(userId, StringComparer.Ordinal)) return settings;
        var updated = string.Join(Environment.NewLine, ids.Append(userId));
        return provider switch
        {
            "mattermost" => settings with { MattermostAllowedUserIds = NormalizeAllowedUsers(updated) },
            "slack" => settings with { SlackAllowedUserIds = NormalizeAllowedUsers(updated) },
            _ => settings with { DiscordAllowedUserIds = NormalizeDiscordSnowflakes(updated) }
        };
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
