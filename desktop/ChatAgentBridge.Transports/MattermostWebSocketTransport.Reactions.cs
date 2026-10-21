using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport
{
    private const int MaxLiveProgressPosts = 2_000;
    private readonly object _liveProgressSync = new();
    private readonly Dictionary<string, LiveProgressPost> _liveProgressPosts = new(StringComparer.Ordinal);

    private void RegisterLiveProgressPost(ChatRequest request, ChatProgressMessage handle, CancellationToken cancellationToken)
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
            _liveProgressPosts[handle.MessageId] = new LiveProgressPost(request, handle.ConnectionGeneration);
        }
    }

    public void CompleteProgress(ChatProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ReferenceEquals(message.Owner, _progressOwner))
            return;

        lock (_liveProgressSync)
        {
            if (_liveProgressPosts.TryGetValue(message.MessageId, out var entry) &&
                entry.ConnectionGeneration == message.ConnectionGeneration &&
                string.Equals(entry.Request.ChannelId, message.ChannelId, StringComparison.Ordinal) &&
                string.Equals(entry.Request.ThreadId, message.ThreadId, StringComparison.Ordinal) &&
                string.Equals(entry.Request.UserId, message.RequesterId, StringComparison.Ordinal))
                _liveProgressPosts.Remove(message.MessageId);
        }
    }

    private void ClearLiveProgressPosts()
    {
        lock (_liveProgressSync)
        {
            _liveProgressPosts.Clear();
        }
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
        if (!string.Equals(emoji, "octagonal_sign", StringComparison.Ordinal) ||
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
                progress.ConnectionGeneration != generation)
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

            // Consume before dispatch so duplicate reaction_added events cannot enqueue repeated stops.
            _liveProgressPosts.Remove(postId);
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

    private sealed record LiveProgressPost(ChatRequest Request, long ConnectionGeneration);
}
