using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed class SlackSocketModeTransport : IChatTransport, IChatProgressTransport, IChatConnectionEvents
{
    public event Action? Connected;
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly Uri _apiBase;
    private readonly HttpClient _http;
    private readonly BoundedSeenSet _seen = new(10_000);
    private string? _botToken;
    private string? _botUserId;
    private bool _disposed;
    private bool _connected;
    private long _connectionGeneration;
    private readonly object _progressOwner = new();

    public SlackSocketModeTransport() : this(TransportHttp.CreateClient(), new Uri("https://slack.com/api/")) { }

    internal SlackSocketModeTransport(HttpClient http, Uri apiBase, string? token = null)
    {
        _http = http;
        _apiBase = apiBase;
        _botToken = token;
        if (token is not null)
        {
            _connected = true;
            _connectionGeneration = 1;
        }
    }

    public async Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _connectionGeneration);
        _connected = false;
        ArgumentNullException.ThrowIfNull(onRequest);
        if (string.IsNullOrWhiteSpace(settings.SlackBotToken) || string.IsNullOrWhiteSpace(settings.SlackAppToken))
            throw new InvalidOperationException("Slack bot and app tokens are required for Socket Mode.");
        _botToken = settings.SlackBotToken.Trim();
        var sessions = new SessionStore();
        var appToken = settings.SlackAppToken.Trim();
        var identity = await SlackApiAsync<AuthTestResponse>("auth.test", settings.SlackBotToken.Trim(), null, cancellationToken).ConfigureAwait(false);
        if (!identity.Ok || string.IsNullOrWhiteSpace(identity.UserId)) throw new InvalidOperationException("Slack bot token validation failed.");
        _botUserId = identity.UserId;

        var open = await SlackApiAsync<ConnectionsOpenResponse>("apps.connections.open", appToken, new { }, cancellationToken).ConfigureAwait(false);
        if (!open.Ok || string.IsNullOrWhiteSpace(open.Url) || !Uri.TryCreate(open.Url, UriKind.Absolute, out var socketUri) || socketUri.Scheme != "wss")
            throw new InvalidOperationException("Slack Socket Mode connection could not be opened.");

        using var socket = new ClientWebSocket();
        try { await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (WebSocketException) { throw new InvalidOperationException("Slack Socket Mode connection failed."); }
        _connected = true;
        Connected?.Invoke();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            JsonDocument? received;
            try { received = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (WebSocketException) { throw new InvalidOperationException("Slack Socket Mode connection failed."); }
            using var document = received;
            if (document is null)
            {
                if (!cancellationToken.IsCancellationRequested) throw new InvalidOperationException("Slack Socket Mode disconnected. Reconnect from the app to continue.");
                return;
            }
            var envelope = document.RootElement;
            var type = TryString(envelope, "type", out var envelopeType) ? envelopeType : "";
            if (type == "disconnect")
                throw new InvalidOperationException("Slack closed the Socket Mode connection. Reconnect from the app to continue.");
            if (!TryString(envelope, "envelope_id", out var envelopeId)) continue;
            if (type == "events_api")
            {
                await SendAsync(socket, new { envelope_id = envelopeId }, cancellationToken).ConfigureAwait(false);
                if (!envelope.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("event", out var evt)) continue;
                var request = await ParseRequestAsync(evt, _botUserId, settings, sessions, cancellationToken).ConfigureAwait(false);
                if (request is null || !_seen.TryAdd(request.MessageId)) continue;
                await onRequest(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(request.Provider, "slack", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Slack.", nameof(request));
        var token = _botToken ?? throw new InvalidOperationException("Slack is not connected.");
        return PostReplyAsync(request, text, token, cancellationToken);
    }

    public async Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ValidateProgressRequest(request);
        var token = _botToken!;
        var result = await SlackApiAsync<SlackPostResponse>("chat.postMessage", token,
            new { channel = request.ChannelId, thread_ts = request.ThreadId, text }, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Timestamp) ||
            !string.Equals(result.Channel, request.ChannelId, StringComparison.Ordinal) ||
            result.Message?.ThreadTimestamp is { } postedThread && !string.Equals(postedThread, request.ThreadId, StringComparison.Ordinal) ||
            string.Equals(result.Timestamp, request.MessageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Slack could not post the progress message.");
        return new ChatProgressMessage("slack", request.ChannelId, request.ThreadId, request.UserId,
            result.Timestamp, _progressOwner, Volatile.Read(ref _connectionGeneration));
    }

    public async Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken)
    {
        ValidateProgressHandle(request, message, "slack");
        var result = await SlackApiAsync<SlackOkResponse>("chat.update", _botToken!,
            new { channel = request.ChannelId, ts = message.MessageId, text }, cancellationToken).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException("Slack could not update the progress message.");
    }

    private void ValidateProgressRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(request.Provider, "slack", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Slack.", nameof(request));
        if (!_connected || _botToken is null) throw new InvalidOperationException("Slack is not connected.");
    }

    private void ValidateProgressHandle(ChatRequest request, ChatProgressMessage message, string provider)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateProgressRequest(request);
        if (!ReferenceEquals(message.Owner, _progressOwner) || message.ConnectionGeneration != Volatile.Read(ref _connectionGeneration) ||
            !string.Equals(message.Provider, provider, StringComparison.Ordinal) ||
            !string.Equals(message.ChannelId, request.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(message.ThreadId, request.ThreadId, StringComparison.Ordinal) ||
            !string.Equals(message.RequesterId, request.UserId, StringComparison.Ordinal))
            throw new InvalidOperationException("Progress message does not belong to this request and active connection.");
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; _connected = false; _http.Dispose(); }
        return ValueTask.CompletedTask;
    }

    internal static ChatRequest? ParseRequest(JsonElement evt, string? botUserId, bool allowPlainThreadReplies = false)
    {
        if (!TryString(evt, "type", out var eventType) || !TryString(evt, "channel", out var channel) ||
            !TryString(evt, "user", out var user) || !TryString(evt, "text", out var text) ||
            !TryString(evt, "ts", out var timestamp)) return null;
        if (string.Equals(user, botUserId, StringComparison.Ordinal) || evt.TryGetProperty("bot_id", out _)) return null;
        if (evt.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(subtype.GetString())) return null;
        if (eventType != "app_mention" && eventType != "message") return null;

        var threadTimestamp = TryString(evt, "thread_ts", out var thread) ? thread : null;
        var isThreadReply = !string.IsNullOrWhiteSpace(threadTimestamp) && threadTimestamp != timestamp;
        var mentionsBot = ContainsBotMention(text, botUserId);
        var hasTrigger = StartsWithTrigger(text);
        if (eventType == "message" && (!isThreadReply || (!allowPlainThreadReplies && !hasTrigger && !mentionsBot))) return null;
        if (eventType == "app_mention" && !mentionsBot) return null;

        var root = threadTimestamp ?? timestamp;
        var normalized = Regex.Replace(text, "<@[A-Z0-9]+>", " ", RegexOptions.IgnoreCase).Trim();
        if (StartsWithTrigger(normalized)) normalized = normalized[Trigger.Length..].Trim();
        if (normalized.Length == 0) return null;
        return new ChatRequest("slack", channel, root, user, normalized, timestamp);
    }

    internal static async Task<ChatRequest?> ParseRequestAsync(JsonElement evt, string? botUserId,
        BridgeSettings settings, SessionStore sessions, CancellationToken cancellationToken = default)
    {
        var request = ParseRequest(evt, botUserId);
        if (request is not null)
            return ChatAccessPolicy.IsAllowedRequest(settings, "slack", request.UserId, request.ChannelId, request.ThreadId) ? request : null;

        var continuation = ParseRequest(evt, botUserId, allowPlainThreadReplies: true);
        if (continuation is null || !ChatAccessPolicy.IsAllowedRequest(settings, "slack", continuation.UserId, continuation.ChannelId, continuation.ThreadId)) return null;

        var key = ChatSessionScope.Create("slack", continuation.ChannelId, continuation.ThreadId, continuation.UserId);
        return await sessions.FindAsync(key, cancellationToken).ConfigureAwait(false) is not null ||
            await sessions.FindWorkspaceAsync(key, cancellationToken).ConfigureAwait(false) is not null ? continuation : null;
    }

    private async Task<T> SlackApiAsync<T>(string method, string token, object? body, CancellationToken ct)
    {
        using var request = TransportHttp.AuthenticatedRequest(
            body is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(_apiBase, method), token);
        if (body is not null) request.Content = TransportHttp.JsonContent(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Slack API request failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        T? result;
        try { result = await JsonSerializer.DeserializeAsync<T>(stream, TransportHttp.JsonOptions, ct).ConfigureAwait(false); }
        catch (JsonException) { throw new InvalidOperationException("Slack returned an invalid API response."); }
        return result ?? throw new InvalidOperationException("Slack returned an empty API response.");
    }

    private async Task PostReplyAsync(ChatRequest request, string text, string token, CancellationToken ct)
    {
        var result = await SlackApiAsync<SlackOkResponse>("chat.postMessage", token,
            new { channel = request.ChannelId, thread_ts = request.ThreadId, text }, ct).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException("Slack could not post the thread reply.");
    }

    private const string Trigger = "!cab";
    private static bool StartsWithTrigger(string text) => text.StartsWith(Trigger, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == Trigger.Length || char.IsWhiteSpace(text[Trigger.Length]));

    private static bool ContainsBotMention(string text, string? botId) => !string.IsNullOrWhiteSpace(botId) &&
        text.Contains($"<@{botId}>", StringComparison.OrdinalIgnoreCase);

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString()!);
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
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Slack sent a non-text WebSocket message.");
            if (content.Length + result.Count > MaxMessageBytes) throw new InvalidOperationException("Slack WebSocket message exceeded the 1 MiB limit.");
            content.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        try { return JsonDocument.Parse(content.ToArray()); }
        catch (JsonException) { return null; }
    }

    private sealed record AuthTestResponse([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("user_id")] string? UserId);
    private sealed record ConnectionsOpenResponse([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("url")] string? Url);
    private sealed record SlackOkResponse([property: JsonPropertyName("ok")] bool Ok);
    private sealed record SlackPostResponse([property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ts")] string? Timestamp,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("message")] SlackPostedMessage? Message);
    private sealed record SlackPostedMessage([property: JsonPropertyName("thread_ts")] string? ThreadTimestamp);
}
