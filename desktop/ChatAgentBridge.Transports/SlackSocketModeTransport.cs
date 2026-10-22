using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed partial class SlackSocketModeTransport : IChatTransport, IChatProgressTransport, IChatFileTransport, IChatConnectionEvents, IChatReconnectionEvents
{
    public event Action? Connected;
    public event Action<ChatReconnectionStatus>? ReconnectionStatusChanged;
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly Uri _apiBase;
    private readonly HttpClient _http;
    private readonly HttpClient _fileUploadHttp;
    private readonly bool _allowInsecureLoopbackSocketForTests;
    private readonly BoundedSeenSet _seen = new(10_000);
    private string? _botToken;
    private string? _botUserId;
    private bool _disposed;
    private bool _connected;
    private bool _fileUploadConnected;
    private int _reconnectAttempt;
    private CancellationTokenSource? _runLifetime;
    private long _connectionGeneration;
    private long _fileUploadConnectionGeneration;
    private readonly object _progressOwner = new();

    public SlackSocketModeTransport() : this(TransportHttp.CreateClient(), new Uri("https://slack.com/api/"),
        fileUploadHttp: TransportHttp.CreateClient(timeout: TimeSpan.FromMinutes(2))) { }

    internal SlackSocketModeTransport(HttpClient http, Uri apiBase, string? token = null, bool allowInsecureLoopbackSocketForTests = false, HttpClient? fileUploadHttp = null)
    {
        _http = http;
        _fileUploadHttp = fileUploadHttp ?? http;
        _apiBase = apiBase;
        _allowInsecureLoopbackSocketForTests = allowInsecureLoopbackSocketForTests;
        _botToken = token;
        if (token is not null)
        {
            _connected = true;
            _fileUploadConnected = true;
            _connectionGeneration = 1;
            _fileUploadConnectionGeneration = 1;
        }
    }

    public async Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _connectionGeneration);
        _connected = false;
        _fileUploadConnected = false;
        Interlocked.Increment(ref _fileUploadConnectionGeneration);
        _reconnectAttempt = 0;
        ArgumentNullException.ThrowIfNull(onRequest);
        if (string.IsNullOrWhiteSpace(settings.SlackBotToken) || string.IsNullOrWhiteSpace(settings.SlackAppToken))
            throw new InvalidOperationException("Slack bot and app tokens are required for Socket Mode.");
        _botToken = settings.SlackBotToken.Trim();
        _runLifetime?.Cancel();
        _runLifetime?.Dispose();
        _runLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = _runLifetime.Token;
        var sessions = new SessionStore();
        var continuations = new SlackContinuationTracker();
        var appToken = settings.SlackAppToken.Trim();
        var identity = await RetrySlackApiAsync<AuthTestResponse>("auth.test", _botToken, null, runToken,
            static response => response.Ok, "Slack bot token validation failed.", static response => IsRetryableSlackError(response.Error)).ConfigureAwait(false);
        if (!identity.Ok || string.IsNullOrWhiteSpace(identity.UserId)) throw new InvalidOperationException("Slack bot token validation failed.");
        _botUserId = identity.UserId;

        while (!runToken.IsCancellationRequested)
        {
            try
            {
                await RunSocketConnectionAsync(appToken, sessions, continuations, onRequest, settings, runToken).ConfigureAwait(false);
                if (!runToken.IsCancellationRequested) throw new SlackReconnectException();
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                _connected = false;
                _fileUploadConnected = false;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                return;
            }
            catch (ObjectDisposedException) when (runToken.IsCancellationRequested)
            {
                _connected = false;
                _fileUploadConnected = false;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                return;
            }
            catch (SlackReconnectException)
            {
                _fileUploadConnected = false;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                try { await DelayBeforeRetryAsync(runToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    _connected = false;
                    _fileUploadConnected = false;
                    return;
                }
            }
            catch
            {
                _connected = false;
                _fileUploadConnected = false;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                throw;
            }
        }
    }

    private async Task RunSocketConnectionAsync(string appToken, SessionStore sessions, SlackContinuationTracker continuations,
        Func<ChatRequest, CancellationToken, Task> onRequest, BridgeSettings settings, CancellationToken cancellationToken)
    {
        ConnectionsOpenResponse open;
        try { open = await SlackApiAsync<ConnectionsOpenResponse>("apps.connections.open", appToken, new { }, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new SlackReconnectException(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new SlackReconnectException(ex); }
        catch (InvalidOperationException ex) when (IsRetryableHttpFailure(ex.Message)) { throw new SlackReconnectException(ex); }
        if (!open.Ok && IsRetryableSlackError(open.Error)) throw new SlackReconnectException();
        if (!open.Ok || string.IsNullOrWhiteSpace(open.Url) || !Uri.TryCreate(open.Url, UriKind.Absolute, out var socketUri) ||
            (socketUri.Scheme != "wss" && !(_allowInsecureLoopbackSocketForTests && socketUri.Scheme == "ws" && TransportHttp.IsLoopback(socketUri.Host))))
            throw new InvalidOperationException("Slack Socket Mode connection could not be opened.");

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        try { await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException) { throw new SlackReconnectException(ex); }
        _connected = true;
        _fileUploadConnected = true;
        Interlocked.Increment(ref _fileUploadConnectionGeneration);
        if (_reconnectAttempt > 0)
            ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("slack", ChatReconnectionState.Reconnected, _reconnectAttempt, null));
        _reconnectAttempt = 0;
        Connected?.Invoke();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            JsonDocument? received;
            try { received = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { throw new SlackReconnectException(ex); }
            using var document = received;
            if (document is null)
            {
                if (!cancellationToken.IsCancellationRequested) throw new SlackReconnectException();
                return;
            }
            var envelope = document.RootElement;
            var type = TryString(envelope, "type", out var envelopeType) ? envelopeType : "";
            if (type == "disconnect")
            {
                var reason = TryString(envelope, "reason", out var disconnectReason) ? disconnectReason : "";
                if (reason == "link_disabled") throw new InvalidOperationException("Slack Socket Mode was disabled for this app.");
                // Slack sends `warning` shortly before normal recycling and `refresh_requested` when a fresh
                // WebSocket URL is required. Fetch a new URL immediately for either normal refresh path.
                if (reason is "warning" or "refresh_warning" or "refresh_requested" || string.IsNullOrEmpty(reason))
                    throw new SlackReconnectException();
                throw new SlackReconnectException();
            }
            if (!TryString(envelope, "envelope_id", out var envelopeId)) continue;
            if (type == "events_api")
            {
                try { await SendAsync(socket, new { envelope_id = envelopeId }, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or IOException) { throw new SlackReconnectException(ex); }
                if (!envelope.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("event", out var evt)) continue;
                var request = await ParseRequestAsync(evt, _botUserId, settings, sessions, cancellationToken, continuations).ConfigureAwait(false);
                if (request is null || !_seen.TryAdd(request.MessageId)) continue;
                // Remember before delivery so live follow-ups in a brand-new thread are accepted while the first run is active.
                continuations.Remember(ChatSessionScope.Create("slack", request.ChannelId, request.ThreadId, request.UserId));
                await onRequest(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        var attempt = ++_reconnectAttempt;
        var delay = RetryDelay(attempt);
        ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("slack", ChatReconnectionState.Reconnecting, attempt, delay));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(attempt - 1, 0, 5))));

    private sealed class SlackReconnectException(Exception? inner = null)
        : Exception("Slack Socket Mode connection was interrupted.", inner);

    private async Task<T> RetrySlackApiAsync<T>(string method, string token, object? body, CancellationToken cancellationToken,
        Func<T, bool> isValid, string invalidResponseMessage, Func<T, bool>? isRetryableResponse = null)
    {
        while (true)
        {
            try
            {
                var response = await SlackApiAsync<T>(method, token, body, cancellationToken).ConfigureAwait(false);
                if (!isValid(response))
                {
                    if (isRetryableResponse?.Invoke(response) == true)
                    {
                        await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new InvalidOperationException(invalidResponseMessage);
                }
                return response;
            }
            catch (HttpRequestException) { await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (InvalidOperationException ex) when (IsRetryableHttpFailure(ex.Message))
            { await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false); }
        }
    }

    private static bool IsRetryableSlackError(string? error) => error is
        "ratelimited" or "rate_limited" or "internal_error" or "temporarily_unavailable";

    private static bool IsRetryableHttpFailure(string message)
    {
        var marker = message.LastIndexOf("HTTP ", StringComparison.Ordinal);
        if (marker < 0) return false;
        var statusText = message.AsSpan(marker + 5);
        return int.TryParse(statusText[..Math.Min(3, statusText.Length)], out var status) &&
            (status == 429 || status >= 500);
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
        if (!_disposed)
        {
            _disposed = true;
            _connected = false;
            _fileUploadConnected = false;
            Interlocked.Increment(ref _fileUploadConnectionGeneration);
            _runLifetime?.Cancel();
            _http.Dispose();
            if (!ReferenceEquals(_fileUploadHttp, _http)) _fileUploadHttp.Dispose();
        }
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
        if (StartsWithTrigger(normalized) && !AgentChatCommand.TryParse(normalized, out _))
            normalized = normalized[Trigger.Length..].Trim();
        if (normalized.Length == 0) return null;
        return new ChatRequest("slack", channel, root, user, normalized, timestamp);
    }

    internal static async Task<ChatRequest?> ParseRequestAsync(JsonElement evt, string? botUserId,
        BridgeSettings settings, SessionStore sessions, CancellationToken cancellationToken = default,
        SlackContinuationTracker? continuations = null)
    {
        var request = ParseRequest(evt, botUserId);
        if (request is not null)
            return ChatAccessPolicy.IsAllowedRequest(settings, "slack", request.UserId, request.ChannelId, request.ThreadId) ? request : null;

        var continuation = ParseRequest(evt, botUserId, allowPlainThreadReplies: true);
        if (continuation is null || !ChatAccessPolicy.IsAllowedRequest(settings, "slack", continuation.UserId, continuation.ChannelId, continuation.ThreadId)) return null;

        var key = ChatSessionScope.Create("slack", continuation.ChannelId, continuation.ThreadId, continuation.UserId);
        if (continuations?.Contains(key) == true) return continuation;
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
        if (!response.IsSuccessStatusCode)
        {
            if (method.StartsWith("files.", StringComparison.Ordinal))
            {
                var apiError = await TryReadSlackApiErrorAsync(response, ct).ConfigureAwait(false);
                if (string.Equals(apiError, "missing_scope", StringComparison.Ordinal) || string.Equals(apiError, "not_allowed_token_type", StringComparison.Ordinal))
                    throw SlackFileApiError(apiError, "Slack file operation failed.");
            }
            throw new InvalidOperationException($"Slack API request failed with HTTP {(int)response.StatusCode}.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        T? result;
        try { result = await JsonSerializer.DeserializeAsync<T>(stream, TransportHttp.JsonOptions, ct).ConfigureAwait(false); }
        catch (JsonException) { throw new InvalidOperationException("Slack returned an invalid API response."); }
        return result ?? throw new InvalidOperationException("Slack returned an empty API response.");
    }

    private static async Task<string?> TryReadSlackApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return TryString(document.RootElement, "error", out var error) ? error : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
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

    private sealed record AuthTestResponse([property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record ConnectionsOpenResponse([property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record SlackOkResponse([property: JsonPropertyName("ok")] bool Ok);
    private sealed record SlackPostResponse([property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ts")] string? Timestamp,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("message")] SlackPostedMessage? Message);
    private sealed record SlackPostedMessage([property: JsonPropertyName("thread_ts")] string? ThreadTimestamp);
}
