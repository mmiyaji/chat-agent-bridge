using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed class MattermostWebSocketTransport : IChatTransport
{
    private const int MaxMessageBytes = 1024 * 1024;
    private const string Trigger = "!cab";
    private readonly HttpClient _http = TransportHttp.CreateClient();
    private readonly BoundedSeenSet _seen = new(10_000);
    private string? _currentUserId;
    private Uri? _serverUri;
    private string? _token;
    private bool _disposed;

    public async Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onRequest);
        if (string.IsNullOrWhiteSpace(settings.MattermostServerUrl) || string.IsNullOrWhiteSpace(settings.MattermostToken))
            throw new InvalidOperationException("Mattermost server URL and token are required.");
        if (string.IsNullOrWhiteSpace(settings.MattermostChannelId))
            throw new InvalidOperationException("A Mattermost channel ID is required.");

        var root = TransportHttp.MattermostBaseUri(settings.MattermostServerUrl);
        var token = settings.MattermostToken.Trim();
        _serverUri = root;
        _token = token;
        var meRequest = TransportHttp.AuthenticatedRequest(HttpMethod.Get, new Uri(root, "api/v4/users/me"), token);
        var me = await TransportHttp.SendJsonAsync<MattermostUser>(_http, meRequest, cancellationToken).ConfigureAwait(false);
        _currentUserId = me.Id;

        var socketUri = new UriBuilder(new Uri(root, "api/v4/websocket"))
        {
            Scheme = root.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = root.IsDefaultPort ? -1 : root.Port
        }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, new { seq = 1, action = "authentication_challenge", data = new { token } }, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var document = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                if (!cancellationToken.IsCancellationRequested) throw new InvalidOperationException("Mattermost WebSocket disconnected. Reconnect from the app to continue.");
                return;
            }
            var envelope = document.RootElement;
            if (envelope.TryGetProperty("status", out var authStatus) && authStatus.ValueKind == JsonValueKind.String && authStatus.GetString() != "OK")
                throw new InvalidOperationException("Mattermost WebSocket authentication failed.");
            if (!envelope.TryGetProperty("event", out var eventName) || eventName.GetString() != "posted") continue;
            if (!envelope.TryGetProperty("data", out var data) || !data.TryGetProperty("post", out var postJson) || postJson.ValueKind != JsonValueKind.String) continue;

            var request = ParsePost(postJson.GetString()!, settings.MattermostChannelId, _currentUserId ?? "", isBot: false);
            if (request is null || !_seen.TryAdd(request.MessageId)) continue;

            var authorRequest = TransportHttp.AuthenticatedRequest(HttpMethod.Get,
                new Uri(root, $"api/v4/users/{Uri.EscapeDataString(request.UserId)}"), token);
            MattermostUser author;
            try { author = await TransportHttp.SendJsonAsync<MattermostUser>(_http, authorRequest, cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException) { continue; }
            if (author.IsBot) continue;

            await onRequest(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(request.Provider, "mattermost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Mattermost.", nameof(request));
        var root = _serverUri ?? throw new InvalidOperationException("Mattermost is not connected.");
        var token = _token ?? throw new InvalidOperationException("Mattermost is not connected.");
        using var body = TransportHttp.JsonContent(new { channel_id = request.ChannelId, root_id = request.ThreadId, message = text });
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/posts")) { Content = body };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost reply failed with HTTP {(int)response.StatusCode}.");
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; _http.Dispose(); }
        return ValueTask.CompletedTask;
    }

    internal static ChatRequest? ParsePost(string json, string channelFilter, string currentUserId, bool isBot)
    {
        MattermostPost? post;
        try { post = JsonSerializer.Deserialize<MattermostPost>(json, TransportHttp.JsonOptions); }
        catch (JsonException) { return null; }
        if (post is null || string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.UserId) ||
            !string.Equals(post.ChannelId, channelFilter, StringComparison.Ordinal) ||
            string.Equals(post.UserId, currentUserId, StringComparison.Ordinal) || isBot ||
            string.IsNullOrWhiteSpace(post.Message) || !HasTrigger(post.Message) || IsFromWebhook(post)) return null;
        var text = post.Message[Trigger.Length..].Trim();
        if (text.Length == 0) return null;
        var threadId = string.IsNullOrWhiteSpace(post.RootId) ? post.Id : post.RootId;
        return new ChatRequest("mattermost", post.ChannelId, threadId, post.UserId, text, post.Id);
    }

    private static bool HasTrigger(string text) => text.StartsWith(Trigger, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == Trigger.Length || char.IsWhiteSpace(text[Trigger.Length]));

    private static bool IsFromWebhook(MattermostPost post)
    {
        if (post.Props is not { } props || !props.TryGetProperty("from_webhook", out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
            value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, TransportHttp.JsonOptions));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Mattermost sent a non-text WebSocket message.");
            if (content.Length + result.Count > MaxMessageBytes) throw new InvalidOperationException("Mattermost WebSocket message exceeded the 1 MiB limit.");
            content.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        try { return JsonDocument.Parse(content.ToArray()); }
        catch (JsonException) { return null; }
    }

    private sealed record MattermostUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("is_bot")] bool IsBot);
    private sealed record MattermostPost(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("channel_id")] string ChannelId,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("root_id")] string? RootId,
        [property: JsonPropertyName("props")] JsonElement? Props);
}
