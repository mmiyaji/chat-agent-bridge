using System.Text.Json;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private Task? _conversationRestoreTask;
    private Task _conversationSaveTask = Task.CompletedTask;
    private InboxConversationSnapshot[]? _pendingConversationSnapshots;
    private bool _conversationPersistenceReady;
    private bool _conversationPersistencePaused;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasConversationPersistenceWarning))]
    public partial string ConversationPersistenceWarning { get; set; } = "";
    public bool HasConversationPersistenceWarning => ConversationPersistenceWarning.Length > 0;
    public bool HasSelectedRequest => Selected is not null && SelectedConversation?.Requests.Contains(Selected) == true;
    public bool HasSelectedRestoredConversation => SelectedConversation is { Requests.Count: 0 };
    public string RestoredConversationDescription => DesktopText.Get("InboxRestoredDescription");

    private void NotifyRestoredSelection()
    {
        OnPropertyChanged(nameof(HasSelectedRequest));
        OnPropertyChanged(nameof(HasSelectedRestoredConversation));
    }

    // Metadata rows never contain InboxItems, transports, approval tokens, or executable requests.
    public Task EnsureConversationsRestoredAsync() => _conversationRestoreTask ??= RestoreConversationsAsync();

    private async Task RestoreConversationsAsync()
    {
        await Task.Yield();
        try
        {
            var store = new InboxConversationStore(_store.DirectoryPath);
            var snapshots = (await store.ListAsync()).ToDictionary(item => item.Key);
            // Upgrade existing owner-scoped session bindings without restoring request content.
            IReadOnlyList<SessionBinding> bindings = [];
            try { bindings = await _sessions.ListAsync(); }
            catch (Exception) { /* Legacy binding migration is optional; valid metadata still restores. */ }
            foreach (var binding in bindings)
            {
                if (snapshots.TryGetValue(binding.Key, out var existing))
                {
                    snapshots[binding.Key] = existing with { DisplayName = SafeLabel(binding.DisplayName) };
                    continue;
                }
                if (!HasRestorableScope(binding.Key) || binding.UpdatedAt == default ||
                    binding.WorkspaceRoot.Length > 2048 || binding.WorkspaceRoot.Any(char.IsControl) ||
                    !Path.IsPathFullyQualified(binding.WorkspaceRoot)) continue;
                snapshots[binding.Key] = new(binding.Key, binding.WorkspaceRoot, binding.UpdatedAt,
                    "idle", 0, SafeLabel(binding.DisplayName));
            }
            IReadOnlyDictionary<SessionKey, ChatIdentityLabels> names = new Dictionary<SessionKey, ChatIdentityLabels>();
            try
            {
                using var directory = new ChatIdentityDirectory(_store.DirectoryPath);
                names = await directory.ReadCachedAsync(snapshots.Keys);
            }
            catch (Exception) { /* Restore metadata even when the optional name cache is unavailable. */ }
            if (_closing) return;
            foreach (var item in snapshots.Values.OrderByDescending(item => item.UpdatedAt).Take(500))
            {
                var snapshot = WithCachedNames(item, names.GetValueOrDefault(item.Key));
                var conversation = new InboxConversation(JsonSerializer.Serialize(snapshot.Key))
                {
                    Snapshot = snapshot, PreviousRequestCount = snapshot.RequestCount,
                    RequestCount = snapshot.RequestCount
                };
                ApplyRestoredPresentation(conversation);
                Conversations.Add(conversation);
            }
            _conversationPersistenceReady = true;
            RefreshVisibleConversations();
        }
        catch (Exception)
        {
            // Do not silently replace an unreadable file with an empty list.
            ConversationPersistenceWarning = DesktopText.Get("InboxPersistenceLoadFailed");
        }
    }

    private static bool HasRestorableScope(SessionKey key) =>
        key.Provider is "slack" or "mattermost" && !string.IsNullOrWhiteSpace(key.UserId) &&
        (key.Provider == "slack" ? key.Origin == "slack" : key.Origin is { Length: 64 } origin &&
            origin.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static string? SafeLabel(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(256).ToArray());

    private static InboxConversationSnapshot WithCachedNames(InboxConversationSnapshot snapshot, ChatIdentityLabels? labels) =>
        labels is null ? snapshot : snapshot with
        {
            UserName = SafeLabel(labels.UserName) ?? snapshot.UserName,
            ChannelName = SafeLabel(labels.ChannelName) ?? snapshot.ChannelName,
            SiteName = SafeLabel(labels.SiteName) ?? snapshot.SiteName,
            IsDirectMessage = labels.IsDirectMessage
        };

    private void ApplyConversationLabels(InboxConversation conversation)
    {
        if (conversation.Snapshot is not { } snapshot) return;
        var provider = snapshot.Key.Provider == "mattermost" ? "Mattermost" : "Slack";
        conversation.Title = string.Join(" · ", new[] { snapshot.SiteName ?? provider, snapshot.UserName ?? snapshot.Key.UserId });
        conversation.Location = snapshot.ChannelName is { Length: > 0 } channel
            ? channel : DesktopText.Get("ConversationLocation", snapshot.Key.ChannelId, snapshot.Key.ThreadId);
        conversation.Identity = $"{conversation.Title} / {conversation.Location} / {snapshot.Key.ThreadId}";
    }

    private void ApplyRestoredPresentation(InboxConversation conversation)
    {
        if (conversation.Snapshot is not { } snapshot) return;
        ApplyConversationLabels(conversation);
        conversation.LatestPrompt = "";
        conversation.StateSummary = DesktopText.Get(snapshot.State switch
        {
            "completed" => "ApprovalCompleted",
            "cancelled" => "ApprovalCancelled",
            "failed" => "ApprovalExecutionError",
            "idle" => "InboxRestoredIdle",
            _ => "InboxInterrupted"
        });
        conversation.StateTone = snapshot.State switch
        {
            "completed" => "success", "cancelled" => "stopped", "failed" => "error",
            "idle" => "neutral", _ => "warning"
        };
        conversation.Activity = snapshot.UpdatedAt.ToLocalTime().ToString("g");
        conversation.RefreshPresentation();
    }

    private void UpdateConversationSnapshot(InboxConversation conversation, InboxItem latest, InboxItem stateItem)
    {
        var state = stateItem.State switch
        {
            "承認待ち" => "pending",
            "実行中" or "自動実行を準備" or "追加指示の受領待ち" => "running",
            "完了" or "フォルダー変更済み" or "エージェント変更済み" or "実行中へ追加" => "completed",
            "キャンセル" or "拒否" or "許可取り消し・実行不可" => "cancelled",
            _ when stateItem.StateTone == "error" => "failed",
            _ => "interrupted"
        };
        conversation.Snapshot = conversation.Snapshot is { } old
            ? old with { WorkspaceRoot = latest.Workspace, UpdatedAt = latest.ReceivedAt,
                State = state, RequestCount = conversation.RequestCount }
            : new(CreateSessionKey(latest), latest.Workspace, latest.ReceivedAt, state, conversation.RequestCount);
        ApplyConversationLabels(conversation);
    }

    private void QueueConversationSave()
    {
        if (!_conversationPersistenceReady || _conversationPersistencePaused) return;
        _pendingConversationSnapshots = Conversations.Select(item => item.Snapshot).OfType<InboxConversationSnapshot>()
            .OrderByDescending(item => item.UpdatedAt).Take(500).ToArray();
        if (_conversationSaveTask.IsCompleted) _conversationSaveTask = SaveConversationsAsync();
    }

    private async Task SaveConversationsAsync()
    {
        await Task.Yield();
        while (_pendingConversationSnapshots is { } snapshots)
        {
            _pendingConversationSnapshots = null;
            try
            {
                // Name lookups use the existing local cache only; persistence never requires a network connection.
                try
                {
                    using var directory = new ChatIdentityDirectory(_store.DirectoryPath);
                    var names = await directory.ReadCachedAsync(snapshots.Select(item => item.Key));
                    foreach (var conversation in Conversations)
                    {
                        if (conversation.Snapshot is not { } snapshot) continue;
                        conversation.Snapshot = WithCachedNames(snapshot, names.GetValueOrDefault(snapshot.Key));
                        ApplyConversationLabels(conversation);
                        conversation.RefreshPresentation();
                    }
                    snapshots = snapshots.Select(item => WithCachedNames(item, names.GetValueOrDefault(item.Key))).ToArray();
                }
                catch (Exception) { /* Optional display labels must not prevent saving the list. */ }
                await Task.Run(() => new InboxConversationStore(_store.DirectoryPath).SaveAsync(snapshots));
                ConversationPersistenceWarning = "";
            }
            catch (Exception) { ConversationPersistenceWarning = DesktopText.Get("InboxPersistenceSaveFailed"); }
        }
    }

    internal async Task PauseConversationPersistenceAsync()
    {
        await EnsureConversationsRestoredAsync();
        _conversationPersistencePaused = true;
        await _conversationSaveTask;
    }

    internal async Task ReloadConversationsAsync()
    {
        await PauseConversationPersistenceAsync();
        Requests.Clear();
        Conversations.Clear();
        SelectedConversation = null;
        Selected = null;
        _conversationPersistenceReady = false;
        _conversationRestoreTask = null;
        ConversationPersistenceWarning = "";
        await EnsureConversationsRestoredAsync();
    }

    internal void ResumeConversationPersistence() => _conversationPersistencePaused = false;

    // Call after execution and connection tasks have settled so their terminal-state writes are included.
    internal Task FlushConversationPersistenceAsync() => _conversationSaveTask;
}
