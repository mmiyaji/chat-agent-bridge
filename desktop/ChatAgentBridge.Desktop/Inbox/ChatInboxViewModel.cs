using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge.Transports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem(ChatRequest request, IChatTransport transport, BridgeSettings settings) : ObservableObject
{
    public ChatRequest Request { get; } = request;
    public IChatTransport Transport { get; } = transport;
    public BridgeSettings Settings { get; } = settings;
    public BridgeSettings? EffectiveSettings { get; set; }
    public string Source => $"{Request.Provider} / {Request.ChannelId} / {Request.UserId}";
    public string Prompt => Request.Text;
    public DateTimeOffset ReceivedAt { get; } = DateTimeOffset.Now;
    [ObservableProperty] public partial string Workspace { get; set; } = settings.WorkspaceRoot;
    [ObservableProperty] public partial string State { get; set; } = "承認待ち";
    public string StateDisplay => State switch
    {
        "承認待ち" => DesktopText.Get("ApprovalPending"),
        "実行中" => DesktopText.Get("ApprovalRunning"),
        "完了" => DesktopText.Get("ApprovalCompleted"),
        "キャンセル" => DesktopText.Get("ApprovalCancelled"),
        "拒否" => DesktopText.Get("ApprovalRejected"),
        "実行／記録エラー" => DesktopText.Get("ApprovalExecutionError"),
        "許可取り消し・実行不可" => DesktopText.Get("ApprovalCancelledPermission"),
        "許可設定を確認できず実行不可" => DesktopText.Get("ApprovalPermissionUnavailable"),
        "自動実行を準備" => DesktopText.Get("ApprovalPreparing"),
        "接続終了・再受信が必要" => DesktopText.Get("ApprovalConnectionEnded"),
        "作業先変更・再投稿が必要" => DesktopText.Get("ApprovalWorkspaceNeedsResubmit"),
        "フォルダー変更済み" => DesktopText.Get("ApprovalWorkspaceChanged"),
        "フォルダー変更不可" => DesktopText.Get("ApprovalWorkspaceChangeFailed"),
        "エージェント変更済み" => DesktopText.Get("AgentSwitchCompleted"),
        "エージェント変更不可" => DesktopText.Get("AgentSwitchFailed"),
        "エージェント変更・再投稿が必要" => DesktopText.Get("AgentSwitchResubmit"),
        "添付受信エラー" => DesktopText.Get("IncomingFilesFailedTitle"),
        "実行中へ追加" => DesktopText.Get("FollowUpApplied"),
        "追加指示の受領待ち" => DesktopText.Get("FollowUpPending"),
        "追加指示の反映未確認" => DesktopText.Get("FollowUpUncertain"),
        _ when State.StartsWith("失敗 (", StringComparison.Ordinal) => DesktopText.Get("ApprovalFailed", State[4..^1]),
        _ => State
    };
    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(StateDisplay));
        OnPropertyChanged(nameof(StateTone));
        OnPropertyChanged(nameof(RequestLabel));
    }
    public string RequestLabel => $"{ReceivedAt:t} · {StateDisplay} · {Prompt.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(Prompt.Length, 80)]}";
    [ObservableProperty] public partial string SessionLabel { get; set; } = DesktopText.Get("InboxSessionPreflight");
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    [ObservableProperty] public partial string FullOutput { get; set; } = DesktopText.Get("InboxOutputIntro");
    [ObservableProperty] public partial string Usage { get; set; } = DesktopText.Get("UsageAfterExecution");
    public ExecutionUi.LiveOutputViewModel LiveOutput { get; } = new();
    public Guid? ExecutionId { get; set; }
    public string? ExecutionPolicyFingerprint { get; set; }
    public bool AllowsResultSharing { get; set; } = true;
    public IReadOnlyList<FileAttachmentCandidate> AttachmentCandidates { get; set; } = [];
    public BridgeSettings? AttachmentSettingsAtStart { get; set; }
    public string? AttachmentSessionId { get; set; }
    public bool AttachmentScanComplete { get; set; }
    [ObservableProperty] public partial string AttachmentStatus { get; set; } = "";
    public ChatProgressReporter? Progress { get; set; }
    public Task ReceiptNotification { get; set; } = Task.CompletedTask;
    internal string FollowUpSummary { get; set; } = "";
    internal List<Task> FollowUpConfirmations { get; } = [];
    internal List<InboxItem> FollowUpInputs { get; } = [];
    internal CancellationTokenSource? FollowUpFeedbackDeadline { get; set; }
}

public sealed partial class InboxConversation(string key) : ObservableObject
{
    public string Key { get; } = key;
    internal InboxConversationSnapshot? Snapshot { get; set; }
    internal int PreviousRequestCount { get; set; }
    public string Workspace => Snapshot?.WorkspaceRoot ?? "";
    public DateTimeOffset LastActivity => Requests.LastOrDefault()?.ReceivedAt ?? Snapshot?.UpdatedAt ?? default;
    public ObservableCollection<InboxItem> Requests { get; } = [];
    [ObservableProperty] public partial string Identity { get; set; } = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Location { get; set; } = "";
    [ObservableProperty] public partial string LatestPrompt { get; set; } = "";
    [ObservableProperty] public partial string StateSummary { get; set; } = "";
    [ObservableProperty] public partial string Activity { get; set; } = "";
    [ObservableProperty] public partial int RequestCount { get; set; }
    public string RequestCountLabel => DesktopText.Get(RequestCount == 1 ? "InboxOneRequest" : "InboxRequestCount", RequestCount);
    public override string ToString() => $"{Title} · {StateSummary} · {RequestCountLabel} · {Location}";
    partial void OnRequestCountChanged(int value)
    {
        OnPropertyChanged(nameof(RequestCountLabel));
        OnPropertyChanged(nameof(HasSeveralRequests));
    }
}

public sealed partial class ChatInboxViewModel : ObservableObject
{
    private const int MaxDisplayedOutputCharacters = 64_000;
    private readonly SettingsStore _store = new();
    private readonly SessionStore _sessions = new();
    private readonly SessionAgentRunner _runner;
    private readonly AppServerSessionRunner _serverRunner;
    private readonly ChatApprovalBroker _approvalBroker = new();
    private readonly ExecutionHistoryStore _history = new();
    private readonly AuditStore _audit = new();
    private CancellationTokenSource? _connection;
    private CancellationTokenSource? _execution;
    private Task? _receiveTask;
    private IChatTransport? _transport;
    private BridgeSettings? _accessSettings;
    private BridgeSettings? _connectionSettings;
    internal BridgeSettings? ActiveConnectionSettings => _connectionSettings;
    private readonly SemaphoreSlim _accessRefreshGate = new(1, 1);
    private InboxItem? _activeItem;
    private Task _connectionNotice = Task.CompletedTask;
    private bool _disconnecting;
    private bool _closing;
    private bool _hasRuntimeStatus;
    private InboxItem? _notificationApprovedItem;
    private InboxItem? _automaticallyApprovedItem;
    private bool _processingPolicies;
    private readonly HashSet<InboxItem> _notifiedRequests = [];
    private readonly HashSet<InboxItem> _automaticallyAttempted = [];
    private readonly Dictionary<InboxItem, PropertyChangedEventHandler> _requestHandlers = [];
    private int _droppedCount;
    private readonly ExecutionUi.LiveOutputViewModel _emptyLiveOutput = new();
    public ExecutionUi.LiveOutputViewModel SelectedLiveOutput => Selected?.LiveOutput ?? _emptyLiveOutput;

    public ObservableCollection<InboxItem> Requests { get; } = [];
    public ObservableCollection<InboxConversation> Conversations { get; } = [];
    public event Action<InboxItem>? RequestReceived;
    public event Action? ConnectionEnded;
    [ObservableProperty] public partial string NotificationStatus { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = DesktopText.Get("InboxDisconnected");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOverflowNotice))] public partial string OverflowNotice { get; set; } = "";
    public bool HasOverflowNotice => !string.IsNullOrWhiteSpace(OverflowNotice);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProgressNotice))]
    public partial string ProgressNotice { get; set; } = "";
    public bool HasProgressNotice => !string.IsNullOrWhiteSpace(ProgressNotice);
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectMattermostCommand)), NotifyCanExecuteChangedFor(nameof(ConnectSlackCommand)), NotifyCanExecuteChangedFor(nameof(ConnectDiscordCommand)), NotifyCanExecuteChangedFor(nameof(DisconnectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand)), NotifyCanExecuteChangedFor(nameof(SendAttachmentsCommand))]
    public partial bool IsConnected { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand)), NotifyCanExecuteChangedFor(nameof(SendAttachmentsCommand))]
    public partial bool IsConnectionConfirmed { get; set; }
    [ObservableProperty] public partial bool HasConnectionError { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand)), NotifyCanExecuteChangedFor(nameof(SendAttachmentsCommand))]
    public partial bool IsExecuting { get; set; }
    partial void OnIsExecutingChanged(bool value)
    {
        SendAttachmentsCommand.NotifyCanExecuteChanged();
        NotifyActiveRequestProperties();
        if (!value) _ = ProcessPendingPoliciesAsync();
    }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand)), NotifyCanExecuteChangedFor(nameof(SendAttachmentsCommand))]
    public partial InboxItem? Selected { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveCommand)), NotifyCanExecuteChangedFor(nameof(RejectCommand)), NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyCanExecuteChangedFor(nameof(SendResultCommand)), NotifyCanExecuteChangedFor(nameof(SendAttachmentsCommand))]
    public partial InboxConversation? SelectedConversation { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string StateFilter { get; set; } = "all";
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty] public partial bool IsWideLayout { get; set; } = true;
    public ObservableCollection<InboxConversation> VisibleConversations { get; } = [];
    public IEnumerable<InboxItem> ConversationRequests => SelectedConversation?.Requests ?? [];
    public string SelectedOutput => Selected?.FullOutput ?? DesktopText.Get("InboxSelectConversation");
    public string SelectedUsage => Selected?.Usage ?? DesktopText.Get("UsageAfterExecution");
    public string ConversationCountLabel => DesktopText.Get(VisibleConversations.Count == 1 ? "InboxOneConversation" : "InboxConversationCount", VisibleConversations.Count);
    public string SelectedRequestCountLabel => DesktopText.Get(SelectedConversation?.Requests.Count == 1 ? "InboxOneRequest" : "InboxRequestCount", SelectedConversation?.Requests.Count ?? 0);
    public bool HasSelectedConversation => SelectedConversation is not null;
    public bool HasVisibleConversations => VisibleConversations.Count > 0;
    public bool HasConversationFilters => !string.IsNullOrWhiteSpace(SearchText) || StateFilter != "all";
    public bool ShowEmptyConversationList => !HasVisibleConversations && !HasConnectionError;
    public string EmptyConversationHeading => DesktopText.Get(HasConversationFilters
        ? "InboxEmptyFilteredHeading"
        : !IsConnected ? "InboxEmptyOfflineHeading"
        : !IsConnectionConfirmed ? "InboxEmptyConnectingHeading"
        : "InboxEmptyWaitingHeading");
    public string EmptyConversationDescription => DesktopText.Get(HasConversationFilters
        ? "InboxEmptyFilteredDescription"
        : !IsConnected ? "InboxEmptyOfflineDescription"
        : !IsConnectionConfirmed ? "InboxEmptyConnectingDescription"
        : "InboxEmptyWaitingDescription");
    public string EmptyDetailHeading => DesktopText.Get("InboxEmptyDetailHeading");
    public string EmptyDetailDescription => DesktopText.Get("InboxEmptyDetailDescription");
    public string ClearConversationFiltersLabel => DesktopText.Get("InboxClearFiltersLabel");
    public bool HasOtherRequestRunning => IsExecuting && _activeItem is { LiveOutput.IsActive: true } active && !ReferenceEquals(Selected, active);
    public string ActiveRequestBannerText
    {
        get
        {
            if (_activeItem is not { } active) return "";
            var provider = active.Request.Provider switch { "mattermost" => "Mattermost", "slack" => "Slack", "discord" => "Discord", _ => active.Request.Provider };
            var conversation = Conversations.FirstOrDefault(item => item.Requests.Contains(active));
            var identity = conversation?.Title ?? $"{provider} · {active.Request.UserId}";
            var prompt = active.Prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (prompt.Length > 90) prompt = prompt[..90] + "…";
            return DesktopText.Get("InboxActiveRequestBanner", identity, prompt);
        }
    }

    private void NotifyActiveRequestProperties()
    {
        OnPropertyChanged(nameof(HasOtherRequestRunning));
        OnPropertyChanged(nameof(ActiveRequestBannerText));
        ReturnToActiveRequestCommand.NotifyCanExecuteChanged();
    }
    public string EmptyListMessage => DesktopText.Get(Requests.Count == 0 ? "InboxWaitingForRequests" : "InboxNoMatches");
    partial void OnSelectedChanged(InboxItem? value)
    {
        NotifyRestoredSelection();
        OnPropertyChanged(nameof(SelectedLiveOutput));
        OnPropertyChanged(nameof(SelectedAttachmentStatus));
        SendAttachmentsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(SelectedUsage));
        NotifyActiveRequestProperties();
        ApproveCommand.NotifyCanExecuteChanged(); RejectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); SendResultCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedConversationChanged(InboxConversation? value)
    {
        OnPropertyChanged(nameof(ConversationRequests));
        OnPropertyChanged(nameof(SelectedRequestCountLabel));
        OnPropertyChanged(nameof(HasSelectedConversation));
        NotifyRestoredSelection();
        if (value is not null && (Selected is null || !value.Requests.Contains(Selected))) Selected = PreferredRequest(value);
        ApproveCommand.NotifyCanExecuteChanged(); RejectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); SendResultCommand.NotifyCanExecuteChanged();
    }
    partial void OnSearchTextChanged(string value) => RefreshVisibleConversations();
    partial void OnStateFilterChanged(string value) => RefreshVisibleConversations();
    private bool CanCancel() => IsExecuting && Selected is not null && ReferenceEquals(Selected, _activeItem) && _execution is { IsCancellationRequested: false };
    private bool CanReturnToActiveRequest() => HasOtherRequestRunning;

    [RelayCommand(CanExecute = nameof(CanReturnToActiveRequest))]
    private void ReturnToActiveRequest()
    {
        if (CanReturnToActiveRequest() && _activeItem is { } active)
            RevealRequest(active);
    }

    [RelayCommand]
    private void ClearConversationFilters()
    {
        SearchText = string.Empty;
        StateFilter = "all";
    }

    public void OpenConversation(InboxConversation? conversation)
    {
        if (conversation is null) return;
        SelectedConversation = conversation;
        if (Selected is null || !conversation.Requests.Contains(Selected)) Selected = PreferredRequest(conversation);
        IsDetailOpen = true;
    }
    public void BackToConversations() => IsDetailOpen = false;
    private static InboxItem? PreferredRequest(InboxConversation conversation) =>
        conversation.Requests.FirstOrDefault(item => item.State == "実行中") ??
        conversation.Requests.FirstOrDefault(item => item.State == "承認待ち") ?? conversation.Requests.LastOrDefault();
    public void SelectRequest(InboxItem? item)
    {
        if (item is null) return;
        Selected = item;
        SelectedConversation = Conversations.FirstOrDefault(conversation => conversation.Requests.Contains(item));
    }
    public void RevealRequest(InboxItem? item)
    {
        SearchText = "";
        StateFilter = "all";
        SelectRequest(item);
        IsDetailOpen = true;
    }
    private bool MatchesFilter(InboxConversation conversation)
    {
        var latest = conversation.Requests.LastOrDefault();
        if (latest is null)
        {
            if (StateFilter is "pending" or "active") return false;
            var restoredQuery = SearchText.Trim();
            return restoredQuery.Length == 0 || $"{conversation.Title} {conversation.Location} {conversation.Identity} {conversation.Workspace}"
                .Contains(restoredQuery, StringComparison.CurrentCultureIgnoreCase);
        }
        if (StateFilter == "pending" && !conversation.Requests.Any(item => item.State == "承認待ち")) return false;
        if (StateFilter == "active" && !conversation.Requests.Any(item => item.State is "実行中" or "追加指示の受領待ち")) return false;
        if (StateFilter == "completed" && !conversation.Requests.Any(item => item.State is not ("承認待ち" or "実行中" or "自動実行を準備" or "追加指示の受領待ち"))) return false;
        var query = SearchText.Trim();
        return query.Length == 0 || conversation.Identity.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            conversation.Requests.Any(item => item.Prompt.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Workspace.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
    private void RefreshVisibleConversations()
    {
        var wanted = Conversations.Where(MatchesFilter).ToArray();
        var wantedSet = wanted.ToHashSet();
        foreach (var old in VisibleConversations.Where(item => !wantedSet.Contains(item)).ToArray()) VisibleConversations.Remove(old);
        for (var index = 0; index < wanted.Length; index++)
        {
            if (index < VisibleConversations.Count && ReferenceEquals(VisibleConversations[index], wanted[index])) continue;
            var current = VisibleConversations.IndexOf(wanted[index]);
            if (current < 0) VisibleConversations.Insert(index, wanted[index]);
            else if (current != index) VisibleConversations.Move(current, index);
        }
        if (SelectedConversation is not null && !wantedSet.Contains(SelectedConversation))
        {
            SelectedConversation = null;
            Selected = null;
            IsDetailOpen = false;
        }
        OnPropertyChanged(nameof(ConversationCountLabel));
        OnPropertyChanged(nameof(HasVisibleConversations));
        OnPropertyChanged(nameof(EmptyListMessage));
        OnPropertyChanged(nameof(HasConversationFilters));
        OnPropertyChanged(nameof(ShowEmptyConversationList));
        OnPropertyChanged(nameof(EmptyConversationHeading));
        OnPropertyChanged(nameof(EmptyConversationDescription));
    }
    private static string ConversationKey(InboxItem item) => System.Text.Json.JsonSerializer.Serialize(CreateSessionKey(item));
    private void RefreshConversations(InboxItem? changedItem = null)
    {
        var changedKey = changedItem is null ? null : ConversationKey(changedItem);
        var groups = Requests.GroupBy(ConversationKey).ToDictionary(group => group.Key, group => group.OrderBy(item => item.ReceivedAt).ToArray());
        var byKey = Conversations.ToDictionary(item => item.Key);
        foreach (var existing in Conversations.Where(item => item.Requests.Count > 0 && !groups.ContainsKey(item.Key)).ToArray())
        {
            existing.PreviousRequestCount = existing.RequestCount;
            existing.Requests.Clear();
            if (existing.Snapshot is { } snapshot)
            {
                existing.Snapshot = snapshot with { State = snapshot.State is "pending" or "running" ? "interrupted" : snapshot.State };
                ApplyRestoredPresentation(existing);
            }
        }
        foreach (var group in groups.OrderByDescending(pair => pair.Value[^1].ReceivedAt))
        {
            if (changedKey is not null && group.Key != changedKey) continue;
            var conversation = byKey.GetValueOrDefault(group.Key);
            if (conversation is null)
            {
                conversation = new InboxConversation(group.Key);
                Conversations.Add(conversation);
            }
            var items = group.Value;
            foreach (var oldRequest in conversation.Requests.Where(request => !items.Contains(request)).ToArray())
                conversation.Requests.Remove(oldRequest);
            for (var index = 0; index < items.Length; index++)
            {
                var current = conversation.Requests.IndexOf(items[index]);
                if (current < 0) conversation.Requests.Insert(index, items[index]);
                else if (current != index) conversation.Requests.Move(current, index);
            }
            var latest = items[^1];
            conversation.Identity = $"{latest.Request.Provider}  /  {latest.Request.ChannelId}  /  {latest.Request.ThreadId}  /  {latest.Request.UserId}";
            var provider = latest.Request.Provider switch { "mattermost" => "Mattermost", "slack" => "Slack", "discord" => "Discord", _ => latest.Request.Provider };
            conversation.Title = $"{provider} · {latest.Request.UserId}";
            conversation.Location = DesktopText.Get("ConversationLocation", latest.Request.ChannelId, latest.Request.ThreadId);
            conversation.LatestPrompt = latest.Prompt;
            var stateItem = items.FirstOrDefault(item => item.State == "実行中") ??
                items.FirstOrDefault(item => item.State == "承認待ち") ?? latest;
            conversation.StateSummary = stateItem.StateDisplay;
            conversation.StateTone = stateItem.StateTone;
            conversation.Activity = latest.ReceivedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
            conversation.RequestCount = (int)Math.Min(int.MaxValue, (long)conversation.PreviousRequestCount + items.Length);
            UpdateConversationSnapshot(conversation, latest, stateItem);
            conversation.RefreshPresentation();
        }
        var sorted = Conversations.OrderByDescending(item => item.LastActivity).ToArray();
        var evicted = sorted.Skip(500).Where(item => item.Requests.Count == 0).ToHashSet();
        foreach (var old in evicted) Conversations.Remove(old);
        if (evicted.Count > 0) sorted = sorted.Where(item => !evicted.Contains(item)).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            if (ReferenceEquals(Conversations[index], sorted[index])) continue;
            var current = Conversations.IndexOf(sorted[index]);
            if (current != index) Conversations.Move(current, index);
        }
        RefreshVisibleConversations();
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(ConversationRequests));
        OnPropertyChanged(nameof(SelectedRequestCountLabel));
        if (SelectedConversation is not null && !Conversations.Contains(SelectedConversation))
        {
            SelectedConversation = null; Selected = null; IsDetailOpen = false;
        }
        else if (SelectedConversation is not null && (Selected is null || !SelectedConversation.Requests.Contains(Selected)))
            Selected = PreferredRequest(SelectedConversation);
        NotifyRestoredSelection();
        QueueConversationSave();
    }
    public Func<InboxItem, CancellationToken, Task<bool>>? ConfirmAsync { get; set; }
    public Func<InboxItem, string, Task<bool>>? ConfirmReplyAsync { get; set; }
    public Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? ServerApprovalHandler { get; set; }
    public Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? ServerInputHandler { get; set; }

    public ChatInboxViewModel()
    {
        _runner = new SessionAgentRunner(_sessions);
        _serverRunner = new AppServerSessionRunner(_sessions);
        Requests.CollectionChanged += Requests_CollectionChanged;
    }

    private void Requests_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var pair in _requestHandlers)
            {
                _ = CompleteRequestActionsAsync(pair.Key);
                pair.Key.PropertyChanged -= pair.Value;
                pair.Key.LiveOutput.Dispose();
            }
            _requestHandlers.Clear();
        }
        else if (e.OldItems is not null)
        {
            foreach (InboxItem item in e.OldItems)
            {
                _ = CompleteRequestActionsAsync(item);
                var conversation = Conversations.FirstOrDefault(row => row.Requests.Contains(item));
                if (conversation is not null && conversation.PreviousRequestCount < int.MaxValue)
                    conversation.PreviousRequestCount++;
                if (_requestHandlers.Remove(item, out var handler)) item.PropertyChanged -= handler;
                item.LiveOutput.Dispose();
            }
        }
        if (e.NewItems is not null)
        {
            foreach (InboxItem item in e.NewItems)
            {
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName == nameof(InboxItem.State) && item.State != "承認待ち")
                        _ = CompleteRequestActionsAsync(item);
                    if (args.PropertyName is nameof(InboxItem.State) or nameof(InboxItem.Workspace)) RefreshConversations(item);
                    if (ReferenceEquals(item, Selected))
                    {
                        if (args.PropertyName == nameof(InboxItem.FullOutput)) OnPropertyChanged(nameof(SelectedOutput));
                        if (args.PropertyName == nameof(InboxItem.Usage)) OnPropertyChanged(nameof(SelectedUsage));
                        if (args.PropertyName == nameof(InboxItem.AttachmentStatus))
                        {
                            OnPropertyChanged(nameof(SelectedAttachmentStatus));
                            SendAttachmentsCommand.NotifyCanExecuteChanged();
                        }
                    }
                };
                item.PropertyChanged += handler;
                _requestHandlers[item] = handler;
            }
        }
        RefreshConversations();
    }

    private bool CanConnect() => !_closing && !IsConnected && !IsExecuting;
    private bool CanApprove() => !_closing && !IsExecuting && IsConnectionConfirmed && Selected is { State: "承認待ち" } item && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectMattermostAsync() => ConnectAsync(new MattermostWebSocketTransport());

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectSlackAsync() => ConnectAsync(new SlackSocketModeTransport());

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectDiscordAsync()
    {
#if CAB_DISCORD_FIXTURE
        var endpoint = Environment.GetEnvironmentVariable("CAB_DISCORD_FIXTURE_URL");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var parent = Directory.GetParent(_store.DirectoryPath);
            var settings = await _store.LoadAsync();
            if (parent is null || !Guid.TryParseExact(parent.Name, "D", out _) ||
                !File.Exists(Path.Combine(_store.DirectoryPath, ".cab-discord-fixture")) ||
                settings.DiscordBotToken != "offline-discord-fixture")
                throw new InvalidOperationException("Isolated Discord fixture profile is missing.");
            await ConnectAsync(DiscordGatewayTransport.CreateLoopbackFixture(new Uri(endpoint), _sessions));
            return;
        }
#endif
        await ConnectAsync(new DiscordGatewayTransport());
    }

    private async Task ConnectAsync(IChatTransport transport)
    {
        if (IsConnected || _closing) { await transport.DisposeAsync(); return; }
        HasConnectionError = false;
        ProgressNotice = "";
        IsConnected = true;
        _disconnecting = false;
        IsConnectionConfirmed = false;
        Status = DesktopText.Get("InboxPreparingConnection");
        try
        {
            await EnsureConversationsRestoredAsync();
            var settings = await _store.LoadAsync();
            var allowed = transport switch
            {
                MattermostWebSocketTransport => settings.MattermostAllowedUserIds,
                DiscordGatewayTransport => settings.DiscordAllowedUserIds,
                _ => settings.SlackAllowedUserIds
            };
            if (string.IsNullOrWhiteSpace(allowed))
            {
                await transport.DisposeAsync();
                IsConnected = false;
                Status = DesktopText.Get("InboxAllowedUsersRequired");
                HasConnectionError = true;
                return;
            }
            if (transport is DiscordGatewayTransport &&
                (string.IsNullOrWhiteSpace(settings.DiscordBotToken) ||
                 !settings.DiscordAllowDirectMessages && string.IsNullOrWhiteSpace(settings.DiscordAllowedGuildIds)))
            {
                await transport.DisposeAsync();
                IsConnected = false;
                Status = DesktopText.Get("DiscordInboxConfigurationMissing");
                HasConnectionError = true;
                return;
            }
            Volatile.Write(ref _accessSettings, settings);
            await _history.ListAsync();
            await _audit.ListAsync();
            await _sessions.ListAsync();
            if (_closing) { await transport.DisposeAsync(); IsConnected = false; return; }
            if (!IsValidInboxWorkspacePath(settings.WorkspaceRoot) || !Directory.Exists(settings.WorkspaceRoot))
            {
                await transport.DisposeAsync();
                IsConnected = false;
                Status = DesktopText.Get("InboxWorkspaceInvalid");
                HasConnectionError = true;
                return;
            }
            // Reconnection invalidates pending work from the previous connection.
            Requests.Clear();
            _notifiedRequests.Clear();
            _automaticallyAttempted.Clear();
            Selected = null;
            _transport = transport;
            _connectionSettings = settings;
            var connection = new CancellationTokenSource();
            _connection = connection;
            SubscribeConnectionEvents(transport, settings, connection);
            Status = DesktopText.Get("InboxConnectingWaiting");
            _receiveTask = ReceiveAsync(transport, settings, connection);
        }
        catch (Exception)
        {
            await transport.DisposeAsync();
            Status = DesktopText.Get("InboxConnectionFailed");
            HasConnectionError = true;
            IsConnectionConfirmed = false;
            IsConnected = false;
        }
    }

    private async Task ReceiveAsync(IChatTransport transport, BridgeSettings settings, CancellationTokenSource connection)
    {
        var cancellationToken = connection.Token;
        var receivedAudits = new ReceivedAuditQueue(_audit, _ => ReportReceivedAuditFailure(connection));
        try
        {
            await transport.RunAsync(settings, async (request, token) =>
            {
                // The socket retains its authenticated identity, but each new request uses
                // the latest saved execution defaults and access rules.
                BridgeSettings accessSettings;
                try { accessSettings = await _store.LoadAsync(token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
                    System.Security.Cryptography.CryptographicException or ArgumentException or FormatException)
                {
                    Volatile.Write(ref _accessSettings, null);
                    App.DispatcherQueue.TryEnqueue(() => { HasConnectionError = true; Status = DesktopText.Get("InboxPermissionsUnavailable"); });
                    return;
                }
                if (!IsValidInboxWorkspacePath(accessSettings.WorkspaceRoot))
                {
                    Volatile.Write(ref _accessSettings, null);
                    App.DispatcherQueue.TryEnqueue(ReportInvalidWorkspaceSettings);
                    return;
                }
                Volatile.Write(ref _accessSettings, accessSettings);
                if (_disconnecting || !CanReceive(accessSettings, request, settings)) return;
                await QueueReceivedAuditAsync(receivedAudits, request, cancellationToken);
                // A full queue can yield while the user revokes access or disconnects.
                cancellationToken.ThrowIfCancellationRequested();
                var latestAccess = Volatile.Read(ref _accessSettings);
                if (_disconnecting || latestAccess is null || !CanReceive(latestAccess, request, settings)) return;
                accessSettings = latestAccess;
                if (request.CancellationTargetMessageId is not null)
                {
                    await TryHandleChatControlAsync(transport, accessSettings, request, token);
                    return;
                }
                if (_approvalBroker.TryHandle(request)) return;
                if (await TryHandleChatAnswerAsync(transport, accessSettings, request, token)) return;
                if (await TryHandleAgentCommandAsync(transport, accessSettings, request, token)) return;
                if (await TryHandleChatControlAsync(transport, accessSettings, request, token)) return;
                var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!App.DispatcherQueue.TryEnqueue(() => completion.TrySetResult(
                    AcceptReceivedRequestAsync(request, transport, accessSettings, token)))) completion.TrySetCanceled();
                await await completion.Task;
            }, cancellationToken);
            Status = DesktopText.Get("InboxConnectionEnded");
            HasConnectionError = !_disconnecting && !_closing && !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) { Status = DesktopText.Get("InboxDisconnectedNotice"); }
        catch (DiscordConfigurationException error)
        {
            Status = DesktopText.Get(error.Code switch
            {
                "missing_configuration" => "DiscordInboxConfigurationMissing",
                "authentication" => "DiscordInboxAuthenticationFailed",
                "message_content_intent" => "DiscordInboxMessageContentRequired",
                "missing_permissions" => "DiscordInboxPermissionsRequired",
                _ => "InboxReconnectFailed"
            });
            HasConnectionError = true;
        }
        catch (Exception)
        {
            // Provider error payloads can contain tokens or socket URLs.
            Status = DesktopText.Get("InboxReconnectFailed");
            HasConnectionError = true;
        }
        finally
        {
            IsConnectionConfirmed = false;
            ConnectionEnded?.Invoke();
            RequestExecutionCancellation();
            connection.Cancel();
            try
            {
                if (!await receivedAudits.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(1500)))
                    ReportReceivedAuditFailure(connection);
            }
            finally { await receivedAudits.DisposeAsync(); }
            foreach (var item in Requests.Where(item => item.State == "承認待ち")) item.State = "接続終了・再受信が必要";
            ApproveCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
            SendResultCommand.NotifyCanExecuteChanged();
            // Keep reconnect disabled until old execution and transport cleanup finish.
            if (ApproveCommand.ExecutionTask is { IsCompleted: false } execution) await execution;
            if (SendResultCommand.ExecutionTask is { IsCompleted: false } reply) await reply;
            if (SendAttachmentsCommand.ExecutionTask is { IsCompleted: false } attachments) await attachments;
            await FinishConnectionProgressAsync(transport);
            await _connectionNotice;
            await transport.DisposeAsync();
            if (ReferenceEquals(_transport, transport)) { _transport = null; _connectionSettings = null; OnPropertyChanged(nameof(ConnectionProvider)); }
            if (ReferenceEquals(_connection, connection)) _connection = null;
            connection.Dispose();
            IsConnectionConfirmed = false;
            IsConnected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        var connection = _connection;
        var receive = _receiveTask;
        _disconnecting = true;
        HasConnectionError = false;
        var wasConfirmed = IsConnectionConfirmed;
        IsConnectionConfirmed = false;
        RequestExecutionCancellation();
        if (wasConfirmed && _transport is { } transport && Volatile.Read(ref _accessSettings) is { } settings)
        {
            await _connectionNotice;
            await NotifyConnectionAsync(transport, settings,
                DesktopText.Get("InboxDisconnectChatNotice"));
        }
        connection?.Cancel();
        if (receive is not null) await receive;
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var item = Selected;
        if (item is null || !CanApprove()) return;
        IsExecuting = true;
        _activeItem = item;
        NotifyActiveRequestProperties();
        var approvedByNotification = ReferenceEquals(_notificationApprovedItem, item);
        var approvedAutomatically = ReferenceEquals(_automaticallyApprovedItem, item);
        ExecutionRecord? record = null;
        IncomingAttachmentLease? incomingFiles = null;
        try
        {
            var connection = _connection;
            if (connection is null || connection.IsCancellationRequested) return;
            _execution = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            CancelCommand.NotifyCanExecuteChanged();
            var token = _execution.Token;
            if (!await CheckAccessAsync(item, token)) return;
            if (WorkspaceChatCommand.TryParse(item.Prompt, out var requestedWorkspace))
            {
                await ChangeWorkspaceAsync(item, requestedWorkspace, approvedByNotification, token);
                return;
            }
            if (AgentChatCommand.TryParse(item.Prompt, out var switchCommand))
            {
                await ChangeAgentAsync(item, switchCommand, approvedByNotification, token);
                return;
            }
            var key = CreateSessionKey(item);
            using var conversationOperation = _sessions.TryBeginConversationOperation(key)
                ?? throw new InvalidOperationException(DesktopText.Get("AgentSwitchBusy"));
            var binding = await _sessions.FindExecutionBindingAsync(key, token);
            var workspaceSettings = await _sessions.ApplyWorkspaceAsync(key, item.Settings, token);
            var effectiveSettings = ConversationExecutionPolicy.GetEffectiveSettings(workspaceSettings, binding);
            item.EffectiveSettings = effectiveSettings;
            item.Workspace = effectiveSettings.WorkspaceRoot;
            if (!ExecutionPolicy.IsWorkspaceTrusted(effectiveSettings, effectiveSettings.WorkspaceRoot))
                throw new InvalidOperationException(DesktopText.Get("WorkspaceOutsideTrustedRootsShort"));
            if (approvedAutomatically && (ConversationExecutionPolicy.RequiresApproval(key, effectiveSettings, binding) ||
                (await _sessions.FindAgentSelectionAsync(key, token))?.RequiresApproval == true))
            {
                NotifyPendingRequest(item);
                Status = DesktopText.Get("InboxApprovalRequiredAgain");
                return;
            }
            item.ExecutionPolicyFingerprint = ConversationExecutionPolicy.GetFingerprint(key, effectiveSettings);
            item.AllowsResultSharing = effectiveSettings.ChatResultMode != "none";
            var requestedModel = effectiveSettings.AgentModel;
            var requestedEffort = effectiveSettings.AgentReasoningEffort;
            item.SessionLabel = binding is null ? DesktopText.Get("InboxNewConversation") : DesktopText.Get("InboxContinueSession", binding.SessionId);
            item.SessionLabel += DesktopText.Get("InboxSessionModel", requestedModel ?? DesktopText.Get("CliDefault"), requestedEffort ?? DesktopText.Get("CliDefault"));
            item.SessionLabel += DesktopText.Get("InboxConversationOwner", item.Request.UserId);
            item.SessionLabel += DesktopText.Get("InboxSessionPolicy", ApprovalLabel(effectiveSettings.ChatApprovalMode), ResultLabel(effectiveSettings.ChatResultMode), PermissionLabel(effectiveSettings.AgentPermissionMode));
            if (!approvedByNotification && !approvedAutomatically && (ConfirmAsync is null || !await ConfirmAsync(item, token))) return;
            if (item.State != "承認待ち" || token.IsCancellationRequested) return;
            if (!await CheckAccessAsync(item, token)) return;
            record = new ExecutionRecord
            {
                StartedAt = DateTimeOffset.UtcNow, Source = item.Request.Provider,
                ChannelId = item.Request.ChannelId, ThreadId = item.Request.ThreadId,
                UserId = item.Request.UserId, WorkspaceRoot = item.Workspace,
                SessionId = binding?.SessionId, Status = "running",
                AgentModel = requestedModel,
                AgentReasoningEffort = requestedEffort
            };
            await _history.RecordAsync(record, _execution.Token);
            item.ExecutionId = record.Id;
            await AuditAsync("approved", item.Request, record.Id, _execution.Token);
            await AuditAsync("started", item.Request, record.Id, _execution.Token);
            item.State = "実行中";
            item.FullOutput = DesktopText.Get("InboxAgentRunning");
            item.Usage = DesktopText.Get("UsageRunning");
            await StartProgressAsync(item);
            incomingFiles = await PrepareIncomingFilesAsync(item, token);
            var attachmentBefore = await CaptureAttachmentBaselineAsync(item, effectiveSettings, token);
            if (!await CheckAccessAsync(item, token)) return;
            var agentPrompt = FileAttachmentAgentPrompt.Build(incomingFiles?.BuildPrompt(item.Request.Text) ?? item.Request.Text,
                item.AttachmentSettingsAtStart ?? effectiveSettings, attachmentBefore is not null);
            var progress = item.LiveOutput.Begin();
            NotifyActiveRequestProperties();
            AgentRunResult result;
            try
            {
                using var incomingExecution = incomingFiles?.BeginExecutionScope();
                result = effectiveSettings.AgentMode == "app-server"
                    ? await _serverRunner.RunAsync(key, effectiveSettings, agentPrompt,
                        new AppServerRunOptions(ApprovalHandler: (approval, ct) => HandleApprovalAsync(item, approval, ct), InputHandler: (input, ct) => HandleChatInputAsync(item, input, ct), Progress: progress,
                            ActiveTurnChanged: turn => SetActiveTurn(item, turn)), _execution.Token)
                    : await _runner.RunAsync(key, effectiveSettings, agentPrompt, _execution.Token, progress,
                        turn => SetActiveTurn(item, turn));
            }
            finally
            {
                await CloseActiveTurnAsync(item);
                item.LiveOutput.Finish();
                NotifyActiveRequestProperties();
            }
            var output = (result.Claude?.FinalMessage ?? result.Codex?.FinalMessage ??
                    (result.CliSession is { } cliSession ? cliSession.FinalMessage ?? "" : result.Output)) + (string.IsNullOrEmpty(result.Error) ? "" : "\n" + result.Error);
            var needsLogin = !result.Success && effectiveSettings.AgentProvider == AgentProfile.Claude &&
                (output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("Please run /login", StringComparison.OrdinalIgnoreCase));
            if (needsLogin)
                output = DesktopText.Get("InboxClaudeLoginInstructions") + Environment.NewLine + Environment.NewLine + output;
            item.Usage = UsageUi.UsageSummaryFormatter.Format(result.Usage, result.Metrics);
            if (result.HasOutputTruncated) output += Environment.NewLine + DesktopText.Get("InboxOutputTruncated");
            item.FullOutput = BoundOutput(output);
            // Keep the reply preview and displayed output bounded for the 100-item inbox cap.
            var reply = ExtractReply(result);
            item.ResultText = reply.Length > 4001 ? reply[..4001] : reply;
            item.State = result.Success ? "完了" : $"失敗 ({result.ExitCode})";
            var updated = await _sessions.FindAsync(key);
            if (result.Success && updated is not null && await CheckAccessAsync(item, token))
            {
                await _sessions.CompleteAgentHandoffAsync(key, updated.SessionId, token);
                try
                {
                    var answer = ExtractReply(result);
                    if (!string.IsNullOrWhiteSpace(answer) && (await new AgentConnectionStore().ListAsync(token)).Count > 0)
                        await new ConversationSummaryStore().RecordAsync(key, AgentProfile.ResolveProvider(effectiveSettings),
                            updated.SessionId, SummaryPrompt(item),
                            answer[..Math.Min(answer.Length, 90000)], token);
                }
                catch (Exception) { NotificationStatus = DesktopText.Get("AgentSummarySaveFailed"); }
            }
            if (result.Success && !approvedAutomatically && effectiveSettings.ChatApprovalMode == "first" && updated is not null &&
                await CheckAccessAsync(item, token))
                await _sessions.SetFirstApprovalFingerprintAsync(key, item.ExecutionPolicyFingerprint!, token);
            if (updated is not null) item.SessionLabel = DesktopText.Get("InboxUpdatedSessionLabel", updated.SessionId,
                requestedModel ?? DesktopText.Get("CliDefault"), requestedEffort ?? DesktopText.Get("CliDefault"));
            record = record with { FinishedAt = DateTimeOffset.UtcNow, Status = result.Success ? "completed" : "failed", ExitCode = result.ExitCode, SessionId = updated?.SessionId, Usage = result.Usage, Metrics = result.Metrics };
            await _history.RecordAsync(record);
            await AuditAsync(record.Status, item.Request, record.Id);
            await FinishProgressAsync(item, result.Success ? ChatProgressState.Completed : ChatProgressState.Failed);
            try
            {
                if (!await CheckAccessAsync(item, token)) return;
                Status = needsLogin ? DesktopText.Get("InboxClaudeLoginRequired")
                    : result.Success ? DesktopText.Get("InboxResultReady")
                    : DesktopText.Get("InboxExecutionFailed");
                if (result.Success && effectiveSettings.ChatResultMode == "auto" && item.ResultText.Length > 0)
                {
                    await item.Transport.ReplyAsync(item.Request, BoundedReply(item.ResultText), token);
                    await AuditAsync("reply_sent", item.Request, record.Id, token);
                    Status = DesktopText.Get("InboxResultAutoShared");
                }
                else
                {
                    if (item.Progress is null)
                        await item.Transport.ReplyAsync(item.Request,
                            DesktopText.Get("InboxExecutionStateChatNotice", item.StateDisplay), token);
                    if (effectiveSettings.ChatResultMode == "none")
                        Status = result.Success ? DesktopText.Get("InboxResultPrivateSuccess")
                            : DesktopText.Get("InboxResultPrivateFailed");
                }
            }
            catch (Exception)
            {
                Status = DesktopText.Get("InboxStatusReplyFailed");
                await AuditAsync("reply_failed", item.Request, record.Id);
            }
            if (result.Success)
                await DiscoverCompletedAttachmentsAsync(item, attachmentBefore, updated?.SessionId ?? UntrackedAttachmentSession(item), token);
        }
        catch (OperationCanceledException)
        {
            item.State = "キャンセル"; Status = DesktopText.Get("InboxExecutionCancelled");
            item.Usage = UsageUi.UsageSummaryFormatter.Format(null, null);
            await FinishProgressAsync(item, ChatProgressState.Cancelled);
            if (record is not null) await FinishFailureAsync(record, "cancelled", item.Request);
            if (item.Progress is null) await NotifyRequestAsync(item, DesktopText.Get("InboxCancelChatNotice"));
        }
        catch (IncomingFilesException)
        {
            item.State = "添付受信エラー";
            item.FullOutput = DesktopText.Get("IncomingFilesFailed");
            Status = item.FullOutput;
            await FinishProgressAsync(item, ChatProgressState.Failed);
            if (record is not null && record.Status == "running") await FinishFailureAsync(record, "failed", item.Request);
            await NotifyRequestAsync(item, DesktopText.Get("IncomingFilesFailed"));
        }
        catch (Exception)
        {
            if (record?.Status == "running" || record is null) item.State = "実行／記録エラー";
            item.Usage = UsageUi.UsageSummaryFormatter.Format(null, null);
            Status = DesktopText.Get("InboxExecutionRecordFailed");
            await FinishProgressAsync(item, ChatProgressState.Failed);
            if (record is not null && record.Status == "running") await FinishFailureAsync(record, "failed", item.Request);
        }
        finally
        {
            try { await CloseActiveTurnAsync(item); }
            finally { incomingFiles?.Dispose(); }
            if (record is not null) _history.EndTracking(record.Id);
            _execution?.Dispose();
            _execution = null;
            _activeItem = null;
            IsExecuting = false;
            ConnectMattermostCommand.NotifyCanExecuteChanged();
            ConnectSlackCommand.NotifyCanExecuteChanged();
            ConnectDiscordCommand.NotifyCanExecuteChanged();
            SendResultCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task RejectAsync()
    {
        var item = Selected;
        if (item is null || !CanApprove()) return;
        IsExecuting = true;
        try
        {
            await AuditAsync("rejected", item.Request, null);
            item.State = "拒否";
            Status = DesktopText.Get("InboxRequestRejected");
            await FinishProgressAsync(item, ChatProgressState.Rejected);
            if (item.Progress is null) await NotifyRequestAsync(item, DesktopText.Get("InboxRejectChatNotice"));
        }
        catch (Exception) { Status = DesktopText.Get("InboxRejectRecordFailed"); }
        finally { IsExecuting = false; }
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    public async Task<Notifications.PendingNotificationActionResult> ApproveNotificationAsync(InboxItem item)
    {
        bool IsAvailable() => !_closing && Requests.Contains(item) && IsConnectionConfirmed &&
            ReferenceEquals(item.Transport, _transport) && _connection is { IsCancellationRequested: false } &&
            item.State == "承認待ち";
        if (!IsAvailable())
        {
            TraceUnavailableNotification(item);
            return Notifications.PendingNotificationActionResult.Unavailable;
        }
        if (IsExecuting || ApproveCommand.IsRunning || RejectCommand.IsRunning)
        {
            App.PendingRequestNotifications?.TraceDecision("command_busy");
            return Notifications.PendingNotificationActionResult.Busy;
        }
        RevealRequest(item);
        // Revealing a request can synchronously change selection through UI bindings.
        // Never let a notification authorize whichever request became selected instead.
        if (!IsAvailable() || !ReferenceEquals(Selected, item))
        {
            App.PendingRequestNotifications?.TraceDecision("selection_or_state_changed");
            return Notifications.PendingNotificationActionResult.Unavailable;
        }
        if (IsExecuting || ApproveCommand.IsRunning || RejectCommand.IsRunning)
            return Notifications.PendingNotificationActionResult.Busy;
        _notificationApprovedItem = item;
        try { await ApproveCommand.ExecuteAsync(null); }
        finally { _notificationApprovedItem = null; }
        return item.State != "承認待ち" ? Notifications.PendingNotificationActionResult.Completed
            : Notifications.PendingNotificationActionResult.Unavailable;
    }

    public async Task<Notifications.PendingNotificationActionResult> RejectNotificationAsync(InboxItem item)
    {
        bool IsAvailable() => !_closing && Requests.Contains(item) && IsConnectionConfirmed &&
            ReferenceEquals(item.Transport, _transport) && _connection is { IsCancellationRequested: false } &&
            item.State == "承認待ち";
        if (!IsAvailable())
        {
            TraceUnavailableNotification(item);
            return Notifications.PendingNotificationActionResult.Unavailable;
        }
        if (IsExecuting || ApproveCommand.IsRunning || RejectCommand.IsRunning)
            return Notifications.PendingNotificationActionResult.Busy;
        RevealRequest(item);
        if (!IsAvailable() || !ReferenceEquals(Selected, item))
            return Notifications.PendingNotificationActionResult.Unavailable;
        if (IsExecuting || ApproveCommand.IsRunning || RejectCommand.IsRunning)
            return Notifications.PendingNotificationActionResult.Busy;
        await RejectCommand.ExecuteAsync(null);
        return item.State != "承認待ち" ? Notifications.PendingNotificationActionResult.Completed
            : Notifications.PendingNotificationActionResult.Unavailable;
    }

    private void TraceUnavailableNotification(InboxItem item)
    {
        var reason = _closing ? "closing" : !Requests.Contains(item) ? "missing_item" :
            !IsConnectionConfirmed ? "disconnected" : !ReferenceEquals(item.Transport, _transport) ? "transport_changed" :
            _connection is not { IsCancellationRequested: false } ? "connection_canceled" : "already_resolved";
        App.PendingRequestNotifications?.TraceDecision(reason);
    }

    private bool CanSendResult() => !_closing && !IsExecuting && IsConnectionConfirmed && Selected is { ResultText.Length: > 0, AllowsResultSharing: true } item && ReferenceEquals(item.Transport, _transport);

    [RelayCommand(CanExecute = nameof(CanSendResult))]
    private async Task SendResultAsync()
    {
        var item = Selected;
        if (item is null || !CanSendResult()) return;
        var text = BoundedReply(item.ResultText);
        IsExecuting = true;
        _activeItem = item;
        try
        {
            _execution = CancellationTokenSource.CreateLinkedTokenSource(_connection!.Token);
            CancelCommand.NotifyCanExecuteChanged();
            var token = _execution.Token;
            if (!await CheckAccessAsync(item, token)) return;
            if (ConfirmReplyAsync is null || !await ConfirmReplyAsync(item, text) || token.IsCancellationRequested) return;
            if (!await CheckAccessAsync(item, token)) return;
            await item.Transport.ReplyAsync(item.Request, text, token);
            await AuditAsync("reply_sent", item.Request, item.ExecutionId);
            Status = DesktopText.Get("InboxResultSent");
        }
        catch (OperationCanceledException) { Status = DesktopText.Get("InboxResultSendCancelled"); }
        catch (Exception) { Status = DesktopText.Get("InboxResultSendFailed"); }
        finally { _execution?.Dispose(); _execution = null; _activeItem = null; IsExecuting = false; }
    }

    private Task AuditAsync(string kind, ChatRequest request, Guid? executionId, CancellationToken token = default) =>
        _audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, executionId, request.Provider, request.ChannelId, request.ThreadId), token);

    // The receive path may backpressure on its bounded queue, but never waits for a disk write.
    private static ValueTask QueueReceivedAuditAsync(ReceivedAuditQueue queue, ChatRequest request, CancellationToken token)
    {
        var record = new AuditEvent(DateTimeOffset.UtcNow, "received", null, request.Provider,
            request.ChannelId, request.ThreadId);
        return queue.EnqueueAsync(record, token);
    }

    private void ReportReceivedAuditFailure(CancellationTokenSource connection)
    {
        void Report()
        {
            if (!ReferenceEquals(_connection, connection) || _closing) return;
            var warning = DesktopText.Get("InboxAuditSaveFailed");
            if (!Status.Contains(warning, StringComparison.Ordinal)) Status = (Status + " " + warning).Trim();
        }
        if (App.DispatcherQueue.HasThreadAccess) Report();
        else App.DispatcherQueue.TryEnqueue(Report);
    }

    private async Task<AppServerDecision> HandleApprovalAsync(InboxItem item, AppServerApproval approval, CancellationToken token)
    {
        if (!await CheckAccessAsync(item, token)) return AppServerDecision.Decline;
        var settings = Volatile.Read(ref _accessSettings);
        var decision = await _approvalBroker.RequestAsync(item.Transport, item.Request, approval, ServerApprovalHandler, token,
            publishToThread: settings?.ShareApprovalsInChat == true,
            canDecideInChat: async ct =>
            {
                var current = await _store.LoadAsync(ct);
                return current.ShareApprovalsInChat && ReferenceEquals(item.Transport, _transport) &&
                    HasAccess(current, item.Request, item.Settings);
            });
        if (decision == AppServerDecision.Accept && !await CheckAccessAsync(item, token)) return AppServerDecision.Decline;
        try { await AuditAsync(decision == AppServerDecision.Accept ? "approved" : "rejected", item.Request, item.ExecutionId, token); }
        catch (Exception) { return AppServerDecision.Decline; }
        return decision;
    }

    private static SessionKey CreateSessionKey(InboxItem item) => ChatSessionScope.Create(
        item.Request.Provider, item.Request.ChannelId, item.Request.ThreadId, item.Request.UserId,
        item.Request.Provider == "mattermost" ? item.Settings.MattermostServerUrl : null);

    private static bool HasAccess(BridgeSettings current, ChatRequest request, BridgeSettings received)
    {
        return CanReceive(current, request, received) && SameExecutionDefaults(current, received);
    }

    private static bool CanReceive(BridgeSettings current, ChatRequest request, BridgeSettings connected) =>
        ChatAccessPolicy.IsAllowedRequest(current, request.Provider, request.UserId, request.ChannelId, request.ThreadId, request.GuildId) &&
        (request.Provider != "mattermost" || string.Equals(current.MattermostChannelId, request.ChannelId, StringComparison.Ordinal)) &&
        ChatConnectionPolicy.Matches(request.Provider, current, connected);

    public async Task RefreshAccessAsync()
    {
        await _accessRefreshGate.WaitAsync();
        try
        {
            var settings = await _store.LoadAsync();
            if (!IsValidInboxWorkspacePath(settings.WorkspaceRoot))
            {
                ReportInvalidWorkspaceSettings();
                return;
            }
            var previous = Volatile.Read(ref _accessSettings);
            Volatile.Write(ref _accessSettings, settings);
            if (_transport is IChatLiveAccessSettings live) live.UpdateAccessSettings(settings);
            if (_attachmentTransferActive && previous is not null && !SameAttachmentPolicy(previous, settings))
                RequestExecutionCancellation();
            foreach (var item in Requests.Where(item => !HasAccess(settings, item.Request, item.Settings)))
            {
                if (item.State == "承認待ち") item.State = "許可取り消し・実行不可";
                item.ResultText = "";
            }
            if (_activeItem is { } active && !HasAccess(settings, active.Request, active.Settings)) RequestExecutionCancellation();
            if (IsConnected && _connectionSettings is { } connected && ConnectionProvider is { } provider &&
                !ChatConnectionPolicy.Matches(provider, settings, connected))
            {
                // Old credentials or a different server/channel must never remain a green,
                // apparently usable connection after the settings have been saved.
                IsConnectionConfirmed = false;
                await DisconnectAsync();
                HasConnectionError = true;
                Status = DesktopText.Get("InboxConnectionSettingsChanged");
            }
            else if (IsConnected && previous is not null && !SameExecutionDefaults(previous, settings))
                Status = DesktopText.Get("InboxExecutionSettingsChanged");
        }
        catch (Exception)
        {
            Volatile.Write(ref _accessSettings, null);
            RequestExecutionCancellation();
            foreach (var item in Requests) { if (item.State == "承認待ち") item.State = "許可設定を確認できず実行不可"; item.ResultText = ""; }
            Status = DesktopText.Get("InboxPermissionsUnavailable");
            HasConnectionError = true;
        }
        finally { _accessRefreshGate.Release(); }
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        SendResultCommand.NotifyCanExecuteChanged();
        SendAttachmentsCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> CheckAccessAsync(InboxItem item, CancellationToken token)
    {
        var settings = await _store.LoadAsync(token);
        Volatile.Write(ref _accessSettings, settings);
        var allowed = IsValidInboxWorkspacePath(settings.WorkspaceRoot) && HasAccess(settings, item.Request, item.Settings);
        if (allowed && item.ExecutionPolicyFingerprint is { } fingerprint)
        {
            var key = CreateSessionKey(item);
            var binding = await _sessions.FindExecutionBindingAsync(key, token);
            var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(key, settings, token), binding);
            allowed = fingerprint == ConversationExecutionPolicy.GetFingerprint(key, effective);
        }
        if (allowed) return true;
        App.DispatcherQueue.TryEnqueue(() =>
        {
            item.State = "許可取り消し・実行不可";
            item.ResultText = "";
            Status = DesktopText.Get("InboxExecutionPausedSettingsChanged");
        });
        return false;
    }

    private async Task NotifyRequestAsync(InboxItem item, string message)
    {
        if (_closing || !IsConnectionConfirmed || !ReferenceEquals(item.Transport, _transport)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await item.Transport.ReplyAsync(item.Request, message, timeout.Token);
        }
        catch (Exception) { Status += " " + DesktopText.Get("InboxStatusNoticeFailed"); }
    }

    private async Task NotifyConnectionAsync(IChatTransport transport, BridgeSettings settings, string message)
    {
        if (transport is not IChatConnectionNotifications notifications) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // A save can finish before RefreshAccessAsync updates its in-memory
            // snapshot. Re-read the notice preference so turning it off takes
            // effect immediately, while retaining the active connection target.
            if (!(await _store.LoadAsync(timeout.Token)).PostConnectionNotice) return;
            await notifications.NotifyConnectionAsync(settings, message, timeout.Token);
        }
        catch (Exception) { if (!_closing) Status += " " + DesktopText.Get("InboxConnectionNoticeFailed"); }
    }

    private async Task FinishFailureAsync(ExecutionRecord record, string status, ChatRequest request)
    {
        try
        {
            await _history.RecordAsync(record with { FinishedAt = DateTimeOffset.UtcNow, Status = status });
            await AuditAsync(status, request, record.Id);
        }
        catch (Exception) { Status += " " + DesktopText.Get("InboxAuditSaveFailed"); }
    }

    private static string ExtractReply(AgentRunResult result)
    {
        if (result.Claude is { } claude) return claude.FinalMessage ?? "";
        if (result.CliSession is { } cliSession) return cliSession.FinalMessage ?? "";
        var parsed = result.Codex ?? CodexOutputParser.Parse(result.Output);
        return parsed.FinalMessage ?? (parsed.HasEvents ? "" : result.Output);
    }

    private async Task ProcessPendingPoliciesAsync()
    {
        if (_processingPolicies || IsExecuting || !IsConnectionConfirmed || _closing) return;
        _processingPolicies = true;
        try
        {
            await Task.Yield();
            if (IsExecuting || !IsConnectionConfirmed || _closing) return;
            // New requests can arrive while an earlier execution awaits the CLI.
            // Take snapshots and serialize all automatic execution through the same command.
            foreach (var item in Requests.ToArray())
            {
                if (_closing || !IsConnectionConfirmed || IsExecuting) break;
                if (item.State != "承認待ち" || _automaticallyAttempted.Contains(item)) continue;
                if (!await CheckAccessAsync(item, _connection?.Token ?? CancellationToken.None)) continue;
                if (WorkspaceChatCommand.TryParse(item.Prompt, out _) || AgentChatCommand.TryParse(item.Prompt, out _))
                {
                    NotifyPendingRequest(item);
                    continue;
                }
                var key = CreateSessionKey(item);
                var binding = await _sessions.FindExecutionBindingAsync(key);
                var effective = ConversationExecutionPolicy.GetEffectiveSettings(await _sessions.ApplyWorkspaceAsync(key, item.Settings), binding);
                item.Workspace = effective.WorkspaceRoot;
                if (ConversationExecutionPolicy.RequiresApproval(key, effective, binding) ||
                    (await _sessions.FindAgentSelectionAsync(key))?.RequiresApproval == true)
                {
                    NotifyPendingRequest(item);
                    continue;
                }
                _automaticallyAttempted.Add(item);
                if (_notifiedRequests.Remove(item))
                {
                    // First-turn consent can authorize replies that arrived while it was pending.
                    // Resolve their obsolete approval toasts before starting them automatically.
                    item.State = "自動実行を準備";
                    item.State = "承認待ち";
                }
                if (!Requests.Contains(item) || !ReferenceEquals(item.Transport, _transport)) continue;
                RevealRequest(item);
                _automaticallyApprovedItem = item;
                try
                {
                    if (CanApprove()) await ApproveCommand.ExecuteAsync(null);
                    else _automaticallyAttempted.Remove(item);
                }
                finally { _automaticallyApprovedItem = null; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Status = DesktopText.Get("InboxAutomaticExecutionPaused");
            foreach (var item in Requests.Where(item => item.State == "承認待ち"))
                NotifyPendingRequest(item);
        }
        finally
        {
            _processingPolicies = false;
            if (!_closing && !IsExecuting && IsConnectionConfirmed && Requests.Any(item => item.State == "承認待ち" &&
                !_notifiedRequests.Contains(item) && !_automaticallyAttempted.Contains(item)))
            {
                await Task.Yield();
                _ = ProcessPendingPoliciesAsync();
            }
        }
    }

    private static bool SameExecutionDefaults(BridgeSettings left, BridgeSettings right) =>
        left.WorkspaceRoot == right.WorkspaceRoot && left.AgentExecutable == right.AgentExecutable &&
        left.AgentArguments == right.AgentArguments && left.AgentMode == right.AgentMode && left.AgentProvider == right.AgentProvider &&
        left.AgentModel == right.AgentModel && left.AgentReasoningEffort == right.AgentReasoningEffort &&
        left.AgentPermissionMode == right.AgentPermissionMode && left.AllowAgentNetwork == right.AllowAgentNetwork &&
        left.TrustedWorkspaceRoots == right.TrustedWorkspaceRoots && left.ChatApprovalMode == right.ChatApprovalMode &&
        left.ChatResultMode == right.ChatResultMode && left.ChatFollowUpMode == right.ChatFollowUpMode && left.ShareApprovalsInChat == right.ShareApprovalsInChat &&
        left.ShareAgentQuestionsInChat == right.ShareAgentQuestionsInChat;

    private static string BoundedReply(string text) => text.Length > 4000
        ? text[..4000] + Environment.NewLine + DesktopText.Get("InboxLongTextTruncated") : text;

    private static string BoundOutput(string text)
    {
        if (text.Length <= MaxDisplayedOutputCharacters) return text;
        var suffix = Environment.NewLine + DesktopText.Get("InboxOutputDisplayTruncated");
        return text[..(MaxDisplayedOutputCharacters - suffix.Length)] + suffix;
    }

    private static string ApprovalLabel(string mode) => mode switch { "auto" => DesktopText.Get("Auto"), "first" => DesktopText.Get("AtStartupOnly"), _ => DesktopText.Get("EveryTime") };
    private static string ResultLabel(string mode) => mode switch { "auto" => DesktopText.Get("AutoReply"), "none" => DesktopText.Get("NoSharing"), _ => DesktopText.Get("ManualShareShort") };
    private static string PermissionLabel(string mode) => mode switch
    {
        "read-only" => DesktopText.Get("ReadAndPlanOnly"), "workspace-write" => DesktopText.Get("AutoAllowEdits"),
        "full-access" => DesktopText.Get("FullAccess"), "ask" => DesktopText.Get("AskPerAction"), _ => DesktopText.Get("CliDefault")
    };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (CanCancel()) RequestExecutionCancellation();
    }

    private void RequestExecutionCancellation()
    {
        var cancellation = _execution;
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        if (_activeItem is { } active)
            active.LiveOutput.IsCancellationPending = true;
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        IsConnectionConfirmed = false;
        RequestExecutionCancellation();
        _connection?.Cancel();
        foreach (var item in Requests) item.LiveOutput.Dispose();
        _emptyLiveOutput.Dispose();
    }

    internal async Task WaitForConnectionShutdownAsync()
    {
        if (_receiveTask is { } receive) await receive;
        if (_conversationRestoreTask is { } restore) await restore;
        await _conversationSaveTask;
    }
}
