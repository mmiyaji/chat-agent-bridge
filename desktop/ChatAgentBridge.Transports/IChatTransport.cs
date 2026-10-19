using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public interface IChatConnectionEvents
{
    event Action? Connected;
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
    string MessageId);

public interface IChatTransport : IAsyncDisposable
{
    Task RunAsync(
        BridgeSettings settings,
        Func<ChatRequest, CancellationToken, Task> onRequest,
        CancellationToken cancellationToken);

    Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken);
}

/// <summary>Optional support for posting a transport-owned progress message and editing that same message.</summary>
public interface IChatProgressTransport
{
    Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken);

    Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken);
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
