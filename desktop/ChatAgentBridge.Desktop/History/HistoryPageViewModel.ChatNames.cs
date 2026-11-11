using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class HistoryPageViewModel
{
    private IReadOnlyDictionary<SessionKey, ChatIdentityLabels> _chatNames = new Dictionary<SessionKey, ChatIdentityLabels>();
    private IReadOnlyList<SessionBinding> _nameBindings = [];
    private readonly Dictionary<Guid, ExecutionChatInfo> _executionChats = [];
    private readonly Dictionary<Guid, (string Provider, string Channel, string Thread)> _executionChatScopes = [];
    private readonly Dictionary<(string Provider, string Channel, string Thread), string> _auditChatLabels = [];
    private IReadOnlyDictionary<(string Provider, string UserId), string?> _allowedUserNames =
        new Dictionary<(string, string), string?>();
    private CancellationTokenSource? _chatNameCancellation;
    private int _chatNameGeneration;
    private bool _chatNameUpdatesEnabled = true;
    private Task _chatNameRefreshTask = Task.CompletedTask;

    private async Task LoadCachedChatNamesAsync(IReadOnlyList<SessionBinding> bindings)
    {
        CancelChatNameRefresh();
        _nameBindings = bindings;
        using var directory = new ChatIdentityDirectory(_settingsStore.DirectoryPath);
        _chatNames = await directory.ReadCachedAsync(bindings.Select(binding => binding.Key));
        try
        {
            var users = await new ChatAgentBridge.Core.AllowedUserDirectory(_settingsStore.DirectoryPath).LoadAsync();
            _allowedUserNames = users.GroupBy(user => (user.Provider, user.UserId))
                .ToDictionary(group => group.Key, group => group.OrderByDescending(user => user.AddedAt).First().DisplayName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Optional display names must not prevent offline history from loading.
            _allowedUserNames = new Dictionary<(string, string), string?>();
        }
    }

    public void SetChatNameUpdatesEnabled(bool enabled)
    {
        _chatNameUpdatesEnabled = enabled;
        if (!enabled) CancelChatNameRefresh();
    }

    internal async Task PauseChatNameUpdatesAsync()
    {
        SetChatNameUpdatesEnabled(false);
        // Drain the final cache write before data maintenance removes or restores files.
        await _chatNameRefreshTask;
    }

    private void CancelChatNameRefresh()
    {
        _chatNameGeneration++;
        _chatNameCancellation?.Cancel();
        _chatNameCancellation?.Dispose();
        _chatNameCancellation = null;
    }

    private void StartChatNameRefresh()
    {
        CancelChatNameRefresh();
        if (!_chatNameUpdatesEnabled || _nameBindings.Count == 0) return;
        var cancellation = _chatNameCancellation = new CancellationTokenSource();
        var generation = _chatNameGeneration;
        var keys = _nameBindings.Select(binding => binding.Key).ToArray();
        _chatNameRefreshTask = RefreshChatNamesAfterAsync(_chatNameRefreshTask, keys, generation, cancellation.Token);
    }

    private async Task RefreshChatNamesAfterAsync(Task previous, SessionKey[] keys, int generation, CancellationToken cancellationToken)
    {
        await previous;
        await RefreshChatNamesAsync(keys, generation, cancellationToken);
    }

    private async Task RefreshChatNamesAsync(SessionKey[] keys, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken);
            using var directory = new ChatIdentityDirectory(_settingsStore.DirectoryPath);
            var names = await directory.ResolveAsync(settings, keys, cancellationToken);
            if (cancellationToken.IsCancellationRequested || generation != _chatNameGeneration || IsBusy) return;
            if (keys.All(key => Equals(_chatNames.GetValueOrDefault(key), names.GetValueOrDefault(key)))) return;
            _chatNames = names;
            RebuildChatNameIndexes();
            // Replace immutable rows so compiled one-time item bindings see the new labels.
            // Execution settings and session keys are never changed by display-name resolution.
            var selectedKey = SelectedSession?.Binding.Key;
            for (var i = 0; i < Sessions.Count; i++)
                Sessions[i] = Sessions[i] with { ChatLabels = names.GetValueOrDefault(Sessions[i].Binding.Key) };
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                // Labels do not affect the service/folder filters or sort keys. Update visible
                // rows in place so a background lookup does not reset the list's scroll position.
                var replacements = Sessions.ToDictionary(row => row.Binding.Key);
                for (var i = 0; i < FilteredSessions.Count; i++)
                    FilteredSessions[i] = replacements[FilteredSessions[i].Binding.Key];
                SelectedSession = selectedKey is null ? null : FilteredSessions.FirstOrDefault(row => row.Binding.Key == selectedKey);
            }
            else
            {
                SelectedSession = selectedKey is null ? null : Sessions.FirstOrDefault(row => row.Binding.Key == selectedKey);
                ApplySessionFilter();
            }
            ApplyRunFilters();
            RefreshSessionExecutions();
            for (var i = 0; i < AuditEvents.Count; i++)
                AuditEvents[i] = AuditEvents[i] with { ChatLabel = AuditChatLabel(AuditEvents[i].Record),
                    UserName = AccessUserName(AuditEvents[i].Record) };
            ApplyAuditFilters();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Optional display metadata must never prevent offline history/approval operations.
            // Keep cached labels, and do not surface API responses or credentials in the UI.
        }
    }

    private void RebuildChatNameIndexes()
    {
        _executionChats.Clear();
        _executionChatScopes.Clear();
        _auditChatLabels.Clear();
        // Old execution records have no origin. Only join when the complete identity has one origin.
        var sessions = _nameBindings.Where(binding => !string.IsNullOrEmpty(binding.Key.Origin) &&
                !string.IsNullOrEmpty(binding.Key.UserId))
            .GroupBy(binding => (binding.Key.Provider, binding.Key.ChannelId, binding.Key.ThreadId,
                binding.Key.UserId, binding.SessionId))
            .Where(group => group.Select(binding => binding.Key.Origin).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var execution in _activityRecords)
        {
            if (execution.SessionId is null || !sessions.TryGetValue((execution.Source, execution.ChannelId,
                    execution.ThreadId, execution.UserId, execution.SessionId), out var binding)) continue;
            var row = new SessionHistoryRow(binding, ChatLabels: _chatNames.GetValueOrDefault(binding.Key));
            _executionChats[execution.Id] = new ExecutionChatInfo($"{row.ListTitle} · {row.ListLocation}", row.UserLabel, binding.Key);
            _executionChatScopes[execution.Id] = (execution.Source, execution.ChannelId, execution.ThreadId);
        }
        foreach (var group in _nameBindings.GroupBy(binding => (binding.Key.Provider, binding.Key.ChannelId, binding.Key.ThreadId)))
        {
            if (group.Select(binding => binding.Key.Origin).Distinct().Count() != 1) continue;
            var rows = group.Select(binding => new SessionHistoryRow(binding, ChatLabels: _chatNames.GetValueOrDefault(binding.Key)))
                .Select(row => $"{row.SiteLabel} · {row.ListLocation}").Distinct().ToArray();
            if (rows.Length == 1) _auditChatLabels[group.Key] = rows[0];
        }
    }

    private ExecutionChatInfo? ExecutionChat(ExecutionRecord record) => _executionChats.GetValueOrDefault(record.Id);

    private string? AccessUserName(AuditEvent record) => record.Access is { } access
        ? _allowedUserNames.GetValueOrDefault((record.Source, access.UserId)) : null;

    private string? AuditChatLabel(AuditEvent record) =>
        record.ExecutionId is { } id && _executionChatScopes.GetValueOrDefault(id) == (record.Source, record.ChannelId, record.ThreadId) &&
        _executionChats.TryGetValue(id, out var chat) ? $"{chat.Conversation} · {chat.User}" :
        _auditChatLabels.GetValueOrDefault((record.Source, record.ChannelId, record.ThreadId));
}
