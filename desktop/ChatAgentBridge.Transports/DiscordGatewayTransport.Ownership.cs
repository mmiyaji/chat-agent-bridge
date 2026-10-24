using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed partial class DiscordGatewayTransport
{
    private readonly DiscordPendingOwnerReservations _pendingOwners = new(500);

    private async Task LoadPendingOwnerReservationsAsync(SessionStore sessions, CancellationToken cancellationToken)
    {
        IReadOnlyList<InboxConversationSnapshot> snapshots;
        try { snapshots = await new InboxConversationStore(sessions.DirectoryPath).ListAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) { return; }

        foreach (var snapshot in snapshots.Take(500))
        {
            var key = snapshot.Key;
            if (key.Provider != "discord" || key.Origin != "discord" || !IsSnowflake(key.UserId) ||
                !IsSnowflake(key.ChannelId) || !IsSnowflake(key.ThreadId))
                continue;
            _pendingOwners.Reserve(key.ChannelId, key.ThreadId, key.UserId!);
        }
    }

    private static bool IsSnowflake(string? value)
    {
        try { return !string.IsNullOrWhiteSpace(value) && ChatAccessPolicy.NormalizeDiscordSnowflakes(value) == value; }
        catch (FormatException) { return false; }
    }

    private bool IsPendingOwnerMismatch(SessionKey key) => _pendingOwners.IsOwnedByAnotherUser(key.ChannelId, key.ThreadId, key.UserId!);
}

internal sealed class DiscordPendingOwnerReservations(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Channel, string Thread), string> _owners = new();
    private readonly Queue<(string Channel, string Thread)> _order = new();

    internal void Reserve(string channelId, string threadId, string userId)
    {
        lock (_gate)
        {
            var scope = (channelId, threadId);
            if (_owners.ContainsKey(scope)) return;
            _owners.Add(scope, userId);
            _order.Enqueue(scope);
            while (_owners.Count > capacity) _owners.Remove(_order.Dequeue());
        }
    }

    internal bool IsOwnedByAnotherUser(string channelId, string threadId, string userId)
    {
        lock (_gate)
            return _owners.TryGetValue((channelId, threadId), out var owner) && !string.Equals(owner, userId, StringComparison.Ordinal);
    }
}
