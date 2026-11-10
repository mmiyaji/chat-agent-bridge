using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Inbox;

internal enum AccessDecision { Allow, AllowOnce, Deny }

/// <summary>A request from a chat user who is not on the allowed-user list, waiting for a decision on this PC.</summary>
public sealed partial class AccessRequestItem : ObservableObject
{
    internal AccessRequestItem(ChatRequest request, IChatTransport transport, BridgeSettings connection,
        Func<AccessRequestItem, AccessDecision, Task> decide)
    {
        Request = request;
        Transport = transport;
        Connection = connection;
        ReceivedAt = DateTimeOffset.Now;
        CanRunOnce = request.IncomingFiles.Count == 0 && IsPlainRequest(request.Text);
        AllowCommand = new AsyncRelayCommand(() => decide(this, AccessDecision.Allow), () => !IsBusy);
        AllowOnceCommand = new AsyncRelayCommand(() => decide(this, AccessDecision.AllowOnce), () => !IsBusy && CanRunOnce);
        DenyCommand = new AsyncRelayCommand(() => decide(this, AccessDecision.Deny), () => !IsBusy);
    }

    internal ChatRequest Request { get; }
    internal IChatTransport Transport { get; }
    internal BridgeSettings Connection { get; }
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>A one-time run only covers a plain request: commands and attachments need a saved permission.</summary>
    public bool CanRunOnce { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))]
    public partial string? DisplayName { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Location))]
    public partial string? ChannelName { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public IAsyncRelayCommand AllowCommand { get; }
    public IAsyncRelayCommand AllowOnceCommand { get; }
    public IAsyncRelayCommand DenyCommand { get; }

    public string Title => DesktopText.Get("AccessRequestUser", DisplayName ?? Request.UserId, Request.UserId, ProviderLabel);
    public string Location => DesktopText.Get("AccessRequestLocation", ChannelName ?? Request.ChannelId, ReceivedAt.ToString("t"));
    public string Preview => Request.Text.Length > 200 ? Request.Text[..200] + "…" : Request.Text;

    private string ProviderLabel => Request.Provider switch
    {
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        "discord" => "Discord",
        _ => Request.Provider
    };

    partial void OnIsBusyChanged(bool value)
    {
        AllowCommand.NotifyCanExecuteChanged();
        AllowOnceCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
    }

    private static bool IsPlainRequest(string text) =>
        ChatControlCommand.Parse(text).Kind == ChatControlCommandKind.None &&
        !AgentChatCommand.TryParse(text, out _) && !WorkspaceChatCommand.IsWorkspaceCommand(text);
}

public sealed partial class ChatInboxViewModel
{
    private const int MaxPendingAccessRequests = 20;
    private const int MaxOneTimeGrants = 200;
    private static readonly TimeSpan DeniedAccessCooldown = TimeSpan.FromMinutes(10);
    // Grants are keyed by the exact message, so a one-time run never admits later posts from the same user.
    private static readonly ConcurrentDictionary<(string Provider, string UserId, string MessageId), byte> OneTimeAccess = new();
    private readonly Dictionary<(string Provider, string? Origin, string UserId), DateTimeOffset> _deniedAccess = [];

    public ObservableCollection<AccessRequestItem> AccessRequests { get; } = [];
    public bool HasAccessRequests => AccessRequests.Count > 0;

    /// <summary>Raised on the UI thread when a new user asks for access, so the page can show a Windows notification.</summary>
    public event Action<AccessRequestItem>? AccessRequestReceived;

    /// <summary>Raised after a chat user was added to the allowed-user list, with the saved settings.</summary>
    public event Action<BridgeSettings>? AllowedUsersSaved;

    private static bool HasOneTimeAccess(BridgeSettings current, ChatRequest request) =>
        OneTimeAccess.ContainsKey((request.Provider, request.UserId, request.MessageId)) &&
        ChatAccessPolicy.IsAllowedScope(current, request.Provider, request.ChannelId, request.ThreadId, request.GuildId, request.IsDirectMessage);

    private static (string Provider, string? Origin, string UserId) AccessIdentity(ChatRequest request, BridgeSettings connection) =>
        (request.Provider, request.Provider == "mattermost" ? connection.MattermostServerUrl : request.GuildId, request.UserId);

    private async Task QueueAccessRequestAsync(IChatTransport transport, BridgeSettings current, BridgeSettings connection,
        ChatRequest request, CancellationToken token)
    {
        if (_disconnecting || _closing || !ReferenceEquals(transport, _transport)) return;
        if (!ChatAccessPolicy.CanApproveAccessRequest(current, connection, request.Provider, request.ChannelId,
            request.ThreadId, request.GuildId, request.IsDirectMessage)) return;
        var completion = new TaskCompletionSource<AccessRequestItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(() => completion.TrySetResult(AddAccessRequest(request, transport, connection, current))))
            return;
        if (await completion.Task is null) return;
        try { await AppendAccessAuditAsync("access_requested", request, null, actor: "", token); }
        catch (Exception) { /* The request stays visible; the audit store reports its own failures on later writes. */ }
        await ReplyQuietlyAsync(transport, request, DesktopText.Get("AccessRequestPendingReply"));
    }

    private AccessRequestItem? AddAccessRequest(ChatRequest request, IChatTransport transport, BridgeSettings connection, BridgeSettings current)
    {
        if (_closing || _disconnecting || !ReferenceEquals(transport, _transport)) return null;
        var identity = AccessIdentity(request, connection);
        // One pending request per person; a refused person is not asked about again for a while.
        if (AccessRequests.Any(item => AccessIdentity(item.Request, item.Connection) == identity)) return null;
        if (_deniedAccess.TryGetValue(identity, out var until) && until > DateTimeOffset.UtcNow) return null;
        if (AccessRequests.Count >= MaxPendingAccessRequests) return null;
        var item = new AccessRequestItem(request, transport, connection, DecideAccessAsync);
        AccessRequests.Add(item);
        OnPropertyChanged(nameof(HasAccessRequests));
        Status = DesktopText.Get("AccessRequestReceivedStatus", item.Title);
        _nameLookupTask = ResolveAccessRequestNamesAfterAsync(_nameLookupTask, item, current, _nameLookupCancellation.Token);
        AccessRequestReceived?.Invoke(item);
        return item;
    }

    private async Task ResolveAccessRequestNamesAfterAsync(Task previous, AccessRequestItem item, BridgeSettings settings,
        CancellationToken cancellationToken)
    {
        await previous;
        var key = ChatSessionScope.Create(item.Request.Provider, item.Request.ChannelId, item.Request.ThreadId,
            item.Request.UserId, item.Request.Provider == "mattermost" ? item.Connection.MattermostServerUrl : null);
        ChatIdentityLabels? labels = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var directory = new ChatIdentityDirectory(_store.DirectoryPath);
            var resolved = await directory.ResolveAsync(settings, [key], timeout.Token);
            resolved.TryGetValue(key, out labels);
        }
        catch (Exception)
        {
            // Names are optional. The request remains identified by its immutable user ID.
            return;
        }
        if (labels is null || cancellationToken.IsCancellationRequested) return;
        App.DispatcherQueue.TryEnqueue(() =>
        {
            item.DisplayName = labels.UserName ?? item.DisplayName;
            item.ChannelName = labels.ChannelName ?? item.ChannelName;
        });
    }

    /// <summary>Runs on the UI thread from the access request card or its Windows notification.</summary>
    internal async Task DecideAccessAsync(AccessRequestItem item, AccessDecision decision)
    {
        if (item.IsBusy || !AccessRequests.Contains(item)) return;
        item.IsBusy = true;
        var token = _connection?.Token ?? CancellationToken.None;
        var actor = Environment.UserName;
        try
        {
            switch (decision)
            {
                case AccessDecision.Deny:
                    _deniedAccess[AccessIdentity(item.Request, item.Connection)] = DateTimeOffset.UtcNow + DeniedAccessCooldown;
                    RemoveAccessRequest(item);
                    await AppendAccessAuditAsync("access_denied", item.Request, item.DisplayName, actor, token);
                    Status = DesktopText.Get("AccessRequestDeniedStatus", item.Title);
                    return;
                case AccessDecision.Allow:
                {
                    var request = item.Request;
                    var updated = !_disconnecting && !_closing && ReferenceEquals(item.Transport, _transport)
                        ? await _store.TryAddAllowedUserAsync(item.Connection, request.Provider, request.UserId,
                            request.ChannelId, request.ThreadId, request.GuildId, token, request.IsDirectMessage)
                        : null;
                    if (updated is null)
                    {
                        RemoveAccessRequest(item);
                        Status = DesktopText.Get("AccessRequestNotDispatchedStatus");
                        return;
                    }
                    RemoveAccessRequest(item);
                    await AppendAccessAuditAsync("access_allowed", item.Request, item.DisplayName, actor, token);
                    try
                    {
                        await new AllowedUserDirectory(_store.DirectoryPath).RecordAddedAsync(new AllowedUserRecord(
                            item.Request.Provider, item.Request.UserId, item.DisplayName, DateTimeOffset.UtcNow,
                            AllowedUserDirectory.ChatRequestMethod, actor, AccessIdentity(item.Request, item.Connection).Origin,
                            item.Request.ChannelId, item.Request.ThreadId), token);
                    }
                    catch (Exception)
                    {
                        // Names and times are display details; the permission itself is already saved.
                    }
                    AllowedUsersSaved?.Invoke(updated);
                    await RefreshAccessAsync();
                    Status = DesktopText.Get("AccessRequestAllowedStatus", item.Title);
                    await DispatchApprovedAccessRequestAsync(item, updated, oneTime: false, token);
                    return;
                }
                case AccessDecision.AllowOnce:
                {
                    if (!item.CanRunOnce) return;
                    var current = await _store.LoadAsync(token);
                    var request = item.Request;
                    if (_disconnecting || _closing || !ReferenceEquals(item.Transport, _transport) ||
                        !ChatAccessPolicy.CanApproveAccessRequest(current, item.Connection, request.Provider,
                            request.ChannelId, request.ThreadId, request.GuildId, request.IsDirectMessage))
                    {
                        RemoveAccessRequest(item);
                        Status = DesktopText.Get("AccessRequestNotDispatchedStatus");
                        return;
                    }
                    if (OneTimeAccess.Count >= MaxOneTimeGrants) OneTimeAccess.Clear();
                    OneTimeAccess.TryAdd((item.Request.Provider, item.Request.UserId, item.Request.MessageId), 0);
                    RemoveAccessRequest(item);
                    await AppendAccessAuditAsync("access_allowed_once", item.Request, item.DisplayName, actor, token);
                    Status = DesktopText.Get("AccessRequestAllowedOnceStatus", item.Title);
                    await DispatchApprovedAccessRequestAsync(item, current, oneTime: true, token);
                    return;
                }
            }
        }
        catch (Exception)
        {
            if (_closing) return;
            Status = DesktopText.Get("AccessRequestFailedStatus");
            item.IsBusy = false;
        }
    }

    private void RemoveAccessRequest(AccessRequestItem item)
    {
        AccessRequests.Remove(item);
        OnPropertyChanged(nameof(HasAccessRequests));
    }

    private void ClearAccessRequests()
    {
        if (AccessRequests.Count == 0) return;
        AccessRequests.Clear();
        OnPropertyChanged(nameof(HasAccessRequests));
    }

    private async Task DispatchApprovedAccessRequestAsync(AccessRequestItem item, BridgeSettings current, bool oneTime,
        CancellationToken token)
    {
        var request = item.Request with { IsAccessRequest = false };
        if (_disconnecting || _closing || !ReferenceEquals(item.Transport, _transport) || !CanReceive(current, request, item.Connection))
        {
            Status = DesktopText.Get("AccessRequestNotDispatchedStatus");
            return;
        }
        Volatile.Write(ref _accessSettings, current);
        await AuditAsync("received", request, null, token);
        if (!oneTime)
        {
            if (await _approvalBroker.TryHandleAsync(request, token)) return;
            if (await TryHandleChatAnswerAsync(item.Transport, current, request, token)) return;
            if (await TryHandleAgentCommandAsync(item.Transport, current, request, token)) return;
            if (await TryHandleChatControlAsync(item.Transport, current, request, token)) return;
        }
        await AcceptReceivedRequestAsync(request, item.Transport, current, token);
    }

    private readonly Dictionary<(string Provider, string UserId), DateTimeOffset> _lastRequestRecorded = [];

    /// <summary>Keeps the allowed-user list's "last request" current, writing at most once an hour per user.</summary>
    private void RecordAllowedUserRequest(ChatRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (request.Provider, request.UserId);
        if (_lastRequestRecorded.TryGetValue(key, out var last) && now - last < TimeSpan.FromHours(1)) return;
        _lastRequestRecorded[key] = now;
        var directory = _store.DirectoryPath;
        _ = Task.Run(async () =>
        {
            try { await new AllowedUserDirectory(directory).RecordRequestAsync(request.Provider, request.UserId, now); }
            catch (Exception) { /* Display detail only. */ }
        });
    }

    private Task AppendAccessAuditAsync(string kind, ChatRequest request, string? displayName, string actor, CancellationToken token) =>
        _audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, null, request.Provider, request.ChannelId, request.ThreadId)
        {
            Access = new AuditAccessSubject(request.UserId, displayName, actor, AuditAccessSubject.ChatRequestMethod)
        }, token);

    private static async Task ReplyQuietlyAsync(IChatTransport transport, ChatRequest request, string text)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await transport.ReplyAsync(request, text, timeout.Token);
        }
        catch (Exception)
        {
            // The PC still shows the request; a failed courtesy reply must not block the decision.
        }
    }
}
