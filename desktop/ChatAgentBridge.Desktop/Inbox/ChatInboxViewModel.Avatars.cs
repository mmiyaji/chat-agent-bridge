using System.Security.Cryptography;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxConversation
{
    [ObservableProperty] public partial string? AvatarCachePath { get; set; }
}

public sealed partial class ChatInboxViewModel
{
    private readonly CancellationTokenSource _avatarLifetime = new();
    private readonly object _avatarSync = new();
    private readonly Dictionary<AvatarIdentity, AvatarLoad> _avatarLoads = [];
    private readonly HashSet<InboxConversation> _avatarVisibleRows = [];
    private readonly Dictionary<object, InboxConversation> _avatarRowLeases = new(ReferenceEqualityComparer.Instance);
    private ChatProfileImageCache? _avatarCache;
    private Task<BridgeSettings>? _avatarSettingsRead;
    private DateTimeOffset _avatarSettingsReadAt;

    private sealed record AvatarIdentity(string Provider, string? Origin, string User, string CredentialHash, bool Direct);

    private sealed class AvatarLoad(CancellationTokenSource cancellation, BridgeSettings settings, SessionKey key)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public BridgeSettings Settings { get; } = settings;
        public SessionKey Key { get; } = key;
        public HashSet<InboxConversation> Consumers { get; } = [];
        public bool IsSelected { get; set; }
        public bool Started { get; set; }
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private int _activeAvatarLoads;

    // The inbox virtualizes its rows. Keep the set of realized rows current so queued
    // image fetches can be abandoned when a row scrolls away.
    public void TrackAvatarLoaded(InboxConversation conversation, object rowLease)
    {
        lock (_avatarSync)
        {
            if (_avatarRowLeases.TryGetValue(rowLease, out var previous) && !ReferenceEquals(previous, conversation))
                ReleaseAvatarRowLeaseUnderLock(rowLease, previous);
            _avatarRowLeases[rowLease] = conversation;
            _avatarVisibleRows.Add(conversation);
        }
        _ = EnsureAvatarAsync(conversation);
    }

    public void TrackAvatarUnloaded(object rowLease)
    {
        lock (_avatarSync)
        {
            if (_avatarRowLeases.TryGetValue(rowLease, out var conversation))
                ReleaseAvatarRowLeaseUnderLock(rowLease, conversation);
        }
        StartQueuedAvatarLoads();
    }

    private void ReleaseAvatarRowLeaseUnderLock(object rowLease, InboxConversation conversation)
    {
        _avatarRowLeases.Remove(rowLease);
        if (!_avatarRowLeases.Values.Contains(conversation)) _avatarVisibleRows.Remove(conversation);
        foreach (var load in _avatarLoads.Values)
        {
            if (!_avatarVisibleRows.Contains(conversation) && !ReferenceEquals(SelectedConversation, conversation))
                load.Consumers.Remove(conversation);
            if (!load.Started && load.Consumers.Count == 0 &&
                !_avatarVisibleRows.Any(row => HasSameAvatarOwner(row, load.Key)) &&
                !HasSameAvatarOwner(SelectedConversation, load.Key))
            {
                load.Cancellation.Cancel();
                load.Completion.TrySetResult(null);
            }
        }
    }

    private void ReconcileAvatarSelectionUnderLock()
    {
        foreach (var load in _avatarLoads.Values)
        {
            load.IsSelected = HasSameAvatarOwner(SelectedConversation, load.Key);
            foreach (var conversation in load.Consumers.ToArray())
                if (!_avatarVisibleRows.Contains(conversation) && !ReferenceEquals(SelectedConversation, conversation))
                    load.Consumers.Remove(conversation);
            if (!load.Started && load.Consumers.Count == 0 &&
                !_avatarVisibleRows.Any(row => HasSameAvatarOwner(row, load.Key)) &&
                !HasSameAvatarOwner(SelectedConversation, load.Key))
            {
                load.Cancellation.Cancel();
                load.Completion.TrySetResult(null);
            }
        }
    }

    // Only realized rows and the selected conversation request optional images.
    // Images never participate in request authorization.
    public async Task EnsureAvatarAsync(InboxConversation? conversation)
    {
        if (_closing) return;
        lock (_avatarSync) ReconcileAvatarSelectionUnderLock();
        StartQueuedAvatarLoads();
        if (conversation?.Snapshot is not { Key.UserId: { Length: > 0 } } snapshot ||
            !Conversations.Contains(conversation)) return;
        var token = _avatarLifetime.Token;
        try
        {
            var settings = await ReadAvatarSettingsAsync(token);
            var credential = snapshot.Key.Provider switch
            {
                "mattermost" => settings.MattermostToken,
                "slack" => settings.SlackBotToken,
                "discord" => settings.DiscordBotToken,
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(credential)) return;
            var identity = new AvatarIdentity(snapshot.Key.Provider, snapshot.Key.Origin, snapshot.Key.UserId!,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))), settings.MattermostBypassProxy);
            string? path = null;
            AvatarLoad? pendingLoad = null;
            lock (_avatarSync)
            {
                if (!_avatarVisibleRows.Contains(conversation) && !ReferenceEquals(SelectedConversation, conversation)) return;
                if (!_avatarLoads.TryGetValue(identity, out var load))
                {
                    load = new AvatarLoad(CancellationTokenSource.CreateLinkedTokenSource(token), settings, snapshot.Key);
                    _avatarLoads.Add(identity, load);
                }
                load.Consumers.Add(conversation);
                load.IsSelected |= ReferenceEquals(SelectedConversation, conversation);
                pendingLoad = load;
            }
            if (pendingLoad is not null)
            {
                path = await WaitForAvatarLoadAsync(identity, pendingLoad, conversation);
                if (pendingLoad.Cancellation.IsCancellationRequested) return;
            }
            if (!_closing && Conversations.Contains(conversation) && conversation.Snapshot?.Key == snapshot.Key)
                conversation.AvatarCachePath = path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // A missing image leaves the cached name/initials usable.
        }
    }

    private Task<BridgeSettings> ReadAvatarSettingsAsync(CancellationToken token)
    {
        if (_avatarSettingsRead is null || _avatarSettingsRead.IsFaulted || _avatarSettingsRead.IsCanceled ||
            DateTimeOffset.UtcNow - _avatarSettingsReadAt > TimeSpan.FromSeconds(5))
        {
            _avatarSettingsReadAt = DateTimeOffset.UtcNow;
            _avatarSettingsRead = _store.LoadAsync(token);
        }
        return _avatarSettingsRead;
    }

    private static bool HasSameAvatarOwner(InboxConversation? conversation, SessionKey key) =>
        conversation?.Snapshot?.Key is { } other && other.Provider == key.Provider &&
        other.Origin == key.Origin && other.UserId == key.UserId;

    private async Task<string?> WaitForAvatarLoadAsync(AvatarIdentity identity, AvatarLoad load, InboxConversation conversation)
    {
        StartQueuedAvatarLoads();
        try
        {
            return await load.Completion.Task;
        }
        finally
        {
            lock (_avatarSync)
            {
                if (!_avatarVisibleRows.Contains(conversation) && !ReferenceEquals(SelectedConversation, conversation))
                    load.Consumers.Remove(conversation);
            }
        }
    }

    private void StartQueuedAvatarLoads()
    {
        List<(AvatarIdentity Identity, AvatarLoad Load)>? start = null;
        lock (_avatarSync)
        {
            if (_closing) return;
            foreach (var pair in _avatarLoads
                .Where(pair => !pair.Value.Started && !pair.Value.Cancellation.IsCancellationRequested && pair.Value.Consumers.Count > 0)
                .OrderByDescending(pair => pair.Value.IsSelected))
            {
                if (_activeAvatarLoads >= 2) break;
                pair.Value.Started = true;
                _activeAvatarLoads++;
                (start ??= []).Add((pair.Key, pair.Value));
            }
            foreach (var pair in _avatarLoads.Where(pair => pair.Value.Cancellation.IsCancellationRequested && !pair.Value.Started).ToArray())
            {
                _avatarLoads.Remove(pair.Key);
                pair.Value.Completion.TrySetResult(null);
                pair.Value.Cancellation.Dispose();
            }
        }
        if (start is null) return;
        foreach (var item in start) _ = RunAvatarLoadAsync(item.Identity, item.Load);
    }

    private async Task RunAvatarLoadAsync(AvatarIdentity identity, AvatarLoad load)
    {
        try
        {
            if (_closing || load.Cancellation.IsCancellationRequested) { load.Completion.TrySetResult(null); return; }
            lock (_avatarSync) _avatarCache ??= new ChatProfileImageCache(_store.DirectoryPath);
            // ResolveAsync performs its first path safety checks synchronously before its
            // first await. Start it on a worker so the initial filesystem walk cannot block XAML.
            var path = await System.Threading.Tasks.Task.Run(
                () => _avatarCache.ResolveAsync(load.Settings, load.Key, load.Cancellation.Token), load.Cancellation.Token);
            load.Completion.TrySetResult(path);
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested) { load.Completion.TrySetResult(null); }
        catch (Exception) { load.Completion.TrySetResult(null); }
        finally
        {
            lock (_avatarSync)
            {
                _activeAvatarLoads--;
                if (_avatarLoads.TryGetValue(identity, out var current) && ReferenceEquals(current, load))
                    _avatarLoads.Remove(identity);
                load.Cancellation.Dispose();
            }
            StartQueuedAvatarLoads();
        }
    }

    private void CloseAvatars()
    {
        _avatarLifetime.Cancel();
        lock (_avatarSync)
        {
            foreach (var pair in _avatarLoads.ToArray())
            {
                pair.Value.Cancellation.Cancel();
                if (pair.Value.Started) continue;
                pair.Value.Completion.TrySetResult(null);
                pair.Value.Cancellation.Dispose();
                _avatarLoads.Remove(pair.Key);
            }
        }
        _avatarCache?.Dispose();
    }
}
