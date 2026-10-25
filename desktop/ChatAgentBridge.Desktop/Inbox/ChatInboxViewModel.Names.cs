using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly HashSet<SessionKey> _nameLookupRequested = [];
    private CancellationTokenSource _nameLookupCancellation = new();
    private Task _nameLookupTask = Task.CompletedTask;

    // The inbox otherwise only reads names that History resolved. Look up each live requester
    // once per session; the directory applies its own freshness, budget, and cooldown rules.
    private void StartConversationNameLookup()
    {
        if (_closing || _conversationPersistencePaused) return;
        var keys = Conversations
            .Where(conversation => conversation.Requests.Count > 0 && conversation.Snapshot is { } snapshot &&
                (snapshot.UserName is null || snapshot.ChannelName is null))
            .Select(conversation => conversation.Snapshot!.Key)
            .Where(_nameLookupRequested.Add)
            .ToArray();
        if (keys.Length == 0) return;
        _nameLookupTask = LookupConversationNamesAfterAsync(_nameLookupTask, keys, _nameLookupCancellation.Token);
    }

    private async Task LookupConversationNamesAfterAsync(Task previous, SessionKey[] keys, CancellationToken cancellationToken)
    {
        await previous;
        try
        {
            var settings = await _store.LoadAsync(cancellationToken);
            using var directory = new ChatIdentityDirectory(_store.DirectoryPath);
            await directory.ResolveAsync(settings, keys, cancellationToken);
        }
        catch (Exception)
        {
            // Optional display metadata must never interrupt request handling. Keep the fallback labels.
            return;
        }
        if (cancellationToken.IsCancellationRequested || _closing) return;
        // The save pass re-reads the updated cache and applies the labels to the matching rows.
        QueueConversationSave();
    }

    // Data maintenance and shutdown replace or remove the cache file; drain the lookup first.
    private async Task StopConversationNameLookupAsync()
    {
        _nameLookupCancellation.Cancel();
        await _nameLookupTask;
    }

    private void ResetConversationNameLookup()
    {
        if (_nameLookupCancellation.IsCancellationRequested)
        {
            _nameLookupCancellation.Dispose();
            _nameLookupCancellation = new();
        }
        _nameLookupRequested.Clear();
    }
}
