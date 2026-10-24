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
    private readonly SemaphoreSlim _avatarSlots = new(2, 2);
    private readonly Dictionary<AvatarIdentity, Task<string?>> _avatarLoads = [];
    private readonly Dictionary<AvatarIdentity, (DateTimeOffset CheckedAt, string? Path)> _avatarResults = [];
    private ChatProfileImageCache? _avatarCache;
    private Task<BridgeSettings>? _avatarSettingsRead;
    private DateTimeOffset _avatarSettingsReadAt;

    private sealed record AvatarIdentity(string Provider, string? Origin, string User, string CredentialHash, bool Direct);

    // Only realized rows and the selected conversation request optional images.
    // Images never participate in request authorization.
    public async Task EnsureAvatarAsync(InboxConversation? conversation)
    {
        if (_closing || conversation?.Snapshot is not { Key.UserId: { Length: > 0 } } snapshot ||
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
            string? path;
            if (_avatarResults.TryGetValue(identity, out var cached) &&
                DateTimeOffset.UtcNow - cached.CheckedAt < (cached.Path is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24)) &&
                (cached.Path is null || File.Exists(cached.Path))) path = cached.Path;
            else
            {
                if (!_avatarLoads.TryGetValue(identity, out var load))
                {
                    load = LoadAvatarAsync(settings, snapshot.Key, token);
                    _avatarLoads.Add(identity, load);
                }
                try { path = await load; }
                finally { _avatarLoads.Remove(identity); }
                _avatarResults[identity] = (DateTimeOffset.UtcNow, path);
                if (_avatarResults.Count > 128)
                    _avatarResults.Remove(_avatarResults.MinBy(pair => pair.Value.CheckedAt).Key);
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

    private async Task<string?> LoadAvatarAsync(BridgeSettings settings, SessionKey key, CancellationToken token)
    {
        await _avatarSlots.WaitAsync(token);
        try
        {
            if (_closing) return null;
            _avatarCache ??= new ChatProfileImageCache(_store.DirectoryPath);
            return await _avatarCache.ResolveAsync(settings, key, token);
        }
        finally { _avatarSlots.Release(); }
    }

    private void CloseAvatars()
    {
        _avatarLifetime.Cancel();
        _avatarCache?.Dispose();
    }
}
