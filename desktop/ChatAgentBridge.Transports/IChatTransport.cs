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
    /// <summary>Files attached to this exact incoming message. IDs are opaque provider identifiers, never URLs.</summary>
    public IReadOnlyList<IncomingChatFile> IncomingFiles { get; init; } = Array.Empty<IncomingChatFile>();

    /// <summary>The Discord guild that owns the channel, or null for a direct message.</summary>
    public string? GuildId { get; init; }

    /// <summary>The Discord channel that contained MessageId; differs from ChannelId for a new public thread.</summary>
    public string? SourceChannelId { get; init; }

    /// <summary>A fixed, user-safe marker when incoming attachments could not be represented safely.</summary>
    public string? IncomingFilesError { get; init; }
    /// <summary>
    /// Transport-generated stop signal target. When set, the request represents a cancellation
    /// signal for this original request's MessageId and must not be treated as a new AI request.
    /// Ordinary posted messages leave this unset.
    /// </summary>
    public string? CancellationTargetMessageId { get; init; }
}

/// <summary>Opaque file identity and provider supplied display metadata for an incoming message attachment.</summary>
public sealed record IncomingChatFile(string Id, string Name, long Size, string? ContentType);

/// <summary>Optional support for resolving and downloading files attached to an approved incoming request.</summary>
public interface IChatIncomingFileTransport
{
    Task<IReadOnlyList<IncomingChatFile>> ResolveIncomingFilesAsync(ChatRequest request, CancellationToken cancellationToken);

    Task DownloadFileAsync(
        ChatRequest request,
        IncomingChatFile file,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken);
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

public enum ChatActionKind
{
    RequestApproval,
    ToolApproval
}

public enum ChatActionDecision
{
    Approve,
    Reject
}

public enum ChatActionResult
{
    Completed,
    Busy,
    Unavailable
}

/// <summary>Posts transport-owned approval controls and routes their one-time decisions.</summary>
public interface IChatActionTransport
{
    /// <summary>
    /// Publishes an approval message with buttons. The returned lease invalidates the controls when
    /// disposed. A callback returning Busy keeps the controls available for a later click.
    /// </summary>
    Task<IAsyncDisposable> PostActionsAsync(
        ChatRequest origin,
        string text,
        ChatActionKind kind,
        Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler,
        CancellationToken cancellationToken);
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
