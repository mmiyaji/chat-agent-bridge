using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

/// <summary>Discord-specific configuration and privileged-intent failures use these fixed safe codes.</summary>
public sealed class DiscordConfigurationException(string code) : InvalidOperationException(SafeMessage(code))
{
    public string Code { get; } = code;
    private static string SafeMessage(string code) => code switch
    {
        "missing_configuration" => "Discord bot token is not configured.",
        "authentication" => "Discord rejected the bot token. Check the token and bot authorization.",
        "message_content_intent" => "Discord denied the Message Content privileged intent. Enable Message Content Intent for the bot in the Discord Developer Portal.",
        "missing_permissions" => "The Discord bot cannot create a public thread in this channel. Grant Create Public Threads and Send Messages in Threads permissions.",
        _ => "Discord configuration is invalid."
    };
}

/// <summary>Discord Gateway and REST transport. Discord snowflakes remain opaque decimal strings.</summary>
public sealed partial class DiscordGatewayTransport : IChatTransport, IChatProgressTransport,
    IChatProgressLifecycleTransport, IChatActionTransport, IChatFileTransport,
    IChatIncomingFileTransport, IChatConnectionEvents, IChatReconnectionEvents, IChatLiveAccessSettings
{
    internal const int MaxGatewayMessageBytes = 1024 * 1024;
    private const int GatewayIntents = 1 | 512 | 4096 | 32768; // GUILDS, GUILD_MESSAGES, DIRECT_MESSAGES, MESSAGE_CONTENT
    private const string Trigger = "!cab";
    private static readonly TimeSpan ActionTtl = TimeSpan.FromMinutes(5);
    private readonly Uri _apiBase;
    private readonly HttpClient _http;
    private readonly HttpClient _cdnHttp;
    private readonly Func<Uri, IDiscordGatewaySocket> _socketFactory;
    private readonly Func<int, TimeSpan> _retryDelay;
    private readonly SessionStore? _injectedSessions;
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenMessageOrder = new();
    private readonly object _seenLock = new();
    private readonly object _stateLock = new();
    private readonly object _actionLock = new();
    private readonly Dictionary<string, PendingAction> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveProgress> _progress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _threadParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _createdThreads = new(StringComparer.Ordinal);
    private readonly object _progressOwner = new();
    private BridgeSettings? _liveSettings;
    private CancellationTokenSource? _runLifetime;
    private string? _token;
    private string? _botUserId;
    private string? _applicationId;
    private bool _connected;
    private bool _disposed;
    private int _reconnectAttempt;
    private long _generation;
    private long _progressGeneration;

    public event Action? Connected;
    public event Action<ChatReconnectionStatus>? ReconnectionStatusChanged;

    public void UpdateAccessSettings(BridgeSettings settings) => Volatile.Write(ref _liveSettings, settings);

    public DiscordGatewayTransport() : this(TransportHttp.CreateClient(timeout: TimeSpan.FromSeconds(15)),
        new Uri("https://discord.com/api/v10/"), null, null, TransportHttp.CreateClient(timeout: TimeSpan.FromMinutes(2))) { }

    internal DiscordGatewayTransport(SessionStore sessions) : this()
    {
        _injectedSessions = sessions;
    }

    internal DiscordGatewayTransport(HttpClient http, Uri apiBase, string? token = null,
        Func<Uri, IDiscordGatewaySocket>? socketFactory = null, HttpClient? cdnHttp = null, SessionStore? sessions = null,
        Func<int, TimeSpan>? retryDelay = null)
    {
        _http = http;
        _cdnHttp = cdnHttp ?? http;
        _apiBase = apiBase;
        _token = token;
        _injectedSessions = sessions;
        _socketFactory = socketFactory ?? (uri => new ClientDiscordGatewaySocket(uri));
        _retryDelay = retryDelay ?? RetryDelay;
        if (token is not null)
        {
            _connected = true;
            _generation = 1;
        }
    }

    public async Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onRequest);
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateAccessSettings(settings);
        if (string.IsNullOrWhiteSpace(settings.DiscordBotToken))
            throw new DiscordConfigurationException("missing_configuration");
        _token = settings.DiscordBotToken.Trim();
        Interlocked.Increment(ref _progressGeneration);
        _activeOnRequest = onRequest;
        _runLifetime?.Cancel();
        _runLifetime?.Dispose();
        _runLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken runToken = _runLifetime.Token;
        var sessions = _injectedSessions ?? new SessionStore();
        await LoadPendingOwnerReservationsAsync(sessions, runToken).ConfigureAwait(false);
        var continuations = new DiscordContinuationTracker();
        var messageQueue = Channel.CreateBounded<QueuedDiscordMessage>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var dispatchLifetime = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        Task dispatchTask = DispatchQueuedMessagesAsync(messageQueue.Reader, settings, sessions, continuations, onRequest, dispatchLifetime.Token);
        string gatewayUrl;
        int startupAttempt = 0;
        while (true)
        {
            try { gatewayUrl = await GetGatewayUrlAsync(runToken).ConfigureAwait(false); break; }
            catch (Exception ex) when (IsTransient(ex, runToken))
            {
                var delay = _retryDelay(++startupAttempt);
                ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("discord", ChatReconnectionState.Reconnecting, startupAttempt, delay));
                await Task.Delay(delay, runToken).ConfigureAwait(false);
            }
        }

        try
        {
        while (!runToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(gatewayUrl, settings, sessions, continuations, messageQueue.Writer, dispatchTask, runToken).ConfigureAwait(false);
                if (!runToken.IsCancellationRequested) throw new DiscordReconnectException();
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested) { SetDisconnected(); return; }
            catch (DiscordReconnectException)
            {
                SetDisconnected();
                var attempt = ++_reconnectAttempt;
                var delay = _retryDelay(attempt);
                ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("discord", ChatReconnectionState.Reconnecting, attempt, delay));
                try { await Task.Delay(delay, runToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested) { return; }
                try { gatewayUrl = await GetGatewayUrlAsync(runToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsTransient(ex, runToken)) { }
            }
            catch
            {
                SetDisconnected();
                throw;
            }
        }
        }
        finally
        {
            messageQueue.Writer.TryComplete();
            dispatchLifetime.Cancel();
            try { await dispatchTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            dispatchLifetime.Dispose();
            Interlocked.Increment(ref _progressGeneration);
        }
    }

    private async Task RunConnectionAsync(string gatewayUrl, BridgeSettings settings, SessionStore sessions,
        DiscordContinuationTracker continuations, ChannelWriter<QueuedDiscordMessage> messageWriter, Task dispatchTask,
        CancellationToken cancellationToken)
    {
        var socketUri = BuildGatewayUri(_resumeGatewayUrl ?? gatewayUrl);
        await using var socket = _socketFactory(socketUri);
        try { await socket.ConnectAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        { throw new DiscordReconnectException(ex); }

        int? heartbeatInterval = null;
        bool heartbeatAcknowledged = true;
        DateTimeOffset nextHeartbeat = DateTimeOffset.MaxValue;
        bool identified = false;
        Task<string?> receiveTask = socket.ReceiveAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (dispatchTask.IsFaulted) await dispatchTask.ConfigureAwait(false);
            if (heartbeatInterval is null)
            {
                string? first;
                try { first = await receiveTask.ConfigureAwait(false); }
                catch (DiscordGatewayCloseException ex) { throw MapGatewayClose(ex.CloseCode); }
                catch (Exception ex) when (ex is WebSocketException or IOException) { throw new DiscordReconnectException(ex); }
                if (first is null) throw new DiscordReconnectException();
                using var hello = ParseJson(first);
                if (hello is null || !TryReadInt(hello.RootElement, "op", out var op) || op != 10 ||
                    !hello.RootElement.TryGetProperty("d", out var helloData) || !TryReadInt(helloData, "heartbeat_interval", out var interval) || interval is < 100 or > 300_000)
                    throw new DiscordReconnectException();
                heartbeatInterval = interval;
                nextHeartbeat = DateTimeOffset.UtcNow.AddMilliseconds(Jitter(interval));
                await SendIdentifyOrResumeAsync(socket, settings, cancellationToken).ConfigureAwait(false);
                identified = true;
                receiveTask = socket.ReceiveAsync(cancellationToken);
                continue;
            }

            TimeSpan wait = nextHeartbeat - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            Task timer = Task.Delay(wait, cancellationToken);
            Task winner = await Task.WhenAny(receiveTask, timer, dispatchTask).ConfigureAwait(false);
            if (winner == dispatchTask)
            {
                await dispatchTask.ConfigureAwait(false);
                throw new InvalidOperationException("Discord message dispatcher stopped unexpectedly.");
            }
            if (winner == timer)
            {
                if (!heartbeatAcknowledged) throw new DiscordReconnectException();
                heartbeatAcknowledged = false;
                try { await SendGatewayAsync(socket, 1, LastSequence, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or IOException) { throw new DiscordReconnectException(ex); }
                nextHeartbeat = DateTimeOffset.UtcNow.AddMilliseconds(heartbeatInterval.Value);
                continue;
            }

            string? raw;
            try { raw = await receiveTask.ConfigureAwait(false); }
            catch (DiscordGatewayCloseException ex) { throw MapGatewayClose(ex.CloseCode); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { throw new DiscordReconnectException(ex); }
            if (raw is null) throw new DiscordReconnectException();
            receiveTask = socket.ReceiveAsync(cancellationToken);
            using var document = ParseJson(raw);
            if (document is null || !TryReadInt(document.RootElement, "op", out var opcode)) continue;
            var root = document.RootElement;
            if (root.TryGetProperty("s", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var sequence))
                LastSequence = sequence;
            if (opcode == 11) { heartbeatAcknowledged = true; continue; }
            if (opcode == 1)
            {
                heartbeatAcknowledged = false;
                await SendGatewayAsync(socket, 1, LastSequence, cancellationToken).ConfigureAwait(false);
                nextHeartbeat = DateTimeOffset.UtcNow.AddMilliseconds(heartbeatInterval.Value);
                continue;
            }
            if (opcode == 7) throw new DiscordReconnectException();
            if (opcode == 9)
            {
                bool resumable = root.TryGetProperty("d", out var resumeValue) && resumeValue.ValueKind == JsonValueKind.True;
                if (!resumable) ClearGatewaySession();
                throw new DiscordReconnectException();
            }
            long? eventSequence = root.TryGetProperty("s", out seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out sequence) ? sequence : null;
            if (opcode != 0 || !root.TryGetProperty("t", out var eventType) || eventType.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("d", out var data)) continue;

            string name = eventType.GetString()!;
            if (name == "READY")
            {
                _sessionId = ReadString(data, "session_id");
                _resumeGatewayUrl = ReadString(data, "resume_gateway_url") ?? gatewayUrl;
                _botUserId = data.TryGetProperty("user", out var user) ? ReadString(user, "id") : null;
                _applicationId = data.TryGetProperty("application", out var app) ? ReadString(app, "id") : null;
                if (string.IsNullOrWhiteSpace(_botUserId)) throw new InvalidOperationException("Discord READY event did not include the bot user ID.");
                OnConnected();
                continue;
            }
            if (name == "RESUMED") { OnConnected(); continue; }
            if (!identified) continue;
            var currentSettings = Volatile.Read(ref _liveSettings) ?? settings;
            if (!DiscordSettingsStillMatch(currentSettings, settings)) continue;
            if (name == "MESSAGE_CREATE")
            {
                if (!messageWriter.TryWrite(new QueuedDiscordMessage(data.Clone(), eventSequence)))
                {
                    LastSequence = LastProcessedMessageSequence;
                    throw new DiscordReconnectException();
                }
            }
            else if (name == "INTERACTION_CREATE")
            {
                // ACKs and callback work are detached from the receive loop after their tiny initial defer.
                if (data.TryGetProperty("type", out var interactionType) && interactionType.ValueKind == JsonValueKind.Number && interactionType.GetInt32() == 3)
                    _ = HandleInteractionAsync(data, currentSettings, socket, cancellationToken);
            }
        }
    }

    private async Task DispatchQueuedMessagesAsync(ChannelReader<QueuedDiscordMessage> reader, BridgeSettings settings,
        SessionStore sessions, DiscordContinuationTracker continuations,
        Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await ProcessMessageAsync(item.Data, Volatile.Read(ref _liveSettings) ?? settings, sessions, continuations, onRequest, ct).ConfigureAwait(false); break; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsTransient(ex, ct) && attempt < 3) { await Task.Delay(_retryDelay(attempt + 1), ct).ConfigureAwait(false); }
            }
            if (item.Sequence is not null)
            {
                LastProcessedMessageSequence = item.Sequence;
                LastSequence = item.Sequence;
            }
        }
    }

    private readonly object _sequenceLock = new();
    private long? _lastSequenceValue;
    private long? _lastProcessedMessageSequenceValue;
    private long? LastSequence { get { lock (_sequenceLock) return _lastSequenceValue; } set { lock (_sequenceLock) _lastSequenceValue = value; } }
    private long? LastProcessedMessageSequence { get { lock (_sequenceLock) return _lastProcessedMessageSequenceValue; } set { lock (_sequenceLock) _lastProcessedMessageSequenceValue = value; } }
    private string? _sessionId;
    private string? _resumeGatewayUrl;

    private async Task SendIdentifyOrResumeAsync(IDiscordGatewaySocket socket, BridgeSettings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_sessionId) && !string.IsNullOrWhiteSpace(_resumeGatewayUrl))
        {
            await SendGatewayAsync(socket, 6, new { token = _token, session_id = _sessionId, seq = LastSequence }, ct).ConfigureAwait(false);
            return;
        }
        await SendGatewayAsync(socket, 2, new
        {
            token = _token,
            intents = GatewayIntents,
            properties = new { os = Environment.OSVersion.Platform.ToString().ToLowerInvariant(), browser = "chat-agent-bridge", device = "chat-agent-bridge" },
            shard = new[] { 0, 1 }
        }, ct).ConfigureAwait(false);
    }

    private async Task<string> GetGatewayUrlAsync(CancellationToken ct)
    {
        var result = await SendApiAsync<GatewayBotResponse>(HttpMethod.Get, "gateway/bot", null, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Url) || !Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Discord did not return a secure Gateway URL.");
        _ = BuildGatewayUri(uri.ToString());
        return uri.ToString();
    }

    private async Task ProcessMessageAsync(JsonElement data, BridgeSettings settings, SessionStore sessions,
        DiscordContinuationTracker continuations, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken ct)
    {
        if (!TryReadMessage(data, out var parsed) || parsed is null) return;
        if (HasSeenMessage(parsed.MessageId)) return;
        if (string.Equals(parsed.UserId, _botUserId, StringComparison.Ordinal) || parsed.IsBot || parsed.IsWebhook || parsed.MessageType is not (0 or 19))
            return;
        if (!ChatAccessPolicy.IsAllowed(settings, "discord", parsed.UserId)) return;
        if (parsed.GuildId is null)
        {
            if (!settings.DiscordAllowDirectMessages) return;
        }
        else
        {
            string configuredGuilds = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedGuildIds);
            if (!configuredGuilds.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(parsed.GuildId, StringComparer.Ordinal)) return;
        }
        if (parsed.GuildId is not null && parsed.ParentChannelId is null)
        {
            string? resolvedParent = await ResolveThreadParentAsync(parsed.ChannelId, ct).ConfigureAwait(false);
            if (resolvedParent is not null) parsed = parsed with { ParentChannelId = resolvedParent };
        }
        bool inThread = !string.IsNullOrWhiteSpace(parsed.ParentChannelId);
        string parentChannel = inThread ? parsed.ParentChannelId! : parsed.ChannelId;
        string threadId = inThread ? parsed.ChannelId : parsed.ChannelId;
        bool explicitStart = HasTrigger(parsed.Text) || HasBotMention(parsed.Text, _botUserId);
        bool isDirectMessage = parsed.GuildId is null;
        if (!explicitStart && !inThread && !isDirectMessage) return;
        var request = new ChatRequest("discord", parentChannel, threadId, parsed.UserId,
            CleanPrompt(parsed.Text, _botUserId), parsed.MessageId)
        {
            GuildId = parsed.GuildId,
            SourceChannelId = parsed.ChannelId,
            IncomingFiles = parsed.Files,
            IncomingFilesError = parsed.FilesError
        };
        if (request.Text.Length == 0 && request.IncomingFiles.Count == 0) return;
        if (!ChatAccessPolicy.IsAllowedRequest(settings, "discord", request.UserId, request.ChannelId, request.ThreadId, request.GuildId)) return;

        if (inThread || isDirectMessage)
        {
            var key = ChatSessionScope.Create("discord", request.ChannelId, request.ThreadId, request.UserId);
            if (IsPendingOwnerMismatch(key)) return;
            if (continuations.IsOwnedByAnotherUser(key)) return;
            var allSessions = await sessions.ListAsync(ct).ConfigureAwait(false);
            if (allSessions.Any(session => session.Key.Provider == "discord" && session.Key.ChannelId == request.ChannelId &&
                session.Key.ThreadId == request.ThreadId && session.Key.UserId != request.UserId)) return;
            if (!explicitStart && !continuations.Contains(key) &&
                await sessions.FindAsync(key, ct).ConfigureAwait(false) is null &&
                await sessions.FindWorkspaceAsync(key, ct).ConfigureAwait(false) is null) return;
        }
        if (!inThread && request.GuildId is not null)
        {
            // Top-level guild requests receive their own public thread before the session is dispatched.
            string? existingThread;
            lock (_stateLock) _createdThreads.TryGetValue(request.MessageId, out existingThread);
            var created = existingThread is null
                ? await CreatePublicThreadAsync(parsed.ChannelId, parsed.MessageId, request.Text, ct).ConfigureAwait(false)
                : new DiscordChannelResponse(existingThread);
            if (string.IsNullOrWhiteSpace(created.Id)) return;
            lock (_stateLock)
            {
                _createdThreads[request.MessageId] = created.Id;
                while (_createdThreads.Count > 10_000) _createdThreads.Remove(_createdThreads.Keys.First());
            }
            request = request with { ThreadId = created.Id };
        }

        continuations.Remember(ChatSessionScope.Create("discord", request.ChannelId, request.ThreadId, request.UserId));
        await onRequest(request, ct).ConfigureAwait(false);
        RememberSeenMessage(request.MessageId);
    }

    private async Task<string?> ResolveThreadParentAsync(string channelId, CancellationToken ct)
    {
        lock (_stateLock)
            if (_threadParents.TryGetValue(channelId, out var cached)) return cached;
        try
        {
            var channel = await SendApiAsync<DiscordChannelInfo>(HttpMethod.Get, $"channels/{Escape(channelId)}", null, ct).ConfigureAwait(false);
            if (channel.Type is not (10 or 11 or 12) || string.IsNullOrWhiteSpace(channel.ParentId)) return null;
            lock (_stateLock)
            {
                _threadParents[channelId] = channel.ParentId;
                while (_threadParents.Count > 2_000) _threadParents.Remove(_threadParents.Keys.First());
            }
            return channel.ParentId;
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<DiscordChannelResponse> CreatePublicThreadAsync(string channelId, string messageId, string text, CancellationToken ct)
    {
        string name = MakeThreadName(text);
        try
        {
            return await SendApiAsync<DiscordChannelResponse>(HttpMethod.Post,
                $"channels/{Escape(channelId)}/messages/{Escape(messageId)}/threads",
                new { name, auto_archive_duration = 1440 }, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 403", StringComparison.Ordinal))
        {
            throw new DiscordConfigurationException("missing_permissions");
        }
    }

    public async Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        foreach (string chunk in SplitMessage(text, 2000))
            await SendMessageAsync(request.ChannelId, request.ThreadId, chunk, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        string nonce = CreateNonce();
        var response = await SendMessageAsync(request.ChannelId, request.ThreadId, LimitMessage(text),
            StopComponents(nonce), cancellationToken).ConfigureAwait(false);
        var handle = new ChatProgressMessage("discord", request.ChannelId, request.ThreadId, request.UserId,
            response.Id ?? throw new InvalidOperationException("Discord did not return the progress message ID."), _progressOwner, Volatile.Read(ref _progressGeneration));
        lock (_actionLock)
        {
            _progress[nonce] = new LiveProgress(request, handle, Volatile.Read(ref _progressGeneration), DateTimeOffset.UtcNow + ActionTtl);
        }
        return handle;
    }

    public async Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken)
    {
        ValidateProgressHandle(request, message);
        RenewProgressStop(message);
        await EditMessageAsync(message.ThreadId, message.MessageId, LimitMessage(text), StopComponents(FindProgressNonce(message)), cancellationToken).ConfigureAwait(false);
    }

    public void CompleteProgress(ChatProgressMessage message)
    {
        if (message is null || !ReferenceEquals(message.Owner, _progressOwner)) return;
        string? nonce = null;
        lock (_actionLock)
        {
            foreach (var item in _progress)
                if (item.Value.Handle == message) { nonce = item.Key; break; }
            if (nonce is not null) _progress.Remove(nonce);
        }
        if (nonce is not null && IsCurrentGeneration(message))
            _ = TryEditComponentsAsync(message.ThreadId, message.MessageId, null);
    }

    public async Task CompleteProgressAsync(ChatProgressMessage message, CancellationToken cancellationToken = default)
    {
        if (message is null || !ReferenceEquals(message.Owner, _progressOwner)) return;
        bool removed = false;
        lock (_actionLock)
        {
            foreach (var item in _progress.Where(item => item.Value.Handle == message).ToArray())
            { _progress.Remove(item.Key); removed = true; }
        }
        if (removed && IsCurrentGeneration(message)) await TryEditComponentsAsync(message.ThreadId, message.MessageId, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> PostActionsAsync(ChatRequest origin, string text, ChatActionKind kind,
        Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler, CancellationToken cancellationToken)
    {
        ValidateRequest(origin);
        ArgumentNullException.ThrowIfNull(handler);
        var action = new PendingAction(origin, kind, handler, Volatile.Read(ref _generation), DateTimeOffset.UtcNow + ActionTtl);
        lock (_actionLock) _actions.Add(action.Nonce, action);
        try
        {
            var response = await SendMessageAsync(origin.ChannelId, origin.ThreadId, LimitMessage(text),
                ApprovalComponents(action), cancellationToken).ConfigureAwait(false);
            action.MessageId = response.Id ?? throw new InvalidOperationException("Discord did not return the action message ID.");
            action.ChannelId = origin.ThreadId;
            return new ActionLease(this, action);
        }
        catch
        {
            lock (_actionLock) _actions.Remove(action.Nonce);
            throw;
        }
    }

    private async Task HandleInteractionAsync(JsonElement data, BridgeSettings settings, IDiscordGatewaySocket socket, CancellationToken ct)
    {
        if (!TryReadString(data, "id", out var interactionId) || !TryReadString(data, "token", out var interactionToken)) return;
        if (!data.TryGetProperty("data", out var component) || !TryReadString(component, "custom_id", out var customId))
        {
            await AcknowledgeInteractionAsync(interactionId, interactionToken, ct).ConfigureAwait(false);
            return;
        }
        ActionTarget? target = LookupAction(customId, data, settings);
        // Always acknowledge component interactions promptly, even when stale, duplicate, or unauthorized.
        try { await AcknowledgeInteractionAsync(interactionId, interactionToken, ct).ConfigureAwait(false); }
        catch { if (target is { } rejectedTarget) ReleaseTarget(rejectedTarget); return; }
        if (target is null)
        {
            if (customId.StartsWith("cab1.", StringComparison.Ordinal) || customId.StartsWith("cabs.", StringComparison.Ordinal))
                _ = SendInteractionStatusAsync(interactionToken, CoreText.Get("DiscordActionUnavailable"), ct);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (target.Value.StopRequest is not null)
                {
                    await target.Value.StopHandler!(target.Value.StopRequest, ct).ConfigureAwait(false);
                    RemoveProgress(target.Value.Nonce!);
                    await TryEditComponentsAsync(target.Value.ChannelId!, target.Value.MessageId!, null, ct).ConfigureAwait(false);
                    return;
                }
                var action = target.Value.Action!;
                ChatActionResult result = await action.Handler(target.Value.Decision, ct).ConfigureAwait(false);
                if (result == ChatActionResult.Busy)
                {
                    lock (_actionLock) action.InFlight = false;
                    await SendInteractionStatusAsync(interactionToken, CoreText.Get("DiscordActionBusy"), ct).ConfigureAwait(false);
                    return;
                }
                lock (_actionLock) _actions.Remove(action.Nonce);
                await TryCloseActionAsync(action.ChannelId!, action.MessageId!, ct).ConfigureAwait(false);
                if (result == ChatActionResult.Unavailable)
                    await SendInteractionStatusAsync(interactionToken, CoreText.Get("DiscordActionUnavailable"), ct).ConfigureAwait(false);
            }
            catch
            {
                if (target.Value.Action is { } action)
                {
                    lock (_actionLock) _actions.Remove(action.Nonce);
                    await TryCloseActionAsync(action.ChannelId!, action.MessageId!, ct).ConfigureAwait(false);
                    await SendInteractionStatusAsync(interactionToken, CoreText.Get("DiscordActionUnavailable"), ct).ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
    }

    private void ReleaseTarget(ActionTarget target)
    {
        lock (_actionLock)
        {
            if (target.Action is { } action) action.InFlight = false;
            if (target.Nonce is { } nonce && _progress.TryGetValue(nonce, out var progress)) progress.StopRequested = false;
        }
    }

    private ActionTarget? LookupAction(string customId, JsonElement interaction, BridgeSettings settings)
    {
        string? actor = interaction.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var memberUser)
            ? ReadString(memberUser, "id") : interaction.TryGetProperty("user", out var user) ? ReadString(user, "id") : null;
        string? guild = ReadString(interaction, "guild_id");
        string? channel = ReadString(interaction, "channel_id");
        string? message = interaction.TryGetProperty("message", out var messageElement) ? ReadString(messageElement, "id") : null;
        if (actor is null || channel is null || message is null) return null;
        lock (_actionLock)
        {
            if (customId.StartsWith("cab1.", StringComparison.Ordinal))
            {
                var parts = customId.Split('.');
                if (parts.Length != 3 || !_actions.TryGetValue(parts[1], out var action) || action.Consumed || action.InFlight ||
                    DateTimeOffset.UtcNow > action.ExpiresAt || action.Generation != Volatile.Read(ref _generation) ||
                    action.MessageId != message || action.ChannelId != channel || action.Origin.UserId != actor || action.Origin.GuildId != guild ||
                    !ChatAccessPolicy.IsAllowedRequest(settings, "discord", actor, action.Origin.ChannelId, action.Origin.ThreadId, guild)) return null;
                if (parts[2] is not ("approve" or "reject")) return null;
                action.InFlight = true;
                return new ActionTarget(action, parts[2] == "approve" ? ChatActionDecision.Approve : ChatActionDecision.Reject);
            }
            if (!customId.StartsWith("cabs.", StringComparison.Ordinal)) return null;
            var nonce = customId[5..];
            if (!_progress.TryGetValue(nonce, out var progress) || progress.StopRequested || DateTimeOffset.UtcNow > progress.ExpiresAt ||
                progress.Generation != Volatile.Read(ref _progressGeneration) || progress.Handle.MessageId != message || progress.Request.ThreadId != channel ||
                progress.Request.UserId != actor || progress.Request.GuildId != guild ||
                !ChatAccessPolicy.IsAllowedRequest(settings, "discord", actor, progress.Request.ChannelId,
                    progress.Request.ThreadId, guild)) return null;
            progress.StopRequested = true;
            string syntheticId = $"stop:{message}:{actor}:{Guid.NewGuid():N}";
            var synthetic = progress.Request with { Text = "!cab stop", MessageId = syntheticId, CancellationTargetMessageId = progress.Request.MessageId };
            return new ActionTarget(null, default, nonce, channel, message, synthetic, _activeOnRequest);
        }
    }

    private Func<ChatRequest, CancellationToken, Task>? _activeOnRequest;

    private async Task AcknowledgeInteractionAsync(string id, string interactionToken, CancellationToken ct)
    {
        // Type 6 defers an update to the component's original message without exposing response text.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBase,
            $"interactions/{Escape(id)}/{Escape(interactionToken)}/callback")) { Content = TransportHttp.JsonContent(new { type = 6 }) };
        await SendInteractionCallbackAsync(request, ct).ConfigureAwait(false);
    }

    private async Task SendInteractionStatusAsync(string interactionToken, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_applicationId)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_apiBase, $"webhooks/{Escape(_applicationId)}/{Escape(interactionToken)}"))
        {
            Content = TransportHttp.JsonContent(new
            {
                content = text,
                flags = 64,
                allowed_mentions = new { parse = Array.Empty<string>(), users = Array.Empty<string>(), roles = Array.Empty<string>(), replied_user = false }
            })
        };
        DiscordHttp.ConfigureRequest(request);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<DiscordMessageResponse> SendMessageAsync(string parentChannel, string threadId, string text,
        object? components, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = text,
            ["allowed_mentions"] = new { parse = Array.Empty<string>(), users = Array.Empty<string>(), roles = Array.Empty<string>(), replied_user = false }
        };
        if (components is not null) payload["components"] = components;
        // Discord message endpoints take the actual destination channel (thread IDs are channels).
        return await SendApiAsync<DiscordMessageResponse>(HttpMethod.Post, $"channels/{Escape(threadId)}/messages", payload, ct).ConfigureAwait(false);
    }

    private async Task EditMessageAsync(string channelId, string messageId, string text, object? components, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = text,
            ["allowed_mentions"] = new { parse = Array.Empty<string>(), users = Array.Empty<string>(), roles = Array.Empty<string>(), replied_user = false }
        };
        if (components is not null) payload["components"] = components;
        await SendApiAsync<DiscordMessageResponse>(HttpMethod.Patch,
            $"channels/{Escape(channelId)}/messages/{Escape(messageId)}", payload, ct).ConfigureAwait(false);
    }

    private async Task TryEditComponentsAsync(string channel, string message, object? components, CancellationToken ct = default)
    {
        try
        {
            var payload = new { components = components ?? Array.Empty<object>(),
                allowed_mentions = new { parse = Array.Empty<string>(), users = Array.Empty<string>(), roles = Array.Empty<string>(), replied_user = false } };
            await SendApiAsync<DiscordMessageResponse>(HttpMethod.Patch,
                $"channels/{Escape(channel)}/messages/{Escape(message)}", payload, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task TryCloseActionAsync(string channel, string message, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                content = CoreText.Get("DiscordActionClosed"),
                components = Array.Empty<object>(),
                allowed_mentions = new { parse = Array.Empty<string>(), users = Array.Empty<string>(), roles = Array.Empty<string>(), replied_user = false }
            };
            await SendApiAsync<DiscordMessageResponse>(HttpMethod.Patch,
                $"channels/{Escape(channel)}/messages/{Escape(message)}", payload, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<T> SendApiAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_apiBase, path));
            DiscordHttp.ConfigureRequest(request);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _token ?? throw new InvalidOperationException("Discord is not connected."));
            if (body is not null) request.Content = TransportHttp.JsonContent(body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)429 && attempt < 2)
            {
                double seconds = ReadRetryAfter(response);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0.05, 5)), ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new DiscordConfigurationException("authentication");
                string detail = $"Discord API request failed with HTTP {(int)response.StatusCode}.";
                throw new InvalidOperationException(detail);
            }
            if (response.Content.Headers.ContentLength is long declared && declared > 1024 * 1024)
                throw new InvalidOperationException("Discord returned oversized response metadata.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (bounded.Length > 1024 * 1024 - read) throw new InvalidOperationException("Discord returned oversized response metadata.");
                await bounded.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            bounded.Position = 0;
            try { return await JsonSerializer.DeserializeAsync<T>(bounded, TransportHttp.JsonOptions, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Discord returned an empty response."); }
            catch (JsonException) { throw new InvalidOperationException("Discord returned invalid JSON."); }
        }
    }

    private async Task SendInteractionCallbackAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var ackLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackLifetime.CancelAfter(TimeSpan.FromMilliseconds(2500));
        CancellationToken ackToken = ackLifetime.Token;
        for (int attempt = 0; ; attempt++)
        {
            using var retryRequest = new HttpRequestMessage(HttpMethod.Post, uri);
            DiscordHttp.ConfigureRequest(retryRequest);
            if (body is not null) retryRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(retryRequest, ackToken).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)429 && attempt < 1)
            {
                double seconds = ReadRetryAfter(response);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0.05, 1)), ackToken).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Discord interaction acknowledgement failed.");
            return;
        }
    }

    private static double ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return seconds;
        return 1;
    }

    private void ValidateRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(request.Provider, "discord", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Request provider must be Discord.", nameof(request));
        if (!_connected || _token is null) throw new InvalidOperationException("Discord is not connected.");
        var settings = Volatile.Read(ref _liveSettings);
        if (settings is not null && !ChatAccessPolicy.IsAllowedRequest(settings, "discord", request.UserId, request.ChannelId, request.ThreadId, request.GuildId))
            throw new InvalidOperationException("Discord request is no longer allowed by the current access settings.");
    }

    private void ValidateProgressHandle(ChatRequest request, ChatProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateRequest(request);
        if (!ReferenceEquals(message.Owner, _progressOwner) || !IsCurrentGeneration(message) || message.Provider != "discord" ||
            message.ChannelId != request.ChannelId || message.ThreadId != request.ThreadId || message.RequesterId != request.UserId)
            throw new InvalidOperationException("Progress message does not belong to this request and active connection.");
    }

    private bool IsCurrentGeneration(ChatProgressMessage message) => message.ConnectionGeneration == Volatile.Read(ref _progressGeneration);

    private void OnConnected()
    {
        lock (_stateLock) { _connected = true; Interlocked.Increment(ref _generation); }
        if (_reconnectAttempt > 0) ReconnectionStatusChanged?.Invoke(new ChatReconnectionStatus("discord", ChatReconnectionState.Reconnected, _reconnectAttempt, null));
        _reconnectAttempt = 0;
        Connected?.Invoke();
    }

    private void SetDisconnected() { lock (_stateLock) { _connected = false; Interlocked.Increment(ref _generation); } }
    private void ClearGatewaySession() { _sessionId = null; _resumeGatewayUrl = null; LastSequence = null; LastProcessedMessageSequence = null; }
    private Exception MapGatewayClose(int code) => code switch
    {
        4014 => new DiscordConfigurationException("message_content_intent"),
        4004 => new DiscordConfigurationException("authentication"),
        4007 or 4009 => ClearAndReconnect(),
        4010 or 4011 or 4012 or 4013 => new DiscordConfigurationException("gateway_configuration"),
        _ => new DiscordReconnectException(new WebSocketException($"Discord Gateway closed with code {code}."))
    };
    private Exception ClearAndReconnect() { ClearGatewaySession(); return new DiscordReconnectException(); }
    private static bool DiscordSettingsStillMatch(BridgeSettings current, BridgeSettings started) =>
        string.Equals(current.DiscordBotToken, started.DiscordBotToken, StringComparison.Ordinal);

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(attempt - 1, 0, 5))));
    private static int Jitter(int interval) => Random.Shared.Next(Math.Max(1, interval / 2), interval);
    private static Uri BuildGatewayUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Discord returned an invalid Gateway URL.");
        bool officialGateway = uri.Scheme == "wss" && (uri.IsDefaultPort || uri.Port == 443) &&
            (uri.IdnHost.Equals("discord.gg", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".discord.gg", StringComparison.OrdinalIgnoreCase));
#if CAB_DISCORD_FIXTURE
        // Isolated UI integration tests may use a cleartext socket on a numeric loopback IP only.
        bool loopbackFixture = uri.Scheme == "ws" && IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address) && !uri.IsDefaultPort;
#else
        bool loopbackFixture = false;
#endif
        if (!officialGateway && !loopbackFixture)
            throw new InvalidOperationException("Discord returned a Gateway URL outside its secure official host allowlist.");
        var builder = new UriBuilder(uri) { Query = "v=10&encoding=json" };
        return builder.Uri;
    }
    private static async Task SendGatewayAsync(IDiscordGatewaySocket socket, int op, object? data, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new { op, d = data }, TransportHttp.JsonOptions);
        await socket.SendAsync(json, ct).ConfigureAwait(false);
    }
    private static JsonDocument? ParseJson(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > MaxGatewayMessageBytes) return null;
        try { return JsonDocument.Parse(raw); } catch (JsonException) { return null; }
    }
    private static bool TryReadMessage(JsonElement data, out ParsedMessage? result)
    {
        result = null;
        if (!TryReadString(data, "id", out var messageId) || !TryReadString(data, "channel_id", out var channelId) ||
            !data.TryGetProperty("author", out var author) || !TryReadString(author, "id", out var userId) ||
            !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return false;
        string text = content.GetString() ?? "";
        string? guildId = ReadString(data, "guild_id");
        string? parent = ReadString(data, "thread_id");
        bool isBot = author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True;
        bool webhook = data.TryGetProperty("webhook_id", out var webhookId) && webhookId.ValueKind == JsonValueKind.String;
        int type = data.TryGetProperty("type", out var typeElement) && typeElement.TryGetInt32(out var parsedType) ? parsedType : -1;
        var files = ReadAttachments(data, out string? filesError);
        result = new ParsedMessage(messageId, channelId, userId, text, guildId, parent, isBot, webhook, type, files, filesError);
        return true;
    }

    private static IReadOnlyList<IncomingChatFile> ReadAttachments(JsonElement data, out string? error)
    {
        error = null;
        if (!data.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array) return Array.Empty<IncomingChatFile>();
        var items = new List<IncomingChatFile>();
        foreach (var item in attachments.EnumerateArray())
        {
            if (!TryReadString(item, "id", out var id)) { error = "invalid_attachment_metadata"; continue; }
            string name = ReadString(item, "filename") ?? "attachment";
            long size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) && parsedSize >= 0 ? parsedSize : 0;
            string? contentType = ReadString(item, "content_type");
            items.Add(new IncomingChatFile(id, name, size, contentType));
        }
        if (items.Count > TransportHttp.MaxIncomingFileCount) { error = "too_many_attachments"; items = items.Take(TransportHttp.MaxIncomingFileCount).ToList(); }
        return items;
    }

    internal static ChatRequest? ParseMessageRequest(JsonElement data, string? botUserId)
    {
        if (!TryReadMessage(data, out var message) || message is null || message.MessageType is not (0 or 19) || message.IsBot || message.IsWebhook || message.UserId == botUserId) return null;
        bool thread = !string.IsNullOrWhiteSpace(message.ParentChannelId);
        string parent = thread ? message.ParentChannelId! : message.ChannelId;
        bool start = HasTrigger(message.Text) || HasBotMention(message.Text, botUserId);
        if (!thread && !start) return null;
        string prompt = CleanPrompt(message.Text, botUserId);
        if (prompt.Length == 0 && message.Files.Count == 0) return null;
        return new ChatRequest("discord", parent, message.ChannelId, message.UserId, prompt, message.MessageId)
        {
            GuildId = message.GuildId,
            SourceChannelId = message.ChannelId,
            IncomingFiles = message.Files,
            IncomingFilesError = message.FilesError
        };
    }

    private static bool HasTrigger(string text) => Regex.IsMatch(text, @"^!cab(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool HasBotMention(string text, string? bot) => !string.IsNullOrWhiteSpace(bot) &&
        (text.Contains($"<@{bot}>", StringComparison.Ordinal) || text.Contains($"<@!{bot}>", StringComparison.Ordinal));
    private static string CleanPrompt(string text, string? bot)
    {
        string prompt = text;
        if (HasTrigger(prompt)) prompt = prompt[4..].Trim();
        if (!string.IsNullOrWhiteSpace(bot)) prompt = prompt.Replace($"<@{bot}>", " ", StringComparison.Ordinal).Replace($"<@!{bot}>", " ", StringComparison.Ordinal).Trim();
        return prompt;
    }
    private static string MakeThreadName(string text)
    {
        string name = Regex.Replace(text, @"<@!?\d+>", "").Trim();
        if (HasTrigger(name)) name = name[4..].Trim();
        name = Regex.Replace(name, @"\s+", " ");
        return string.IsNullOrWhiteSpace(name) ? "CAB request" : name[..Math.Min(100, name.Length)];
    }
    private static IEnumerable<string> SplitMessage(string text, int max)
    {
        text ??= "";
        if (text.Length == 0) { yield return "\u200b"; yield break; }
        while (text.Length > max)
        {
            int end = max;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            int boundary = text.LastIndexOf('\n', end - 1, Math.Min(end, 400));
            if (boundary > end - 400) end = boundary;
            if (end <= 0) end = max;
            yield return text[..end];
            text = text[end..].TrimStart('\n');
        }
        if (text.Length > 0) yield return text;
    }
    private static string LimitMessage(string text) => text.Length <= 2000 ? text : string.Concat(SplitMessage(text, 1999).First(), "…");
    private static object StopComponents(string nonce) => new[] { new { type = 1, components = new[] { new { type = 2, style = 4, label = CoreText.Get("DiscordActionStop"), custom_id = $"cabs.{nonce}" } } } };
    private static object ApprovalComponents(PendingAction action) => new[] { new { type = 1, components = new[]
    {
        new { type = 2, style = 3, label = CoreText.Get(action.Kind == ChatActionKind.ToolApproval ? "DiscordToolApprovalApprove" : "DiscordActionApprove"), custom_id = $"cab1.{action.Nonce}.approve" },
        new { type = 2, style = 4, label = CoreText.Get("DiscordActionReject"), custom_id = $"cab1.{action.Nonce}.reject" }
    } } };
    private static string CreateNonce() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.TryGetInt32(out value);
    }
    private static bool TryReadString(JsonElement element, string name, out string value) { value = ""; return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = p.GetString()!); }
    private static string? ReadString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool IsTransient(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException or WebSocketException ||
        ex is OperationCanceledException && !ct.IsCancellationRequested ||
        ex is InvalidOperationException invalid && (invalid.Message.Contains("HTTP 429", StringComparison.Ordinal) ||
            invalid.Message.Contains("HTTP 5", StringComparison.Ordinal));

    private void RemoveAction(PendingAction action)
    {
        lock (_actionLock) _actions.Remove(action.Nonce);
    }
    private void RemoveProgress(string nonce) { lock (_actionLock) _progress.Remove(nonce); }
    private string FindProgressNonce(ChatProgressMessage handle)
    {
        lock (_actionLock)
            return _progress.FirstOrDefault(entry => entry.Value.Handle == handle).Key ?? "";
    }
    private bool HasSeenMessage(string id) { lock (_seenLock) return _seenMessageIds.Contains(id); }
    private void RememberSeenMessage(string id)
    {
        lock (_seenLock)
        {
            if (!_seenMessageIds.Add(id)) return;
            _seenMessageOrder.Enqueue(id);
            while (_seenMessageOrder.Count > 10_000) _seenMessageIds.Remove(_seenMessageOrder.Dequeue());
        }
    }
    private void RenewProgressStop(ChatProgressMessage handle)
    {
        lock (_actionLock)
            foreach (var item in _progress)
                if (item.Value.Handle == handle) { item.Value.ExpiresAt = DateTimeOffset.UtcNow + ActionTtl; return; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _progressGeneration);
        SetDisconnected();
        _runLifetime?.Cancel();
        lock (_actionLock) { _actions.Clear(); _progress.Clear(); }
        _runLifetime?.Dispose();
        _http.Dispose();
        if (!ReferenceEquals(_cdnHttp, _http)) _cdnHttp.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task HandleActionsCallbackAsync(PendingAction action, ChatActionDecision decision, CancellationToken ct)
    {
        // delegated target to wrapper keeps the Gateway receive loop independent of broker latency
        ChatActionResult result = await action.Handler(decision, ct).ConfigureAwait(false);
        if (result != ChatActionResult.Busy) RemoveAction(action);
    }

    private sealed class ActionLease(DiscordGatewayTransport owner, PendingAction action) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.RemoveAction(action);
            if (action.MessageId is not null && action.ChannelId is not null && action.Generation == Volatile.Read(ref owner._generation))
                await owner.TryCloseActionAsync(action.ChannelId, action.MessageId).ConfigureAwait(false);
        }
    }

    private sealed class PendingAction(ChatRequest origin, ChatActionKind kind,
        Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler, long generation, DateTimeOffset expiresAt)
    {
        public string Nonce { get; } = CreateNonce();
        public ChatRequest Origin { get; } = origin;
        public ChatActionKind Kind { get; } = kind;
        public Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> Handler { get; } = handler;
        public long Generation { get; } = generation;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string? MessageId { get; set; }
        public string? ChannelId { get; set; }
        public bool InFlight { get; set; }
        public bool Consumed { get; set; }
    }
    private sealed class LiveProgress(ChatRequest request, ChatProgressMessage handle, long generation, DateTimeOffset expiresAt)
    {
        public ChatRequest Request { get; } = request;
        public ChatProgressMessage Handle { get; } = handle;
        public long Generation { get; } = generation;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public bool StopRequested { get; set; }
    }
    private readonly record struct ActionTarget(PendingAction? Action, ChatActionDecision Decision,
        string? Nonce = null, string? ChannelId = null, string? MessageId = null,
        ChatRequest? StopRequest = null, Func<ChatRequest, CancellationToken, Task>? StopHandler = null);
    private sealed record ParsedMessage(string MessageId, string ChannelId, string UserId, string Text,
        string? GuildId, string? ParentChannelId, bool IsBot, bool IsWebhook, int MessageType,
        IReadOnlyList<IncomingChatFile> Files, string? FilesError);
    private sealed record GatewayBotResponse([property: JsonPropertyName("url")] string? Url);
    private sealed record QueuedDiscordMessage(JsonElement Data, long? Sequence);
    private sealed record DiscordChannelResponse([property: JsonPropertyName("id")] string? Id);
    private sealed record DiscordChannelInfo([property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("guild_id")] string? GuildId,
        [property: JsonPropertyName("type")] int Type);
    private sealed record DiscordMessageResponse([property: JsonPropertyName("id")] string? Id);
    private sealed class DiscordReconnectException(Exception? inner = null) : Exception("Discord Gateway connection was interrupted.", inner);
}

internal interface IDiscordGatewaySocket : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task SendAsync(string text, CancellationToken cancellationToken);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class DiscordGatewayCloseException(int closeCode) : IOException("Discord Gateway closed the WebSocket.")
{
    internal int CloseCode { get; } = closeCode;
}

internal sealed class ClientDiscordGatewaySocket(Uri uri) : IDiscordGatewaySocket
{
    private readonly ClientWebSocket _socket = new();
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.SetRequestHeader("User-Agent", DiscordHttp.UserAgent);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        var content = new ArrayBufferWriter<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                int code = _socket.CloseStatus is { } status ? (int)status : 0;
                throw new DiscordGatewayCloseException(code);
            }
            if (result.MessageType != WebSocketMessageType.Text || content.WrittenCount + result.Count > DiscordGatewayTransport.MaxGatewayMessageBytes)
                throw new IOException("Discord Gateway message exceeded its limit.");
            content.Write(buffer.AsSpan(0, result.Count));
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(content.WrittenSpan);
    }
    public async ValueTask DisposeAsync() { _socket.Dispose(); await Task.CompletedTask.ConfigureAwait(false); }
}

internal sealed class DiscordContinuationTracker(int capacity = 1000)
{
    private readonly HashSet<SessionKey> _items = [];
    private readonly Dictionary<SessionKey, string> _owners = [];
    private readonly Queue<SessionKey> _order = new();
    private readonly object _gate = new();
    internal bool Contains(SessionKey key) { lock (_gate) return _items.Contains(key); }
    internal bool IsOwnedByAnotherUser(SessionKey key)
    {
        var scope = key with { UserId = null };
        lock (_gate) return _owners.TryGetValue(scope, out var owner) && !string.Equals(owner, key.UserId, StringComparison.Ordinal);
    }
    internal void Remember(SessionKey key)
    {
        lock (_gate)
        {
            if (!_items.Add(key)) return;
            _owners[key with { UserId = null }] = key.UserId ?? "";
            _order.Enqueue(key);
            while (_items.Count > capacity)
            {
                var expired = _order.Dequeue();
                _items.Remove(expired);
                var scope = expired with { UserId = null };
                if (!_items.Any(item => item with { UserId = null } == scope)) _owners.Remove(scope);
            }
        }
    }
}
