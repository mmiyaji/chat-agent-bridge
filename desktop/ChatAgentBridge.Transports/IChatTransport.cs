using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public interface IChatConnectionEvents
{
    event Action? Connected;
}

/// <summary>Refreshes request filters without changing the authenticated socket identity.</summary>
public interface IChatLiveAccessSettings
{
    void UpdateAccessSettings(BridgeSettings settings);
}

/// <summary>Reports automatic recovery from a transient chat socket failure.</summary>
public interface IChatReconnectionEvents
{
    event Action<ChatReconnectionStatus>? ReconnectionStatusChanged;
}

public enum ChatReconnectionState
{
    Reconnecting,
    Reconnected
}

public sealed record ChatReconnectionStatus(
    string Provider,
    ChatReconnectionState State,
    int Attempt,
    TimeSpan? NextRetryDelay);

public interface IChatConnectionNotifications
{
    Task NotifyConnectionAsync(BridgeSettings settings, string message, CancellationToken cancellationToken);
}

public sealed record ChatRequest(
    string Provider,
    string ChannelId,
    string ThreadId,
    string UserId,
    string Text,
    string MessageId)
{
    /// <summary>
    /// Transport-generated stop signal target. When set, the request represents a cancellation
    /// signal for this original request's MessageId and must not be treated as a new AI request.
    /// Ordinary posted messages leave this unset.
    /// </summary>
    public string? CancellationTargetMessageId { get; init; }
}

public interface IChatTransport : IAsyncDisposable
{
    Task RunAsync(
        BridgeSettings settings,
        Func<ChatRequest, CancellationToken, Task> onRequest,
        CancellationToken cancellationToken);

    Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken);
}

/// <summary>Optional support for uploading prepared files to the active request thread.</summary>
public interface IChatFileTransport
{
    Task<IReadOnlyList<string>> UploadFilesAsync(
        ChatRequest request,
        IReadOnlyList<PreparedFileAttachment> files,
        CancellationToken cancellationToken);
}

/// <summary>Optional support for posting a transport-owned progress message and editing that same message.</summary>
public interface IChatProgressTransport
{
    Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken);

    Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken);
}

/// <summary>Optional lifecycle notification for transports that associate actions with live progress posts.</summary>
public interface IChatProgressLifecycleTransport
{
    /// <summary>Marks a progress post as no longer eligible for transport-specific actions such as stop reactions.</summary>
    void CompleteProgress(ChatProgressMessage message);

    /// <summary>Invalidates actions immediately, then optionally removes transport-owned controls.</summary>
    Task CompleteProgressAsync(ChatProgressMessage message, CancellationToken cancellationToken = default)
    {
        CompleteProgress(message);
        return Task.CompletedTask;
    }
}

/// <summary>An opaque handle for a progress message created by an <see cref="IChatProgressTransport"/>.</summary>
public sealed class ChatProgressMessage
{
    internal ChatProgressMessage(string provider, string channelId, string threadId, string requesterId,
        string messageId, object owner, long connectionGeneration)
    {
        Provider = provider;
        ChannelId = channelId;
        ThreadId = threadId;
        RequesterId = requesterId;
        MessageId = messageId;
        Owner = owner;
        ConnectionGeneration = connectionGeneration;
    }

    internal string Provider { get; }
    internal string ChannelId { get; }
    internal string ThreadId { get; }
    internal string RequesterId { get; }
    internal string MessageId { get; }
    internal object Owner { get; }
    internal long ConnectionGeneration { get; }
}
