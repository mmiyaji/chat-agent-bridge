using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed record ChatChannelChoice(string Id, string Name, string Context, string? Description = null, int? MemberCount = null);
public sealed record ChatChannelDirectoryResult(string ConnectionName, IReadOnlyList<ChatChannelChoice> Channels);

/// <summary>Reads channels visible to the configured bot for the settings picker.</summary>
public static class ChatChannelDirectory
{
    private const int MaxChannels = 2000;

    public static async Task<ChatChannelDirectoryResult> ListAsync(string provider, BridgeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(settings);
        var providerKey = provider.Trim().ToLowerInvariant();
        using var client = TransportHttp.CreateClient(providerKey == "mattermost" && settings.MattermostBypassProxy, timeout: TimeSpan.FromSeconds(20));
        switch (providerKey)
        {
            case "slack":
            {
                var token = settings.SlackBotToken;
                var workspace = await TryGetSlackWorkspaceNameAsync(client, token, cancellationToken).ConfigureAwait(false);
                var channels = await ListSlackAsync(client, token, cancellationToken).ConfigureAwait(false);
                return new ChatChannelDirectoryResult(string.IsNullOrWhiteSpace(workspace) ? "Slack" : $"Slack · {workspace}", channels);
            }
            case "mattermost":
            {
                var channels = await ListMattermostAsync(client, settings, cancellationToken).ConfigureAwait(false);
                var server = TransportHttp.MattermostBaseUri(settings.MattermostServerUrl).Authority;
                return new ChatChannelDirectoryResult($"Mattermost · {server}", channels);
            }
            case "discord":
            {
                var channels = await ListDiscordAsync(client, settings, cancellationToken).ConfigureAwait(false);
                var serverIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedGuildIds)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new ChatChannelDirectoryResult($"Discord · {serverIds.Length} server(s)", channels);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private static async Task<string?> TryGetSlackWorkspaceNameAsync(HttpClient client, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            using var request = TransportHttp.AuthenticatedRequest(HttpMethod.Post, new Uri("https://slack.com/api/auth.test"), token.Trim());
            using var doc = await SendAsync(client, request, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            return root.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? ReadString(root, "team") : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<ChatChannelChoice>> ListSlackAsync(HttpClient client, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("TokenRequired");
        var results = new List<ChatChannelChoice>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var query = "conversations.list?types=public_channel,private_channel&exclude_archived=true&limit=200" +
                (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get, new Uri("https://slack.com/api/" + query), token.Trim());
            using var doc = await SendAsync(client, request, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
                throw new InvalidOperationException(error == "missing_scope" ? "SlackMissingScope" : "ConnectionFailed");
            }
            if (root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in channels.EnumerateArray())
                {
                    var description = ReadString(item, "purpose", "value") ?? ReadString(item, "topic", "value");
                    var memberCount = item.TryGetProperty("num_members", out var members) && members.TryGetInt32(out var count) ? count : (int?)null;
                    Add(results, ReadString(item, "id"), ReadString(item, "name"), ReadString(item, "is_private") == "true" ? "Private channel" : "Public channel", description, memberCount);
                }
            }
            cursor = root.TryGetProperty("response_metadata", out var metadata) ? ReadString(metadata, "next_cursor") : null;
            if (string.IsNullOrEmpty(cursor) || results.Count >= MaxChannels) break;
        }
        return results.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<ChatChannelChoice>> ListMattermostAsync(HttpClient client, BridgeSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.MattermostToken)) throw new InvalidOperationException("TokenRequired");
        Uri root;
        try { root = TransportHttp.MattermostBaseUri(settings.MattermostServerUrl); }
        catch { throw new InvalidOperationException("ServerUrlInvalid"); }
        async Task<JsonDocument> Get(string path)
        {
            var uri = new Uri(root, "api/v4/" + path);
            return await SendAsync(client, TransportHttp.AuthenticatedRequest(HttpMethod.Get, uri, settings.MattermostToken.Trim()), ct).ConfigureAwait(false);
        }

        var results = new List<ChatChannelChoice>();
        using var teams = await Get("users/me/teams").ConfigureAwait(false);
        if (teams.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("ConnectionFailed");
        foreach (var team in teams.RootElement.EnumerateArray())
        {
            var teamId = ReadString(team, "id");
            var teamName = ReadString(team, "display_name");
            if (string.IsNullOrEmpty(teamId)) continue;
            for (var page = 0; page < 10; page++)
            {
                using var channels = await Get($"users/me/teams/{Uri.EscapeDataString(teamId)}/channels?page={page}&per_page=200").ConfigureAwait(false);
                if (channels.RootElement.ValueKind != JsonValueKind.Array) break;
                var count = 0;
                foreach (var item in channels.RootElement.EnumerateArray())
                {
                    count++;
                    Add(results, ReadString(item, "id"), ReadString(item, "display_name"), teamName,
                        ReadString(item, "purpose") ?? ReadString(item, "header"));
                }
                if (count < 200 || results.Count >= MaxChannels) break;
            }
            if (results.Count >= MaxChannels) break;
        }
        return results.OrderBy(c => c.Context, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<ChatChannelChoice>> ListDiscordAsync(HttpClient client, BridgeSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.DiscordBotToken)) throw new InvalidOperationException("TokenRequired");
        string[] guildIds;
        try
        {
            guildIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedGuildIds)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (FormatException) { throw new InvalidOperationException("DiscordGuildIdsInvalid"); }
        if (guildIds.Length == 0) throw new InvalidOperationException("DiscordGuildRequired");
        if (guildIds.Length > 50) throw new InvalidOperationException("DiscordTooManyGuilds");
        var results = new List<ChatChannelChoice>();
        foreach (var guildId in guildIds)
        {
            var uri = new Uri("https://discord.com/api/v10/guilds/" + Uri.EscapeDataString(guildId) + "/channels");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bot " + settings.DiscordBotToken.Trim());
            using var doc = await SendAsync(client, request, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeValue) && typeValue.TryGetInt32(out var number) ? number : -1;
                if (type is not (0 or 5 or 15 or 16)) continue;
                Add(results, ReadString(item, "id"), ReadString(item, "name"), "Server " + guildId, ReadString(item, "topic"));
            }
        }
        return results.OrderBy(c => c.Context, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxChannels).ToArray();
    }

    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("ConnectionFailed");
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidOperationException("ResponseTooLarge");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > 2 * 1024 * 1024) throw new InvalidOperationException("ResponseTooLarge");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            buffer.Position = 0;
            try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
            catch (JsonException) { throw new InvalidOperationException("ConnectionFailed"); }
        }
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    private static string? ReadString(JsonElement element, string parentKey, string childKey) =>
        element.TryGetProperty(parentKey, out var parent) && parent.ValueKind == JsonValueKind.Object
            ? ReadString(parent, childKey)
            : null;

    private static void Add(List<ChatChannelChoice> list, string? id, string? name, string? context, string? description = null, int? memberCount = null)
    {
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && list.Count < MaxChannels)
            list.Add(new ChatChannelChoice(id, name, context ?? "", description, memberCount));
    }
}
