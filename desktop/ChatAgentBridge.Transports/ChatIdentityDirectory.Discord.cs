using System.Net.Http.Headers;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed partial class ChatIdentityDirectory
{
    private async Task ResolveDiscordAsync(BridgeSettings settings, SessionKey[] keys,
        Dictionary<CacheKey, CacheEntry> store, RequestBudget budget, CancellationToken token)
    {
        foreach (var key in keys)
        {
            if (budget.Limited || token.IsCancellationRequested) break;
            if (!IsDiscordId(key.ChannelId) || !IsDiscordId(key.UserId) ||
                !ChatAccessPolicy.IsAllowed(settings, "discord", key.UserId!)) continue;
            var channelKey = new CacheKey("discord", "discord", "channel", key.ChannelId);
            if (NeedsRefresh(store, channelKey))
            {
                CacheEntry? resolvedChannel = null;
                await FetchAndStoreAsync(store, channelKey, "Discord", null, false, async () =>
                {
                    using var channel = await DiscordJsonAsync($"channels/{key.ChannelId}", settings.DiscordBotToken, budget, token);
                    var data = channel.RootElement;
                    if (!IdMatches(data, "id", key.ChannelId)) return null;
                    var guild = data.TryGetProperty("guild_id", out var guildValue) && guildValue.ValueKind == JsonValueKind.String
                        ? guildValue.GetString() : null;
                    if (!ChatAccessPolicy.IsAllowedRequest(settings, "discord", key.UserId!, key.ChannelId, key.ThreadId, guild))
                        return null;
                    var direct = data.TryGetProperty("type", out var type) && type.TryGetInt32(out var channelType) && channelType == 1;
                    if (guild is null && !direct) return null;
                    var site = "Discord";
                    if (IsDiscordId(guild))
                    {
                        var guildKey = new CacheKey("discord", "discord", "team", guild!);
                        if (NeedsRefresh(store, guildKey))
                            await FetchAndStoreAsync(store, guildKey, "Discord", null, false, async () =>
                            {
                                using var document = await DiscordJsonAsync($"guilds/{guild}", settings.DiscordBotToken, budget, token);
                                return IdMatches(document.RootElement, "id", guild!) ? SafeLabel(document.RootElement, "name") : null;
                            }, token);
                        site = GetFreshOrStale(store, "discord", "discord", "team", guild!)?.SiteName ?? "Discord";
                    }
                    // FetchAndStore owns the ordinary label update; enrich the site/direct fields afterward.
                    var label = direct ? "Direct message" : SafeLabel(data, "name");
                    resolvedChannel = Entry(channelKey, site, label, direct, true);
                    return label;
                }, token);
                if (resolvedChannel is not null) store[channelKey] = resolvedChannel;
            }
            if (budget.Limited) break;
            var userKey = new CacheKey("discord", "discord", "user", key.UserId!);
            if (!NeedsRefresh(store, userKey)) continue;
            await FetchAndStoreAsync(store, userKey, null, null, false, async () =>
            {
                using var document = await DiscordJsonAsync($"users/{key.UserId}", settings.DiscordBotToken, budget, token);
                return IdMatches(document.RootElement, "id", key.UserId!)
                    ? SafeLabel(document.RootElement, "global_name") ?? SafeLabel(document.RootElement, "username") : null;
            }, token);
        }
    }

    private async Task<JsonDocument> DiscordJsonAsync(string path, string token, RequestBudget budget, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://discord.com/api/v10/" + path));
        DiscordHttp.ConfigureRequest(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token.Trim());
        return await SendJsonAsync(request, GetDefaultClient(ref _http), budget, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsDiscordId(string? value) => value is { Length: > 0 and <= 20 } &&
        value.All(char.IsAsciiDigit) && ulong.TryParse(value, out var id) && id != 0;
}
