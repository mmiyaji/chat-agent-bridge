using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public interface IChatConnectionEvents
{
    event Action? Connected;
}

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
