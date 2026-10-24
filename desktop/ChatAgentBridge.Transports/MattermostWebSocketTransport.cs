using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport : IChatTransport, IChatProgressTransport, IChatProgressLifecycleTransport, IChatFileTransport, IChatIncomingFileTransport, IChatConnectionEvents, IChatReconnectionEvents, IChatConnectionNotifications, IChatLiveAccessSettings
{
    public event Action? Connected;
    public event Action<ChatReconnectionStatus>? ReconnectionStatusChanged;
    private const int MaxMessageBytes = 1024 * 1024;
    private const string Trigger = "!cab";
    private HttpClient? _http;
    private HttpClient? _fileUploadHttp;
    private readonly SessionStore? _injectedSessionStore;
    private readonly BoundedSeenSet _seen = new(10_000);
    private readonly MattermostBotCache _botCache = new();
    private string? _currentUserId;
    private Uri? _serverUri;
    private string? _token;
    private string? _channelId;
    private CancellationTokenSource? _runLifetime;
    private bool _disposed;
    private bool _connected;
    private bool _fileUploadConnected;
    private int _reconnectAttempt;
    private long _connectionGeneration;
    private long _fileUploadConnectionGeneration;
    private readonly object _progressOwner = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private BridgeSettings? _liveAccessSettings;

    public void UpdateAccessSettings(BridgeSettings settings) => Volatile.Write(ref _liveAccessSettings, settings);

    public MattermostWebSocketTransport() { }

    internal MattermostWebSocketTransport(SessionStore sessionStore) => _injectedSessionStore = sessionStore;

    internal MattermostWebSocketTransport(HttpClient http, Uri serverUri, string token, string channelId, string? botUserId = null)
    {
        _http = http;
        _fileUploadHttp = http;
        _serverUri = serverUri;
        _token = token;
        _channelId = channelId;
        _currentUserId = botUserId;
        _connected = true;
        _fileUploadConnected = true;
        _connectionGeneration = 1;
        _fileUploadConnectionGeneration = 1;
    }

    public async Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        UpdateAccessSettings(settings);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Uri root;
        string token;
        CancellationToken runToken;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runLifetime?.Cancel();
            Interlocked.Increment(ref _connectionGeneration);
            _connected = false;
            _fileUploadConnected = false;
            Interlocked.Increment(ref _fileUploadConnectionGeneration);
            await ClearLiveProgressPostsAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reconnectAttempt = 0;
            ArgumentNullException.ThrowIfNull(onRequest);
            if (string.IsNullOrWhiteSpace(settings.MattermostServerUrl) || string.IsNullOrWhiteSpace(settings.MattermostToken))
                throw new InvalidOperationException("Mattermost server URL and token are required.");
            if (string.IsNullOrWhiteSpace(settings.MattermostChannelId))
                throw new InvalidOperationException("A Mattermost channel ID is required.");

            root = TransportHttp.MattermostBaseUri(settings.MattermostServerUrl);
            token = settings.MattermostToken.Trim();
            _serverUri = root;
            _token = token;
            _channelId = settings.MattermostChannelId;
            if (_fileUploadHttp is not null && !ReferenceEquals(_fileUploadHttp, _http)) _fileUploadHttp.Dispose();
            _http?.Dispose();
            _http = TransportHttp.CreateClient(settings.MattermostBypassProxy);
            _fileUploadHttp = TransportHttp.CreateClient(settings.MattermostBypassProxy, timeout: TimeSpan.FromMinutes(2));
            _runLifetime?.Dispose();
            _runLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runToken = _runLifetime.Token;
        }
        finally { _lifecycleGate.Release(); }

        var sessions = _injectedSessionStore ?? new SessionStore();
        var continuations = new MattermostContinuationTracker();
        var me = await GetIdentityWithRetryAsync(root, token, runToken).ConfigureAwait(false);
        _currentUserId = me.Id;

        var socketUri = new UriBuilder(new Uri(root, "api/v4/websocket"))
        {
            Scheme = root.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = root.IsDefaultPort ? -1 : root.Port
        }.Uri;
        while (!runToken.IsCancellationRequested)
        {
            try
            {
                await RunSocketConnectionAsync(socketUri, root, token, settings, me.Username, sessions, continuations,
                    onRequest, runToken).ConfigureAwait(false);
                if (!runToken.IsCancellationRequested)
                    throw new MattermostReconnectException();
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
            catch (MattermostReconnectException)
            {
                _fileUploadConnected = false;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                var attempt = ++_reconnectAttempt;
                var delay = RetryDelay(attempt);
                ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("mattermost", ChatReconnectionState.Reconnecting, attempt, delay));
                try { await Task.Delay(delay, runToken).ConfigureAwait(false); }
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

    private async Task RunSocketConnectionAsync(Uri socketUri, Uri root, string token, BridgeSettings settings,
        string? username, SessionStore sessions, MattermostContinuationTracker continuations,
        Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        TransportHttp.ConfigureMattermostWebSocketProxy(socket.Options, settings.MattermostBypassProxy);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        try { await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException) { throw new MattermostReconnectException(ex); }
        try { await SendAsync(socket, new { seq = 1, action = "authentication_challenge", data = new { token } }, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException) { throw new MattermostReconnectException(ex); }

        // Per-connection receive buffers; each JsonDocument is disposed before the next receive reuses them.
        var receiveBuffer = new byte[16 * 1024];
        var receiveContent = new ArrayBufferWriter<byte>(receiveBuffer.Length);
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            JsonDocument? received;
            try { received = await ReceiveJsonAsync(socket, receiveBuffer, receiveContent, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { throw new MattermostReconnectException(ex); }
            using var document = received;
            if (document is null)
            {
                if (!cancellationToken.IsCancellationRequested) throw new MattermostReconnectException();
                return;
            }
            var envelope = document.RootElement;
            if (envelope.TryGetProperty("status", out var authStatus) && authStatus.ValueKind == JsonValueKind.String && authStatus.GetString() != "OK")
                throw new InvalidOperationException("Mattermost WebSocket authentication failed.");
            if (envelope.TryGetProperty("seq_reply", out var sequence) && sequence.TryGetInt32(out var replySequence) && replySequence == 1 && authStatus.ValueKind == JsonValueKind.String && authStatus.GetString() == "OK")
            {
                _connected = true;
                _fileUploadConnected = true;
                Interlocked.Increment(ref _fileUploadConnectionGeneration);
                if (_reconnectAttempt > 0)
                    ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("mattermost", ChatReconnectionState.Reconnected, _reconnectAttempt, null));
                _reconnectAttempt = 0;
                Connected?.Invoke();
            }
            if (!envelope.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String) continue;
            var currentSettings = Volatile.Read(ref _liveAccessSettings) ?? settings;
            if (!ChatConnectionPolicy.Matches("mattermost", currentSettings, settings)) continue;
            if (eventName.GetString() == "reaction_added")
            {
                await ProcessStopReactionAsync(envelope, currentSettings, onRequest, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (eventName.GetString() != "posted") continue;
            if (!envelope.TryGetProperty("data", out var data) || !data.TryGetProperty("post", out var postJson) || postJson.ValueKind != JsonValueKind.String) continue;

            var post = postJson.GetString()!;
            await ProcessPostAsync(post, currentSettings, _currentUserId ?? "", username, sessions, continuations, _seen,
                async (request, ct) =>
                {
                    if (!_botCache.TryGet(request.UserId, out var isBot))
                    {
                        MattermostUser author;
                        try { author = await GetAuthorWithRetryAsync(root, token, request.UserId, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException ||
                            ex is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
                        isBot = author.IsBot;
                        _botCache.Set(request.UserId, isBot);
                    }
                    if (isBot) return false;
                    await onRequest(request, ct).ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MattermostUser> GetAuthorWithRetryAsync(Uri root, string token, string userId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get,
                    new Uri(root, $"api/v4/users/{Uri.EscapeDataString(userId)}"), token);
                return await TransportHttp.SendJsonAsync<MattermostUser>(ActiveHttp, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryableAuthorLookupFailure(ex, cancellationToken))
            {
                // Mattermost does not guarantee replay of a post after a later reconnect.
                // Retry the bounded author lookup while this event is still being handled.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableAuthorLookupFailure(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or IOException ||
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested ||
        error is InvalidOperationException invalid && (invalid.Message.Contains("HTTP 429", StringComparison.Ordinal) ||
            TryGetHttpStatus(invalid.Message, out var status) && status >= 500);

    private async Task<MattermostUser> GetIdentityWithRetryAsync(Uri root, string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get, new Uri(root, "api/v4/users/me"), token);
                return await TransportHttp.SendJsonAsync<MattermostUser>(ActiveHttp, request, cancellationToken).ConfigureAwait(false);
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

    private async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        var attempt = ++_reconnectAttempt;
        var delay = RetryDelay(attempt);
        ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("mattermost", ChatReconnectionState.Reconnecting, attempt, delay));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRetryableHttpFailure(string message) =>
        message.Contains("HTTP 429", StringComparison.Ordinal) ||
        TryGetHttpStatus(message, out var status) && status >= 500;

    private static bool TryGetHttpStatus(string message, out int status)
    {
        status = 0;
        var marker = message.LastIndexOf("HTTP ", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var value = message.AsSpan(marker + 5);
            return value.Length >= 3 && int.TryParse(value[..3], out status);
        }
        return false;
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(attempt - 1, 0, 5))));

    private sealed class MattermostReconnectException(Exception? inner = null)
        : Exception("Mattermost WebSocket connection was interrupted.", inner);

    public async Task NotifyConnectionAsync(BridgeSettings settings, string message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationToken runToken = _runLifetime?.Token ?? new CancellationToken(canceled: true);
        if (runToken.IsCancellationRequested) throw new OperationCanceledException(runToken);
        if (!string.Equals(settings.MattermostChannelId, _channelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost notification settings do not match the active connection.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken);
        var root = _serverUri ?? throw new InvalidOperationException("Mattermost is not connected.");
        var token = _token ?? throw new InvalidOperationException("Mattermost is not connected.");
        using var body = TransportHttp.JsonContent(new { channel_id = settings.MattermostChannelId, root_id = "", message });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/posts")) { Content = body };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await ActiveHttp.SendAsync(request, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost status notice failed with HTTP {(int)response.StatusCode}.");
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
        using var response = await ActiveHttp.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost reply failed with HTTP {(int)response.StatusCode}.");
    }

    public async Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ValidateProgressRequest(request);
        var generation = Volatile.Read(ref _connectionGeneration);
        var root = _serverUri!;
        var token = _token!;
        var http = ActiveHttp;
        var botUserId = _currentUserId;
        using var body = TransportHttp.JsonContent(new { channel_id = request.ChannelId, root_id = request.ThreadId, message = text });
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/posts")) { Content = body };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost progress post failed with HTTP {(int)response.StatusCode}.");
        var created = await ReadMattermostPostAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(created.ChannelId, request.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(string.IsNullOrWhiteSpace(created.RootId) ? created.Id : created.RootId, request.ThreadId, StringComparison.Ordinal) ||
            string.Equals(created.Id, request.MessageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost returned a progress post outside the requested thread.");
        cancellationToken.ThrowIfCancellationRequested();
        var handle = new ChatProgressMessage("mattermost", request.ChannelId, request.ThreadId, request.UserId,
            created.Id!, _progressOwner, generation);
        RegisterLiveProgressPost(request, handle, http, root, token, botUserId, cancellationToken);
        try
        {
            await AddStopReactionAsync(handle.MessageId, handle.ConnectionGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var entry = TakeLiveProgressPost(handle);
            if (entry is not null)
                await RemoveStopReactionAsync(handle.MessageId, entry, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return handle;
    }

    public async Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken)
    {
        ValidateProgressHandle(request, message, "mattermost");
        var root = _serverUri!;
        var token = _token!;
        using var body = TransportHttp.JsonContent(new { message = text });
        using var patch = new HttpRequestMessage(HttpMethod.Put,
            new Uri(root, $"api/v4/posts/{Uri.EscapeDataString(message.MessageId)}/patch")) { Content = body };
        patch.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await ActiveHttp.SendAsync(patch, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost progress update failed with HTTP {(int)response.StatusCode}.");
    }

    private void ValidateProgressRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(request.Provider, "mattermost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Mattermost.", nameof(request));
        EnsureConnectedForProgress();
        if (!string.Equals(request.ChannelId, _channelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost progress request does not match the active channel.");
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

    private void EnsureConnectedForProgress()
    {
        if (!_connected || _serverUri is null || _token is null || _channelId is null)
            throw new InvalidOperationException("Mattermost is not connected.");
    }

    private static async Task<MattermostCreatedPost> ReadMattermostPostAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        MattermostCreatedPost? post;
        try { post = await JsonSerializer.DeserializeAsync<MattermostCreatedPost>(stream, TransportHttp.JsonOptions, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new InvalidOperationException("Mattermost returned an invalid progress response."); }
        return post is not null && !string.IsNullOrWhiteSpace(post.Id) && !string.IsNullOrWhiteSpace(post.ChannelId)
            ? post
            : throw new InvalidOperationException("Mattermost returned an invalid progress post.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Interlocked.Increment(ref _connectionGeneration);
            _connected = false;
            _fileUploadConnected = false;
            Interlocked.Increment(ref _fileUploadConnectionGeneration);
            _runLifetime?.Cancel();
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runLifetime?.Cancel();
            Interlocked.Increment(ref _connectionGeneration);
            _connected = false;
            _fileUploadConnected = false;
            Interlocked.Increment(ref _fileUploadConnectionGeneration);
            await ClearLiveProgressPostsAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _http?.Dispose();
            if (_fileUploadHttp is not null && !ReferenceEquals(_fileUploadHttp, _http)) _fileUploadHttp.Dispose();
        }
        finally { _lifecycleGate.Release(); }
    }

    private HttpClient ActiveHttp => _http ?? throw new InvalidOperationException("Mattermost is not connected.");
    private HttpClient ActiveFileUploadHttp => _fileUploadHttp ?? throw new InvalidOperationException("Mattermost is not connected.");

    internal static ChatRequest? ParsePost(string json, string channelFilter, string currentUserId, bool isBot,
        string? currentUsername = null, bool allowPlainThreadReplies = false) =>
        BuildRequest(ReadPost(json, channelFilter, currentUserId, isBot), currentUsername, allowPlainThreadReplies);

    private static MattermostPost? ReadPost(string json, string channelFilter, string currentUserId, bool isBot)
    {
        MattermostPost? post;
        try { post = JsonSerializer.Deserialize<MattermostPost>(json, TransportHttp.JsonOptions); }
        catch (JsonException) { return null; }
        if (post is null || string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.UserId) ||
            !string.Equals(post.ChannelId, channelFilter, StringComparison.Ordinal) ||
            string.Equals(post.UserId, currentUserId, StringComparison.Ordinal) || isBot ||
            string.IsNullOrWhiteSpace(post.Message) || IsFromWebhook(post)) return null;
        return post;
    }

    private static ChatRequest? BuildRequest(MattermostPost? post, string? currentUsername, bool allowPlainThreadReplies)
    {
        if (post is null) return null;
        var threadId = string.IsNullOrWhiteSpace(post.RootId) ? post.Id : post.RootId;
        var isThreadReply = !string.IsNullOrWhiteSpace(post.RootId) &&
            !string.Equals(post.RootId, post.Id, StringComparison.Ordinal);
        if (!TryGetTriggeredText(post.Message, currentUsername, allowPlainThreadReplies, isThreadReply, out var text)) return null;
        var files = ReadIncomingFileIds(post.FileIds, out var filesError);
        return new ChatRequest("mattermost", post.ChannelId, threadId, post.UserId, text, post.Id)
        {
            IncomingFiles = files,
            IncomingFilesError = filesError
        };
    }

    internal static async Task<bool> ProcessPostAsync(
        string json,
        BridgeSettings settings,
        string currentUserId,
        string? currentUsername,
        SessionStore sessions,
        MattermostContinuationTracker continuations,
        BoundedSeenSet seen,
        Func<ChatRequest, CancellationToken, Task<bool>> deliver,
        CancellationToken cancellationToken = default)
    {
        var candidate = await ResolvePostCandidateAsync(json, settings, currentUserId, currentUsername,
            sessions, continuations, cancellationToken).ConfigureAwait(false);
        if (candidate is null || !seen.TryAdd(candidate.Request.MessageId)) return false;

        var delivered = await deliver(candidate.Request, cancellationToken).ConfigureAwait(false);
        if (delivered && candidate.IsExplicit)
            continuations.Remember(CreateSessionKey(settings, candidate.Request));
        return delivered;
    }

    internal static async Task<MattermostRequestCandidate?> ResolvePostCandidateAsync(
        string json,
        BridgeSettings settings,
        string currentUserId,
        string? currentUsername,
        SessionStore sessions,
        MattermostContinuationTracker continuations,
        CancellationToken cancellationToken = default)
    {
        var post = ReadPost(json, settings.MattermostChannelId, currentUserId, isBot: false);
        var explicitRequest = BuildRequest(post, currentUsername, allowPlainThreadReplies: false);
        if (explicitRequest is not null)
        {
            return ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", explicitRequest.UserId,
                explicitRequest.ChannelId, explicitRequest.ThreadId)
                ? new MattermostRequestCandidate(explicitRequest, IsExplicit: true)
                : null;
        }

        var continuation = BuildRequest(post, currentUsername, allowPlainThreadReplies: true);
        if (continuation is null || !ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", continuation.UserId,
                continuation.ChannelId, continuation.ThreadId)) return null;

        var key = CreateSessionKey(settings, continuation);
        if (continuations.Contains(key)) return new MattermostRequestCandidate(continuation, IsExplicit: false);
        if (await sessions.FindAsync(key, cancellationToken).ConfigureAwait(false) is not null ||
            await sessions.FindWorkspaceAsync(key, cancellationToken).ConfigureAwait(false) is not null)
            return new MattermostRequestCandidate(continuation, IsExplicit: false);
        return null;
    }

    private static SessionKey CreateSessionKey(BridgeSettings settings, ChatRequest request) =>
        ChatSessionScope.Create("mattermost", request.ChannelId, request.ThreadId, request.UserId, settings.MattermostServerUrl);

    private static bool TryGetTriggeredText(string message, string? currentUsername, bool allowPlainThreadReplies,
        bool isThreadReply, out string text)
    {
        text = string.Empty;
        if (HasTrigger(message))
        {
            text = message[Trigger.Length..].Trim();
            if (AgentChatCommand.TryParse(message.Trim(), out _)) text = message.Trim();
            return text.Length > 0;
        }

        var username = currentUsername?.Trim();
        if (!string.IsNullOrEmpty(username))
        {
            var mention = "@" + username;
            if (message.StartsWith(mention, StringComparison.OrdinalIgnoreCase))
            {
                if (message.Length == mention.Length) return false;
                if (char.IsWhiteSpace(message[mention.Length]))
                {
                    text = message[mention.Length..].Trim();
                    if (text.Length == 0) return false;
                    if (HasTrigger(text))
                    {
                        if (!AgentChatCommand.TryParse(text.Trim(), out _)) text = text[Trigger.Length..].Trim();
                        else text = text.Trim();
                    }
                    return text.Length > 0;
                }
            }
        }

        if (!allowPlainThreadReplies || !isThreadReply) return false;
        text = message.Trim();
        return text.Length > 0;
    }

    private static bool HasTrigger(string text) => text.StartsWith(Trigger, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == Trigger.Length || char.IsWhiteSpace(text[Trigger.Length]));

    private static bool IsFromWebhook(MattermostPost post)
    {
        if (post.Props is not { } props || !props.TryGetProperty("from_webhook", out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
            value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IncomingChatFile> ReadIncomingFileIds(JsonElement? value, out string? error)
    {
        error = null;
        if (value is null || value.Value.ValueKind == JsonValueKind.Undefined || value.Value.ValueKind == JsonValueKind.Null)
            return Array.Empty<IncomingChatFile>();
        if (value.Value.ValueKind != JsonValueKind.Array) { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
        var files = new List<IncomingChatFile>();
        foreach (var item in value.Value.EnumerateArray())
        {
            if (files.Count == TransportHttp.MaxIncomingFileCount)
            { error = "too_many_attachments"; return files; }
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
            var id = item.GetString()!;
            if (files.Any(file => string.Equals(file.Id, id, StringComparison.Ordinal)))
            { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
            files.Add(new IncomingChatFile(id, string.Empty, 0, null));
        }
        return files;
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, TransportHttp.JsonOptions));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(ClientWebSocket socket, byte[] buffer, ArrayBufferWriter<byte> content, CancellationToken ct)
    {
        content.ResetWrittenCount();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || content.WrittenCount + result.Count > MaxMessageBytes)
                throw new MattermostReconnectException();
            content.Write(buffer.AsSpan(0, result.Count));
        } while (!result.EndOfMessage);
        try { return JsonDocument.Parse(content.WrittenMemory); }
        catch (JsonException) { return null; }
    }

    private sealed record MattermostUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("is_bot")] bool IsBot);
    private sealed record MattermostCreatedPost(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("root_id")] string? RootId);
    internal sealed record MattermostRequestCandidate(ChatRequest Request, bool IsExplicit);
    private sealed record MattermostPost(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("channel_id")] string ChannelId,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("root_id")] string? RootId,
        [property: JsonPropertyName("props")] JsonElement? Props,
        [property: JsonPropertyName("file_ids")] JsonElement? FileIds);
}
