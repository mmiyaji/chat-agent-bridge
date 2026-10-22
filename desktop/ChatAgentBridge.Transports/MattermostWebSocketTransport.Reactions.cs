using System.Net.Http.Headers;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport
{
    private const int MaxLiveProgressPosts = 2_000;
    private const string StopEmoji = "octagonal_sign";
    private static readonly TimeSpan ReactionRequestTimeout = TimeSpan.FromSeconds(2);
    private readonly object _liveProgressSync = new();
    private readonly Dictionary<string, LiveProgressPost> _liveProgressPosts = new(StringComparer.Ordinal);

    private void RegisterLiveProgressPost(ChatRequest request, ChatProgressMessage handle, HttpClient http,
        Uri root, string token, string? botUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
            handle.ConnectionGeneration != Volatile.Read(ref _connectionGeneration))
            throw new OperationCanceledException("Mattermost connection changed while creating the progress post.", cancellationToken);

        lock (_liveProgressSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
                handle.ConnectionGeneration != Volatile.Read(ref _connectionGeneration))
                throw new OperationCanceledException("Mattermost connection changed while creating the progress post.", cancellationToken);
            if (_liveProgressPosts.Count >= MaxLiveProgressPosts && !_liveProgressPosts.ContainsKey(handle.MessageId))
                throw new InvalidOperationException("Mattermost has reached its live progress post limit.");
            _liveProgressPosts[handle.MessageId] = new LiveProgressPost(request, handle.ConnectionGeneration,
                handle, http, root, token, botUserId);
        }
    }

    public void CompleteProgress(ChatProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = TakeLiveProgressPost(message);
    }

    public async Task CompleteProgressAsync(ChatProgressMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = TakeLiveProgressPost(message); // Invalidate before any network wait.
        if (entry is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReactionRequestTimeout);
            await RemoveStopReactionAsync(message.MessageId, entry, ReactionRequestTimeout, timeout.Token).ConfigureAwait(false);
            await WaitForPendingCleanupAsync(entry, ReactionRequestTimeout, timeout.Token).ConfigureAwait(false);
        }
    }

    private LiveProgressPost? TakeLiveProgressPost(ChatProgressMessage message)
    {
        if (!ReferenceEquals(message.Owner, _progressOwner) ||
            !string.Equals(message.Provider, "mattermost", StringComparison.Ordinal))
            return null;
        lock (_liveProgressSync)
        {
            if (!_liveProgressPosts.TryGetValue(message.MessageId, out var entry) ||
                entry.Generation != message.ConnectionGeneration ||
                !ReferenceEquals(entry.Handle.Owner, message.Owner) ||
                !string.Equals(entry.Handle.Provider, message.Provider, StringComparison.Ordinal) ||
                !string.Equals(entry.Handle.ChannelId, message.ChannelId, StringComparison.Ordinal) ||
                !string.Equals(entry.Handle.ThreadId, message.ThreadId, StringComparison.Ordinal) ||
                !string.Equals(entry.Handle.RequesterId, message.RequesterId, StringComparison.Ordinal))
                return null;
            _liveProgressPosts.Remove(message.MessageId);
            return entry;
        }
    }

    private async Task AddStopReactionAsync(string postId, long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LiveProgressPost? entry;
        lock (_liveProgressSync)
        {
            if (!_liveProgressPosts.TryGetValue(postId, out entry) || entry.Generation != generation ||
                _disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
                generation != Volatile.Read(ref _connectionGeneration))
                throw new OperationCanceledException("Mattermost connection changed before the stop reaction could be added.", cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(entry.BotUserId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReactionRequestTimeout);
        HttpRequestMessage? request = null;
        Task<HttpResponseMessage>? sendTask = null;
        try
        {
            var body = TransportHttp.JsonContent(new { user_id = entry.BotUserId, post_id = postId, emoji_name = StopEmoji });
            request = new HttpRequestMessage(HttpMethod.Post, new Uri(entry.Root, "api/v4/reactions")) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Token);
            sendTask = entry.Http.SendAsync(request, timeout.Token);
            using var response = await sendTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var seeded = response.IsSuccessStatusCode;
            request.Dispose();
            timeout.Dispose();
            if (seeded && !IsRegisteredLiveProgressPost(entry))
                await RemoveStopReactionAsync(postId, entry, ReactionRequestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            if (sendTask is not null && request is not null)
                entry.SetPendingSeedCleanup(ObserveLateSeedAsync(sendTask, request, timeout, entry, postId));
            else
            {
                request?.Dispose();
                timeout.Dispose();
            }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            if (sendTask is not null && request is not null)
                entry.SetPendingSeedCleanup(ObserveLateSeedAsync(sendTask, request, timeout, entry, postId));
            else
            {
                request?.Dispose();
                timeout.Dispose();
            }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        }
    }

    private bool IsRegisteredLiveProgressPost(LiveProgressPost entry)
    {
        lock (_liveProgressSync)
            return _liveProgressPosts.TryGetValue(entry.Handle.MessageId, out var active) &&
                ReferenceEquals(active, entry) && active.Generation == Volatile.Read(ref _connectionGeneration) &&
                !_disposed && _connected;
    }

    private static async Task ObserveLateSeedAsync(Task<HttpResponseMessage> sendTask, HttpRequestMessage request,
        CancellationTokenSource timeout, LiveProgressPost entry, string postId)
    {
        try
        {
            try { using var response = await sendTask.ConfigureAwait(false); }
            catch (Exception) { }
            await RemoveStopReactionAsync(postId, entry, ReactionRequestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally
        {
            request.Dispose();
            timeout.Dispose();
        }
    }

    private static async Task RemoveStopReactionAsync(string postId, LiveProgressPost entry,
        TimeSpan timeoutBudget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.BotUserId)) return;
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutBudget);
        HttpRequestMessage? request = null;
        Task<HttpResponseMessage>? sendTask = null;
        try
        {
            var path = $"api/v4/users/{Uri.EscapeDataString(entry.BotUserId)}/posts/{Uri.EscapeDataString(postId)}/reactions/{StopEmoji}";
            request = new HttpRequestMessage(HttpMethod.Delete, new Uri(entry.Root, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Token);
            sendTask = entry.Http.SendAsync(request, timeout.Token);
            using var response = await sendTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            request.Dispose();
            timeout.Dispose();
        }
        catch (Exception)
        {
            if (sendTask is not null && request is not null)
                entry.AddPendingCleanupTask(ObserveLateResponseAsync(sendTask, request, timeout));
            else
            {
                request?.Dispose();
                timeout.Dispose();
            }
        }
    }

    private static async Task ObserveLateResponseAsync(Task<HttpResponseMessage> sendTask,
        HttpRequestMessage request, CancellationTokenSource timeout)
    {
        try { using var response = await sendTask.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            request.Dispose();
            timeout.Dispose();
        }
    }

    private async Task ClearLiveProgressPostsAsync(TimeSpan timeoutBudget)
    {
        LiveProgressPost[] entries;
        lock (_liveProgressSync)
        {
            entries = _liveProgressPosts.Values.ToArray();
            _liveProgressPosts.Clear();
        }
        if (entries.Length == 0) return;

        using var timeout = new CancellationTokenSource(timeoutBudget);
        var nextIndex = -1;
        var workerCount = Math.Min(8, entries.Length);
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (!timeout.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= entries.Length) return;
                var entry = entries[index];
                await RemoveStopReactionAsync(entry.Handle.MessageId, entry, timeoutBudget, timeout.Token).ConfigureAwait(false);
                await WaitForPendingCleanupAsync(entry, timeoutBudget, timeout.Token).ConfigureAwait(false);
            }
        }).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async Task WaitForPendingCleanupAsync(LiveProgressPost entry, TimeSpan timeoutBudget,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutBudget);
        var pending = new List<Task>();
        if (entry.PendingSeedCleanup is { } seed) pending.Add(seed);
        pending.AddRange(entry.PendingCleanupTasks);
        try { await Task.WhenAll(pending).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task ProcessStopReactionAsync(JsonElement envelope, BridgeSettings settings,
        Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken)
    {
        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("reaction", out var reactionJson))
            return;

        using var reactionDocument = ParseReaction(reactionJson);
        if (reactionDocument is null)
            return;

        var reaction = reactionDocument.RootElement;
        var emoji = ReadString(reaction, "emoji_name");
        var actorId = ReadString(reaction, "user_id");
        var postId = ReadString(reaction, "post_id");
        var reactionId = ReadString(reaction, "id");
        if (!string.Equals(emoji, StopEmoji, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(postId) ||
            string.Equals(actorId, _currentUserId, StringComparison.Ordinal))
            return;

        if (!TryReadOptionalString(reaction, "channel_id", out var reactionChannel) ||
            !TryReadOptionalString(data, "channel_id", out var dataChannel))
            return;

        string? broadcastChannel = null;
        if (envelope.TryGetProperty("broadcast", out var broadcast) &&
            !TryReadOptionalString(broadcast, "channel_id", out broadcastChannel))
            return;

        var generation = Volatile.Read(ref _connectionGeneration);
        ChatRequest? original = null;
        lock (_liveProgressSync)
        {
            if (_disposed || !_connected || _runLifetime?.IsCancellationRequested == true ||
                !_liveProgressPosts.TryGetValue(postId, out var progress) ||
                progress.Generation != generation || progress.StopRequested)
                return;

            original = progress.Request;
            if (!string.Equals(original.Provider, "mattermost", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(original.ChannelId, _channelId, StringComparison.Ordinal) ||
                !string.Equals(original.ChannelId, settings.MattermostChannelId, StringComparison.Ordinal) ||
                !string.Equals(actorId, original.UserId, StringComparison.Ordinal) ||
                reactionChannel is not null && !string.Equals(reactionChannel, original.ChannelId, StringComparison.Ordinal) ||
                dataChannel is not null && !string.Equals(dataChannel, original.ChannelId, StringComparison.Ordinal) ||
                broadcastChannel is not null && !string.Equals(broadcastChannel, original.ChannelId, StringComparison.Ordinal) ||
                !ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", original.UserId,
                    original.ChannelId, original.ThreadId))
                return;

            progress.StopRequested = true;
        }

        var synthetic = new ChatRequest("mattermost", original.ChannelId, original.ThreadId,
            original.UserId, "!cab stop", string.IsNullOrWhiteSpace(reactionId)
                ? $"stop-reaction:{postId}:{actorId}"
                : $"reaction:{reactionId}")
        {
            CancellationTargetMessageId = original.MessageId
        };
        await onRequest(synthetic, cancellationToken).ConfigureAwait(false);
    }

    private static JsonDocument? ParseReaction(JsonElement reaction)
    {
        try
        {
            return reaction.ValueKind switch
            {
                JsonValueKind.String => JsonDocument.Parse(reaction.GetString() ?? ""),
                JsonValueKind.Object => JsonDocument.Parse(reaction.GetRawText()),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool TryReadOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return true;
    }

    private sealed class LiveProgressPost(ChatRequest request, long generation, ChatProgressMessage handle,
        HttpClient http, Uri root, string token, string? botUserId)
    {
        public ChatRequest Request { get; } = request;
        public long Generation { get; } = generation;
        public ChatProgressMessage Handle { get; } = handle;
        public HttpClient Http { get; } = http;
        public Uri Root { get; } = root;
        public string Token { get; } = token;
        public string? BotUserId { get; } = botUserId;
        public bool StopRequested { get; set; }
        private readonly object _pendingSync = new();
        private Task? _pendingSeedCleanup;
        private readonly List<Task> _pendingCleanupTasks = [];
        public Task? PendingSeedCleanup { get { lock (_pendingSync) return _pendingSeedCleanup; } }
        public Task[] PendingCleanupTasks { get { lock (_pendingSync) return _pendingCleanupTasks.ToArray(); } }
        public void SetPendingSeedCleanup(Task task) { lock (_pendingSync) _pendingSeedCleanup = task; }
        public void AddPendingCleanupTask(Task task) { lock (_pendingSync) _pendingCleanupTasks.Add(task); }
    }
}
