using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.UsageUi;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.History;

public sealed record ModelExecutionSelection(string? Model, string? ReasoningEffort);
public sealed record SessionPolicySelection(string? ChatApprovalMode, string? ChatResultMode, string? AgentPermissionMode);

public sealed record ExecutionHistoryRow(ExecutionRecord Record)
{
    public string Started => Record.StartedAt.ToLocalTime().ToString("g");
    public string Source => SourceLabel(Record.Source);
    public string Status => Record.Status switch
    {
        "running" => DesktopText.Get("HistoryExecutionRunning"),
        "completed" => DesktopText.Get("HistoryExecutionCompleted"),
        "failed" => DesktopText.Get("HistoryExecutionFailed"),
        "cancelled" => DesktopText.Get("HistoryExecutionCancelled"),
        "interrupted" => DesktopText.Get("HistoryExecutionInterrupted"),
        _ => Record.Status
    };
    public string Summary => $"{Started} · {Source} · {Status}";
    public string Workspace => DesktopText.Get("HistoryWorkspace", Record.WorkspaceRoot);
    public string Session => DesktopText.Get("HistorySessionId", Record.SessionId ?? "—");
    public string Model => DesktopText.Get("HistoryModel", Record.AgentModel ?? DesktopText.Get("DefaultModel"));
    public string ReasoningEffort => DesktopText.Get("HistoryReasoning", Record.AgentReasoningEffort ?? DesktopText.Get("CliDefault"));
    public string Usage => UsageSummaryFormatter.Format(Record.Usage, Record.Metrics);
    public string Conversation => DesktopText.Get("HistoryExecutionConversation", Record.ChannelId, Record.ThreadId, Record.UserId);

    private static string SourceLabel(string source) => source switch
    {
        "local" => DesktopText.Get("Local"),
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        _ => string.IsNullOrWhiteSpace(source) ? DesktopText.Get("UnknownProvider") : source
    };
}

public sealed record AuditHistoryRow(AuditEvent Record)
{
    public string Timestamp => Record.Timestamp.ToLocalTime().ToString("g");
    public string Kind => Record.Kind switch
    {
        "received" => DesktopText.Get("AuditReceived"),
        "approved" => DesktopText.Get("AuditApproved"),
        "rejected" => DesktopText.Get("AuditRejected"),
        "started" => DesktopText.Get("AuditStarted"),
        "completed" => DesktopText.Get("AuditCompleted"),
        "failed" => DesktopText.Get("AuditFailed"),
        "cancelled" => DesktopText.Get("AuditCancelled"),
        "reply_failed" => DesktopText.Get("AuditReplyFailed"),
        "reply_sent" => DesktopText.Get("AuditReplySent"),
        "detached" => DesktopText.Get("AuditDetached"),
        "workspace_changed" => DesktopText.Get("AuditWorkspaceChanged"),
        _ => DesktopText.Get("AuditOther")
    };
    public string Source => Record.Source switch
    {
        "local" => DesktopText.Get("Local"),
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        _ => DesktopText.Get("UnknownProvider")
    };
    public string Summary => $"{Timestamp} · {Source} · {Kind}";
    public string Conversation => DesktopText.Get("HistoryAuditConversation", Record.ChannelId, Record.ThreadId);
}

public sealed record SessionHistoryRow(SessionBinding Binding)
{
    public string Provider => Binding.Key.Provider switch
    {
        "mattermost" => "Mattermost",
        "slack" => "Slack",
        "local" => DesktopText.Get("Local"),
        _ => DesktopText.Get("UnknownProvider")
    };
    public string ChannelId => Binding.Key.ChannelId;
    public string ThreadId => Binding.Key.ThreadId;
    public string Workspace => Binding.WorkspaceRoot;
    public string Agent => Binding.AgentArguments == "app-server"
        ? "Codex App Server"
        : $"{Binding.AgentExecutable} {Binding.AgentArguments}".Trim();
    public string SessionId => Binding.SessionId;
    public string UserId => Binding.Key.UserId ?? DesktopText.Get("LegacyUnknownOwner");
    public string Origin => Binding.Key.Origin ?? DesktopText.Get("HistoryUnknownOrigin");
    public string WorkspaceName => Path.GetFileName(Binding.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name : Binding.WorkspaceRoot;
    public string ConversationLabel => DesktopText.Get("HistorySessionConversation", ChannelId, ThreadId, Environment.NewLine, UserId);
    public string Conversation => ConversationLabel;
    public string ListTitle => $"{Provider} · {UserId}";
    public string ListLocation => DesktopText.Get("ConversationLocation", ChannelId, ThreadId);
    public override string ToString() => $"{ListTitle} · {WorkspaceName} · {ListLocation} · {Updated}";
    public string AgentAndSession => $"{Agent} / {SessionId}";
    public string ModelOverride => Binding.Model ?? string.Empty;
    public string Model => string.IsNullOrWhiteSpace(Binding.Model)
        ? DesktopText.Get("HistoryNextModelDefault")
        : DesktopText.Get("HistoryNextModel", Binding.Model);
    public string ReasoningEffort => DesktopText.Get("HistoryNextReasoning", Binding.ReasoningEffort ?? DesktopText.Get("InheritAgentEffort"));
    public string ExecutionPolicy => DesktopText.Get("HistoryExecutionPolicy", PolicyLabel(Binding.ChatApprovalMode, DesktopText.Get("AskEveryTime"), "first", DesktopText.Get("AskFirstTime"), "auto", DesktopText.Get("AutoApprove")), PolicyLabel(Binding.ChatResultMode, DesktopText.Get("ManualShare"), "auto", DesktopText.Get("AutoShare"), "none", DesktopText.Get("NoSharing")), PermissionLabel(Binding.AgentPermissionMode));
    public string Updated => Binding.UpdatedAt.ToLocalTime().ToString("g");
    public string SearchIndex => string.Join("\n", Provider, ChannelId, ThreadId, UserId, Workspace, SessionId, Agent, ModelOverride, ReasoningEffort, ExecutionPolicy, Origin);

    private static string PolicyLabel(string? value, string defaultLabel, string keyOne, string labelOne, string keyTwo, string labelTwo) =>
        value is null ? DesktopText.Get("GlobalSetting") : value == keyOne ? labelOne : value == keyTwo ? labelTwo : defaultLabel;
    private static string PermissionLabel(string? value) => value switch
    {
        null => DesktopText.Get("GlobalSetting"), "read-only" => DesktopText.Get("ReadAndPlanOnly"), "workspace-write" => DesktopText.Get("AutoAllowWorkspaceEdits"),
        "full-access" => DesktopText.Get("FullAccess"), "ask" => DesktopText.Get("AskPerAction"), _ => DesktopText.Get("CliDefault")
    };
}

public sealed partial class HistoryPageViewModel : ObservableObject
{
    private readonly ExecutionHistoryStore _executionStore = new();
    private readonly AuditStore _auditStore = new();
    private readonly SessionStore _sessionStore = new();
    private readonly SettingsStore _settingsStore = new();

    public ObservableCollection<ExecutionHistoryRow> Executions { get; } = [];
    public ObservableCollection<AuditHistoryRow> AuditEvents { get; } = [];
    public ObservableCollection<SessionHistoryRow> Sessions { get; } = [];
    public ObservableCollection<SessionHistoryRow> FilteredSessions { get; } = [];
    public IReadOnlyList<string> ProviderFilters { get; } = [DesktopText.Get("HistoryProviderAll"), "Slack", "Mattermost", DesktopText.Get("Local")];
    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial string ProviderFilter { get; set; } = DesktopText.Get("HistoryProviderAll");
    [ObservableProperty] public partial bool IsNarrow { get; set; }
    [ObservableProperty] public partial bool HasExecutions { get; set; }
    [ObservableProperty] public partial bool HasSessions { get; set; }
    [ObservableProperty] public partial bool HasFilteredSessions { get; set; }
    [ObservableProperty] public partial bool HasAuditEvents { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand))]
    public partial SessionHistoryRow? SelectedSession { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = DesktopText.Get("HistoryLoading");

    public Func<SessionHistoryRow, Task<bool>>? ConfirmDetachAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<ModelExecutionSelection?>>? EditSessionModelAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<SessionPolicySelection?>>? EditSessionPolicyAsync { get; set; }

    public string ConversationsTabTitle => DesktopText.Get(IsNarrow ? "ActivityChatsTabCompact" : "HistoryConversationsTab", Sessions.Count);
    public string ExecutionsTabTitle => DesktopText.Get(IsNarrow ? "ActivityRunsTabCompact" : "HistoryExecutionsTab", Executions.Count);
    public string AuditTabTitle => DesktopText.Get("HistoryAuditTab", AuditEvents.Count);
    public string SearchLabel => DesktopText.Get("HistorySearchLabel");
    public string SearchPlaceholder => DesktopText.Get("HistorySearchPlaceholder");
    public string ProviderLabel => DesktopText.Get("HistoryProviderLabel");
    public string SessionResultSummary => DesktopText.Get("HistorySessionResults", FilteredSessions.Count, Sessions.Count);
    public string SessionListEmptyText => Sessions.Count == 0 ? DesktopText.Get("HistoryNoSessions") : DesktopText.Get("HistoryNoSessionMatches");
    public string SelectedSessionTitle => SelectedSession is null ? DesktopText.Get("HistorySelectConversation") :
        DesktopText.Get("HistorySelectedSessionTitle", SelectedSession.Provider, SelectedSession.WorkspaceName);
    public string NoSessionSelectedText => DesktopText.Get("HistorySelectConversationHint");
    public string ChannelIdLabel => DesktopText.Get("HistoryChannelIdLabel");
    public string ThreadIdLabel => DesktopText.Get("HistoryThreadIdLabel");
    public string UserIdLabel => DesktopText.Get("HistoryUserIdLabel");
    public string OriginLabel => DesktopText.Get("HistoryOriginLabel");
    public string WorkspacePathLabel => DesktopText.Get("HistoryWorkspacePathLabel");
    public string AgentLabel => DesktopText.Get("HistoryAgentLabel");
    public string SessionIdLabel => DesktopText.Get("HistorySessionIdLabel");
    public string BackToConversationsLabel => DesktopText.Get("HistoryBackToConversations");
    public string ContinueInOriginalThreadHint => DesktopText.Get("HistoryContinueInOriginalThread");
    public string ConversationHeading => DesktopText.Get("HistoryConversationHeading");
    public string RuntimeHeading => DesktopText.Get("HistoryRuntimeHeading");
    public string PolicyHeading => DesktopText.Get("HistoryPolicyHeading");
    public string IdentifiersHeading => DesktopText.Get("HistoryIdentifiersHeading");
    public string ActionsHeading => DesktopText.Get("HistoryActionsHeading");
    public string EditModelLabel => DesktopText.Get("HistoryEditNextModel");
    public string EditPolicyLabel => DesktopText.Get("HistoryEditPolicy");
    public string StartFreshLabel => DesktopText.Get("HistoryStartFresh");
    public string ExecutionSummary => ExecutionDayFilter is { } date
        ? DesktopText.Get("ActivityFilteredExecutions", date.ToString("D"), Executions.Count)
        : DesktopText.Get("HistoryExecutionSummary", Executions.Count);
    public string NoExecutionsText => DesktopText.Get("HistoryNoExecutions");
    public string NoAuditText => DesktopText.Get("HistoryNoAudit");

    partial void OnSearchQueryChanged(string value) => ApplySessionFilter();
    partial void OnIsNarrowChanged(bool value) => NotifySummariesChanged();
    partial void OnProviderFilterChanged(string value) => ApplySessionFilter();
    partial void OnSelectedSessionChanged(SessionHistoryRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(SelectedSessionTitle));
    }
    public bool HasSelectedSession => SelectedSession is not null;

    private void NotifySummariesChanged()
    {
        OnPropertyChanged(nameof(ConversationsTabTitle));
        OnPropertyChanged(nameof(ExecutionsTabTitle));
        OnPropertyChanged(nameof(AuditTabTitle));
        OnPropertyChanged(nameof(SessionResultSummary));
        OnPropertyChanged(nameof(SessionListEmptyText));
        OnPropertyChanged(nameof(ExecutionSummary));
    }

    private void ApplySessionFilter()
    {
        var selectedKey = SelectedSession?.Binding.Key;
        var query = SearchQuery.Trim();
        var provider = ProviderFilter;
        var matches = Sessions.Where(row =>
            (provider == DesktopText.Get("HistoryProviderAll") || row.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || row.SearchIndex.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static row => row.Binding.UpdatedAt).ToArray();

        FilteredSessions.Clear();
        foreach (var row in matches) FilteredSessions.Add(row);
        HasFilteredSessions = matches.Length > 0;
        SelectedSession = selectedKey is null ? null : matches.FirstOrDefault(row => row.Binding.Key == selectedKey);
        OnPropertyChanged(nameof(SessionResultSummary));
        OnPropertyChanged(nameof(SessionListEmptyText));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = DesktopText.Get("HistoryRefreshing");
        try
        {
            var executions = await _executionStore.ListAsync();
            var sessions = await _sessionStore.ListAsync();
            var auditEvents = await _auditStore.ListAsync();

            var selectedKey = SelectedSession?.Binding.Key;
            SetExecutionRecords(executions);
            Sessions.Clear();
            foreach (var binding in sessions.OrderByDescending(static item => item.UpdatedAt)) Sessions.Add(new SessionHistoryRow(binding));
            AuditEvents.Clear();
            foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
                AuditEvents.Add(new AuditHistoryRow(audit));
            HasExecutions = Executions.Count > 0;
            HasSessions = Sessions.Count > 0;
            HasAuditEvents = AuditEvents.Count > 0;
            SelectedSession = selectedKey is null ? null : Sessions.FirstOrDefault(row => row.Binding.Key == selectedKey);
            ApplySessionFilter();
            NotifySummariesChanged();
            Status = DesktopText.Get("HistoryCounts", Executions.Count, Sessions.Count, AuditEvents.Count);
        }
        catch (Exception ex)
        {
            Status = DesktopText.Get("HistoryLoadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDetachSelectedSession))]
    private async Task DetachSelectedSessionAsync()
    {
        var selected = SelectedSession;
        if (selected is null || IsBusy) return;
        IsBusy = true;
        var auditFailed = false;
        string successMessage;
        try
        {
            if (ConfirmDetachAsync is null || !await ConfirmDetachAsync(selected)) return;
            Status = DesktopText.Get("HistoryDetaching");
            await _sessionStore.RemoveAsync(selected.Binding.Key);
            try
            {
                var key = selected.Binding.Key;
                await _auditStore.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "detached", null,
                    key.Provider, key.ChannelId, key.ThreadId), CancellationToken.None);
            }
            catch { auditFailed = true; }

            successMessage = auditFailed
                ? DesktopText.Get("HistoryDetachedAuditFailed")
                : DesktopText.Get("HistoryDetached");
            try
            {
                await RefreshContentsAsync(successMessage);
            }
            catch
            {
                Status = DesktopText.Get("HistoryRefreshAfterDetachFailed", successMessage);
            }
        }
        catch (Exception ex)
        {
            Status = DesktopText.Get("HistoryDetachFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;
    private bool CanDetachSelectedSession() => SelectedSession is not null && !IsBusy;
    private bool CanEditSelectedSessionModel() => SelectedSession is not null && !IsBusy;
    private bool CanEditSelectedSessionPolicy() => SelectedSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEditSelectedSessionPolicy))]
    private async Task EditSelectedSessionPolicyAsync()
    {
        var selected = SelectedSession;
        if (selected is null || IsBusy || EditSessionPolicyAsync is null) return;
        IsBusy = true;
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var selection = await EditSessionPolicyAsync(selected, settings);
            if (selection is null) return;
            await _sessionStore.UpdateExecutionPolicyAsync(selected.Binding.Key, selection.ChatApprovalMode,
                selection.ChatResultMode, selection.AgentPermissionMode, CancellationToken.None);
            await RefreshContentsAsync(DesktopText.Get("HistoryPolicySaved"));
            SelectedSession = Sessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch (Exception ex)
        {
            Status = DesktopText.Get("HistoryPolicySaveFailed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedSessionModel))]
    private async Task EditSelectedSessionModelAsync()
    {
        var selected = SelectedSession;
        if (selected is null || IsBusy || EditSessionModelAsync is null) return;
        IsBusy = true;
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var selection = await EditSessionModelAsync(selected, settings);
            if (selection is null) return;
            await _sessionStore.UpdateModelAndEffortAsync(selected.Binding.Key,
                string.IsNullOrWhiteSpace(selection.Model) ? null : selection.Model.Trim(),
                selection.ReasoningEffort, CancellationToken.None);
            await RefreshContentsAsync(DesktopText.Get("HistoryModelSaved"));
            SelectedSession = Sessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch
        {
            Status = DesktopText.Get("HistoryModelSaveFailed");
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshContentsAsync(string successMessage)
    {
        var executions = await _executionStore.ListAsync();
        var sessions = await _sessionStore.ListAsync();
        var auditEvents = await _auditStore.ListAsync();
        var selectedKey = SelectedSession?.Binding.Key;
        SetExecutionRecords(executions);
        Sessions.Clear();
        foreach (var binding in sessions.OrderByDescending(static item => item.UpdatedAt)) Sessions.Add(new SessionHistoryRow(binding));
        AuditEvents.Clear();
        foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
            AuditEvents.Add(new AuditHistoryRow(audit));
        HasExecutions = Executions.Count > 0;
        HasSessions = Sessions.Count > 0;
        HasAuditEvents = AuditEvents.Count > 0;
        SelectedSession = selectedKey is null ? null : Sessions.FirstOrDefault(row => row.Binding.Key == selectedKey);
        ApplySessionFilter();
        NotifySummariesChanged();
        Status = DesktopText.Get("HistorySuccessCounts", successMessage, Executions.Count, Sessions.Count, AuditEvents.Count);
    }
}
