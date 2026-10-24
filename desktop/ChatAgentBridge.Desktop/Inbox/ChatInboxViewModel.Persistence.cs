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
    private InboxConversationSnapshot[]? _lastSavedConversationSnapshots;
    private CancellationTokenSource? _conversationSaveCooldown;
    private (long Ticks, long Length)? _conversationNamesStamp;
    private IReadOnlyDictionary<SessionKey, ChatIdentityLabels> _cachedConversationNames = new Dictionary<SessionKey, ChatIdentityLabels>();
    private HashSet<SessionKey> _cachedConversationNameKeys = [];
    private int _lastReportedRejectedConversationCount;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasConversationPersistenceWarning))]
    public partial string ConversationPersistenceWarning { get; set; } = "";
    public bool HasConversationPersistenceWarning => ConversationPersistenceWarning.Length > 0;
    public bool HasSelectedRequest => Selected is not null && SelectedConversation?.Requests.Contains(Selected) == true;
    public bool HasSelectedRestoredConversation => SelectedConversation is { Requests.Count: 0 };
    public string RestoredConversationDescription => DesktopText.Get("InboxRestoredDescription");

    private static bool IsValidInboxWorkspacePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 2048 && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    private void ReportInvalidWorkspaceSettings()
    {
        Volatile.Write(ref _accessSettings, null);
        RequestExecutionCancellation();
        foreach (var item in Requests.Where(item => item.State == "承認待ち"))
            item.State = "許可設定を確認できず実行不可";
        HasConnectionError = true;
        Status = DesktopText.Get("InboxWorkspaceInvalid");
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

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
            var retained = snapshots.Values.OrderByDescending(item => item.UpdatedAt).Take(500).ToArray();
            var names = await ReadCachedConversationNamesAsync(retained.Select(item => item.Key));
            if (_closing) return;
            foreach (var item in retained)
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
        key.Provider is "slack" or "mattermost" or "discord" && !string.IsNullOrWhiteSpace(key.UserId) &&
        (key.Provider is "slack" or "discord" ? key.Origin == key.Provider : key.Origin is { Length: 64 } origin &&
            origin.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static string? SafeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= 256 && !value.Any(char.IsControl) ? value :
            new string(value.Where(c => !char.IsControl(c)).Take(256).ToArray());
    }

    private static InboxConversationSnapshot WithCachedNames(InboxConversationSnapshot snapshot, ChatIdentityLabels? labels)
    {
        if (labels is null) return snapshot;
        var user = SafeLabel(labels.UserName) ?? snapshot.UserName;
        var channel = SafeLabel(labels.ChannelName) ?? snapshot.ChannelName;
        var site = SafeLabel(labels.SiteName) ?? snapshot.SiteName;
        var direct = labels.ChannelName is null ? snapshot.IsDirectMessage : labels.IsDirectMessage;
        return user == snapshot.UserName && channel == snapshot.ChannelName && site == snapshot.SiteName && direct == snapshot.IsDirectMessage
            ? snapshot : snapshot with { UserName = user, ChannelName = channel, SiteName = site, IsDirectMessage = direct };
    }

    private async Task<IReadOnlyDictionary<SessionKey, ChatIdentityLabels>> ReadCachedConversationNamesAsync(IEnumerable<SessionKey> keys)
    {
        var requested = keys.ToHashSet();
        try
        {
            var info = new FileInfo(Path.Combine(_store.DirectoryPath, "chat-identities.json"));
            var stamp = info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (-1L, -1L);
            if (_conversationNamesStamp == stamp && requested.IsSubsetOf(_cachedConversationNameKeys))
                return _cachedConversationNames;
            // Capture the stamp before reading: an intervening cache replacement is read next time.
            _conversationNamesStamp = stamp;
            _cachedConversationNameKeys = requested;
            using var directory = new ChatIdentityDirectory(_store.DirectoryPath);
            _cachedConversationNames = await directory.ReadCachedAsync(requested);
        }
        catch (Exception)
        {
            // Cache errors are optional. Retry after a file change or a new identity, not on every state change.
            _cachedConversationNames = _cachedConversationNames.Where(pair => requested.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        return _cachedConversationNames;
    }

    private void ApplyConversationLabels(InboxConversation conversation)
    {
        if (conversation.Snapshot is not { } snapshot) return;
        var provider = snapshot.Key.Provider switch { "mattermost" => "Mattermost", "discord" => "Discord", _ => "Slack" };
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
                var selection = InboxConversationStore.SelectValidSnapshots(snapshots);
                if (selection.RejectedCount != _lastReportedRejectedConversationCount)
                {
                    _lastReportedRejectedConversationCount = selection.RejectedCount;
                    if (selection.RejectedCount > 0)
                        System.Diagnostics.Trace.TraceWarning("Conversation metadata save rejected {0} rows.", selection.RejectedCount);
                }
                if (selection.RejectedCount > 0 && selection.Entries.Count == 0)
                {
                    ConversationPersistenceWarning = DesktopText.Get("InboxPersistenceAllRowsRejected", selection.RejectedCount);
                    continue; // Never replace a previous good file with an empty rejected batch.
                }
                snapshots = selection.Entries.ToArray();
                var validKeys = snapshots.Select(item => item.Key).ToHashSet();
                var names = await ReadCachedConversationNamesAsync(snapshots.Select(item => item.Key));
                foreach (var conversation in Conversations)
                {
                    if (conversation.Snapshot is not { } snapshot || !validKeys.Contains(snapshot.Key)) continue;
                    var named = WithCachedNames(snapshot, names.GetValueOrDefault(snapshot.Key));
                    if (named == snapshot) continue;
                    conversation.Snapshot = named;
                    ApplyConversationLabels(conversation);
                    conversation.RefreshPresentation();
                }
                snapshots = snapshots.Select(item => WithCachedNames(item, names.GetValueOrDefault(item.Key))).ToArray();
                if (_lastSavedConversationSnapshots is null || !snapshots.SequenceEqual(_lastSavedConversationSnapshots))
                {
                    await Task.Run(() => new InboxConversationStore(_store.DirectoryPath).SaveAsync(snapshots));
                    _lastSavedConversationSnapshots = snapshots;
                }
                ConversationPersistenceWarning = selection.RejectedCount == 0 ? "" :
                    DesktopText.Get("InboxPersistenceSomeRowsRejected", selection.RejectedCount);
            }
            catch (Exception) { ConversationPersistenceWarning = DesktopText.Get("InboxPersistenceSaveFailed"); }
            // The first snapshot is written immediately. Briefly collect subsequent state changes,
            // keeping only the newest snapshot; shutdown/maintenance bypass this cooldown and drain it.
            if (!_closing && !_conversationPersistencePaused)
            {
                using var cooldown = new CancellationTokenSource();
                _conversationSaveCooldown = cooldown;
                try { await Task.Delay(150, cooldown.Token); }
                catch (OperationCanceledException) { }
                finally { _conversationSaveCooldown = null; }
            }
        }
    }

    internal async Task PauseConversationPersistenceAsync()
    {
        await EnsureConversationsRestoredAsync();
        _conversationPersistencePaused = true;
        _conversationSaveCooldown?.Cancel();
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
        _lastSavedConversationSnapshots = null;
        _lastReportedRejectedConversationCount = 0;
        _conversationNamesStamp = null;
        _cachedConversationNameKeys.Clear();
        _cachedConversationNames = new Dictionary<SessionKey, ChatIdentityLabels>();
        _conversationRestoreTask = null;
        ConversationPersistenceWarning = "";
        await EnsureConversationsRestoredAsync();
    }

    internal void ResumeConversationPersistence() => _conversationPersistencePaused = false;

    // Call after execution and connection tasks have settled so their terminal-state writes are included.
    internal Task FlushConversationPersistenceAsync()
    {
        _conversationSaveCooldown?.Cancel();
        return _conversationSaveTask;
    }
}
