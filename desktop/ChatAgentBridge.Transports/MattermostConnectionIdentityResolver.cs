using System.Text.Json;

namespace ChatAgentBridge.Transports;

/// <summary>Identifies the Mattermost account represented by a configured access token.</summary>
public sealed record MattermostConnectionIdentity(string Id, string Username, string DisplayName, bool IsBot);

public sealed class MattermostConnectionIdentityResolver : IDisposable
{
    private readonly HttpClient _client;

    public MattermostConnectionIdentityResolver(bool bypassProxy = false) =>
        _client = TransportHttp.CreateClient(bypassProxy, timeout: TimeSpan.FromSeconds(12));

    public async Task<MattermostConnectionIdentity> ResolveAsync(string serverUrl, string token,
        CancellationToken cancellationToken = default)
    {
        Uri root;
        try { root = TransportHttp.MattermostBaseUri(serverUrl); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("MattermostServerUrlInvalid");
        }
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("MattermostTokenRequired");

        var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get, new Uri(root, "api/v4/users/me"), token.Trim());
        using var document = await TransportHttp.SendJsonAsync<JsonDocument>(_client, request, cancellationToken).ConfigureAwait(false);
        var user = document.RootElement;
        var id = ReadString(user, "id");
        var username = ReadString(user, "username");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("MattermostIdentityInvalidResponse");

        var displayName = ReadString(user, "nickname");
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = string.Join(' ', new[] { ReadString(user, "first_name"), ReadString(user, "last_name") }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(displayName)) displayName = username;
        var isBot = user.TryGetProperty("is_bot", out var botValue) && botValue.ValueKind == JsonValueKind.True;
        return new MattermostConnectionIdentity(id, username, displayName, isBot);
    }

    public void Dispose() => _client.Dispose();

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
