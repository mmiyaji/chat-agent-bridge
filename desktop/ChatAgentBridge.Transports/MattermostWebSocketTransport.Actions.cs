using System.Net.Http.Headers;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport
{
    private const int MaxPendingActions = 200;
    private const string ApproveEmoji = "white_check_mark";
    private const string RejectEmoji = "x";
    private static readonly string[] ActionSeedEmojis = [ApproveEmoji, RejectEmoji];
    private readonly object _actionSync = new();
    private readonly Dictionary<string, PendingAction> _pendingActions = new(StringComparer.Ordinal);

    private static ChatActionDecision? DecisionForEmoji(string? emoji) => emoji switch
    {
        ApproveEmoji or "+1" or "thumbsup" => ChatActionDecision.Approve,
        RejectEmoji or "-1" or "thumbsdown" => ChatActionDecision.Reject,
        _ => null
    };

    public async Task<IAsyncDisposable> PostActionsAsync(ChatRequest origin, string text, ChatActionKind kind,
        Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateProgressRequest(origin);
        lock (_actionSync)
            if (_pendingActions.Count >= MaxPendingActions)
                throw new InvalidOperationException("Mattermost has reached its pending action limit.");

        var generation = Volatile.Read(ref _connectionGeneration);
        var root = _serverUri!;
        var token = _token!;
        var http = ActiveHttp;
        var botUserId = _currentUserId;
        using var body = TransportHttp.JsonContent(new { channel_id = origin.ChannelId, root_id = origin.ThreadId, message = text });
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/posts")) { Content = body };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost action post failed with HTTP {(int)response.StatusCode}.");
        var created = await ReadMattermostPostAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(created.ChannelId, origin.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(string.IsNullOrWhiteSpace(created.RootId) ? created.Id : created.RootId, origin.ThreadId, StringComparison.Ordinal) ||
            string.Equals(created.Id, origin.MessageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost returned an action post outside the requested thread.");
        cancellationToken.ThrowIfCancellationRequested();

        var entry = new PendingAction(created.Id!, origin, text, generation, handler, http, root, token, botUserId);
        lock (_actionSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
                generation != Volatile.Read(ref _connectionGeneration))
                throw new OperationCanceledException("Mattermost connection changed while creating the action post.", cancellationToken);
            if (_pendingActions.Count >= MaxPendingActions)
                throw new InvalidOperationException("Mattermost has reached its pending action limit.");
            _pendingActions[entry.PostId] = entry;
        }

        var lease = new ActionLease(this, entry);
        if (!string.IsNullOrWhiteSpace(botUserId))
        {
            try
            {
                foreach (var emoji in ActionSeedEmojis)
                    await SeedActionReactionAsync(entry, emoji, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        return lease;
    }

    private static async Task SeedActionReactionAsync(PendingAction entry, string emoji, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReactionRequestTimeout);
        try
        {
            using var body = TransportHttp.JsonContent(new { user_id = entry.BotUserId, post_id = entry.PostId, emoji_name = emoji });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(entry.Root, "api/v4/reactions")) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Token);
            using var response = await entry.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
    }

    private static async Task RemoveActionReactionsAsync(PendingAction entry, TimeSpan timeoutBudget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.BotUserId)) return;
        foreach (var emoji in ActionSeedEmojis)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutBudget);
            try
            {
                var path = $"api/v4/users/{Uri.EscapeDataString(entry.BotUserId)}/posts/{Uri.EscapeDataString(entry.PostId)}/reactions/{Uri.EscapeDataString(emoji)}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(entry.Root, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Token);
                using var response = await entry.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    // Marks the entry completed and reports whether this call removed the registration (and so owns cleanup).
    private bool TakePendingAction(PendingAction entry)
    {
        lock (_actionSync)
        {
            entry.Completed = true;
            return _pendingActions.TryGetValue(entry.PostId, out var active) && ReferenceEquals(active, entry) &&
                _pendingActions.Remove(entry.PostId);
        }
    }

    private async Task ClearPendingActionsAsync(TimeSpan timeoutBudget)
    {
        PendingAction[] entries;
        lock (_actionSync)
        {
            entries = _pendingActions.Values.ToArray();
            _pendingActions.Clear();
            foreach (var entry in entries) entry.Completed = true;
        }
        if (entries.Length == 0) return;

        using var timeout = new CancellationTokenSource(timeoutBudget);
        try
        {
            await Task.WhenAll(entries.Select(e => RemoveActionReactionsAsync(e, timeoutBudget, timeout.Token)))
                .WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    // Returns the dispatched decision task (not awaited by the socket loop), or a completed task when ignored.
    internal Task ProcessActionReactionAsync(JsonElement envelope, BridgeSettings settings)
    {
        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("reaction", out var reactionJson))
            return Task.CompletedTask;

        using var reactionDocument = ParseReaction(reactionJson);
        if (reactionDocument is null) return Task.CompletedTask;

        var reaction = reactionDocument.RootElement;
        var decision = DecisionForEmoji(ReadString(reaction, "emoji_name"));
        var actorId = ReadString(reaction, "user_id");
        var postId = ReadString(reaction, "post_id");
        if (decision is null || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(postId) ||
            string.Equals(actorId, _currentUserId, StringComparison.Ordinal))
            return Task.CompletedTask;

        if (!TryReadOptionalString(reaction, "channel_id", out var reactionChannel) ||
            !TryReadOptionalString(data, "channel_id", out var dataChannel))
            return Task.CompletedTask;

        string? broadcastChannel = null;
        if (envelope.TryGetProperty("broadcast", out var broadcast) &&
            !TryReadOptionalString(broadcast, "channel_id", out broadcastChannel))
            return Task.CompletedTask;

        var generation = Volatile.Read(ref _connectionGeneration);
        PendingAction? entry;
        CancellationToken lifetime;
        lock (_actionSync)
        {
            if (_disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
                !_pendingActions.TryGetValue(postId, out entry) ||
                entry.Generation != generation || entry.Completed || entry.InFlight)
                return Task.CompletedTask;

            var original = entry.Origin;
            if (!string.Equals(original.Provider, "mattermost", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(original.ChannelId, _channelId, StringComparison.Ordinal) ||
                !string.Equals(original.ChannelId, settings.MattermostChannelId, StringComparison.Ordinal) ||
                !string.Equals(actorId, original.UserId, StringComparison.Ordinal) ||
                reactionChannel is not null && !string.Equals(reactionChannel, original.ChannelId, StringComparison.Ordinal) ||
                dataChannel is not null && !string.Equals(dataChannel, original.ChannelId, StringComparison.Ordinal) ||
                broadcastChannel is not null && !string.Equals(broadcastChannel, original.ChannelId, StringComparison.Ordinal) ||
                !ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", original.UserId,
                    original.ChannelId, original.ThreadId))
                return Task.CompletedTask;

            entry.InFlight = true;
            lifetime = _runLifetime?.Token ?? CancellationToken.None;
        }

        return Task.Run(() => RunActionDecisionAsync(entry, decision.Value, lifetime));
    }

    private async Task RunActionDecisionAsync(PendingAction entry, ChatActionDecision decision, CancellationToken cancellationToken)
    {
        var result = ChatActionResult.Unavailable;
        try { result = await entry.Handler(decision, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { }

        if (result == ChatActionResult.Busy)
        {
            lock (_actionSync) entry.InFlight = false;
            return;
        }
        // The approval flow usually disposes the lease (removing the reactions) before the handler returns.
        if (TakePendingAction(entry))
            await RemoveActionReactionsAsync(entry, ReactionRequestTimeout, CancellationToken.None).ConfigureAwait(false);
        // Mark the decided post so the thread no longer reads as waiting; the reaction emoji is the decision.
        if (result == ChatActionResult.Completed)
            await MarkActionDecidedAsync(entry, decision == ChatActionDecision.Approve ? ApproveEmoji : RejectEmoji).ConfigureAwait(false);
    }

    private static async Task MarkActionDecidedAsync(PendingAction entry, string emoji)
    {
        using var timeout = new CancellationTokenSource(ReactionRequestTimeout);
        try
        {
            using var body = TransportHttp.JsonContent(new { message = $"{entry.Text}{Environment.NewLine}{Environment.NewLine}→ :{emoji}:" });
            using var patch = new HttpRequestMessage(HttpMethod.Put,
                new Uri(entry.Root, $"api/v4/posts/{Uri.EscapeDataString(entry.PostId)}/patch")) { Content = body };
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Token);
            using var response = await entry.Http.SendAsync(patch, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    internal int PendingActionCount { get { lock (_actionSync) return _pendingActions.Count; } }

    private sealed class ActionLease(MattermostWebSocketTransport owner, PendingAction entry) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (owner.TakePendingAction(entry))
                    await RemoveActionReactionsAsync(entry, ReactionRequestTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    private sealed class PendingAction(string postId, ChatRequest origin, string text, long generation,
        Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler,
        HttpClient http, Uri root, string token, string? botUserId)
    {
        public string PostId { get; } = postId;
        public ChatRequest Origin { get; } = origin;
        public string Text { get; } = text;
        public long Generation { get; } = generation;
        public Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> Handler { get; } = handler;
        public HttpClient Http { get; } = http;
        public Uri Root { get; } = root;
        public string Token { get; } = token;
        public string? BotUserId { get; } = botUserId;
        public bool InFlight { get; set; }
        public bool Completed { get; set; }
    }
}
