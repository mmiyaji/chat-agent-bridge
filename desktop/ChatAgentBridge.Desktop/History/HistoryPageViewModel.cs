using System.Collections.ObjectModel;
using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.UsageUi;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.History;

public sealed record ModelExecutionSelection(string? Model, string? ReasoningEffort);
public sealed record SessionPolicySelection(string? ChatApprovalMode, string? ChatResultMode, string? AgentPermissionMode);

public sealed record ExecutionHistoryRow(ExecutionRecord Record, string? ChatLabel = null)
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
    public string Conversation => ChatLabel ?? (Record.Source == "local" ? DesktopText.Get("HistoryThisPc") : DesktopText.Get("HistoryChatNamesUnavailable"));
    public string Identifiers => DesktopText.Get("HistoryExecutionConversation", Record.ChannelId, Record.ThreadId, Record.UserId);
    public string IdentifiersHeading => DesktopText.Get("HistoryIdentifiersHeading");

    private static string SourceLabel(string source) => source switch
    {
        "local" => DesktopText.Get("Local"),
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        _ => string.IsNullOrWhiteSpace(source) ? DesktopText.Get("UnknownProvider") : source
    };
}

public sealed record AuditHistoryRow(AuditEvent Record, string? ChatLabel = null)
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
        "attachment_approved" => DesktopText.Get("AuditAttachmentApproved"),
        "attachment_sent" => DesktopText.Get("AuditAttachmentSent"),
        "attachment_failed" => DesktopText.Get("AuditAttachmentFailed"),
        "detached" => DesktopText.Get("AuditDetached"),
        "workspace_changed" => DesktopText.Get("AuditWorkspaceChanged"),
        "model_changed" => DesktopText.Get("AuditModelChanged"),
        "agent_changed" => DesktopText.Get("AgentSwitchCompleted"),
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
    public string Conversation => ChatLabel ?? (Record.Source == "local" ? DesktopText.Get("HistoryThisPc") : DesktopText.Get("HistoryChatNamesUnavailable"));
    public string Identifiers => DesktopText.Get("HistoryAuditConversation", Record.ChannelId, Record.ThreadId);
    public string IdentifiersHeading => DesktopText.Get("HistoryIdentifiersHeading");
}

public sealed record SessionHistoryRow(SessionBinding Binding, int ExecutionCount = 0,
    string? LatestExecutionStatus = null, string? LatestExecutedModel = null, ChatIdentityLabels? ChatLabels = null)
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
    public string Agent => Binding.NextAgentProvider is { } next ? DesktopText.Get("AgentSwitchPending", next) : Binding.AgentArguments == "app-server"
        ? "Codex App Server"
        : $"{Binding.AgentExecutable} {Binding.AgentArguments}".Trim();
    public string SessionId => Binding.SessionId;
    public string UserId => Binding.Key.UserId ?? DesktopText.Get("LegacyUnknownOwner");
    public string Origin => Binding.Key.Origin ?? DesktopText.Get("HistoryUnknownOrigin");
    public string WorkspaceName => Path.GetFileName(Binding.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name : Binding.WorkspaceRoot;
    public string ConversationLabel => $"{ListTitle}\n{ListIdentity}\n{ListLocation}";
    public string Conversation => ConversationLabel;
    public string ListTitle => string.IsNullOrWhiteSpace(Binding.DisplayName) ? WorkspaceName : Binding.DisplayName;
    public string ListIdentity => $"{SiteLabel} · {UserLabel}";
    public string SiteLabel => ChatLabels?.SiteName is { Length: > 0 } site && site != Provider ? $"{Provider} / {site}" : Provider;
    public string UserLabel => Binding.Key.Provider == "local" ? DesktopText.Get("HistoryThisPc") :
        ChatLabels?.UserName is { Length: > 0 } name ? name : DesktopText.Get("HistoryUserNameUnavailable");
    public string ListContext => DesktopText.Get("HistoryConversationContext", WorkspaceName, AgentModelSummary);
    public string ListLocation => Binding.Key.Provider == "local" ? DesktopText.Get("HistoryThisPc") :
        ChatLabels?.IsDirectMessage == true ? DesktopText.Get("HistoryDirectMessage") :
        ChatLabels?.ChannelName is { Length: > 0 } channel ? $"#{channel.TrimStart('#')}" : DesktopText.Get("HistoryChannelNameUnavailable");
    public override string ToString() => $"{ListTitle} · {ListIdentity} · {ListLocation} · {ListContext} · {ActivitySummary} · {Updated}";
    public string AgentAndSession => $"{Agent} / {SessionId}";
    public string ModelOverride => Binding.Model ?? string.Empty;
    public string AgentModelSummary => Binding.NextAgentProvider is { } next ? DesktopText.Get("AgentSwitchPending", next) : DesktopText.Get("HistoryAgentModelSummary", AgentLabel,
        !string.IsNullOrWhiteSpace(Binding.Model) ? Binding.Model :
        !string.IsNullOrWhiteSpace(LatestExecutedModel) ? LatestExecutedModel : DesktopText.Get("DefaultModel"));
    public string ActivitySummary => ExecutionCount == 0
        ? DesktopText.Get("HistoryConversationNoRuns")
        : DesktopText.Get("HistoryActivitySummary", ExecutionStatusLabel(LatestExecutionStatus), ExecutionCount);
    public string StatusGlyph => LatestExecutionStatus switch
    {
        "completed" => "\uE73E",
        "failed" => "\uE783",
        "cancelled" => "\uE711",
        "running" => "\uE895",
        "interrupted" => "\uE7BA",
        _ => string.Empty
    };
    public string Model => string.IsNullOrWhiteSpace(Binding.Model)
        ? DesktopText.Get("HistoryNextModelDefault")
        : DesktopText.Get("HistoryNextModel", Binding.Model);
    public string ReasoningEffort => DesktopText.Get("HistoryNextReasoning", Binding.ReasoningEffort ?? DesktopText.Get("InheritAgentEffort"));
    public string ExecutionPolicy => DesktopText.Get("HistoryExecutionPolicy", PolicyLabel(Binding.ChatApprovalMode, DesktopText.Get("AskEveryTime"), "first", DesktopText.Get("AskFirstTime"), "auto", DesktopText.Get("AutoApprove")), PolicyLabel(Binding.ChatResultMode, DesktopText.Get("ManualShare"), "auto", DesktopText.Get("AutoShare"), "none", DesktopText.Get("NoSharing")), PermissionLabel(Binding.AgentPermissionMode));
    public string Updated => Binding.UpdatedAt.ToLocalTime().ToString("g");
    public string UpdatedCompact
    {
        get
        {
            var local = Binding.UpdatedAt.ToLocalTime();
            return local.Date == DateTime.Now.Date
                ? DesktopText.Get("HistoryUpdatedToday", local.ToString("HH:mm", CultureInfo.CurrentCulture))
                : local.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
        }
    }
    public string UpdatedTooltip => DesktopText.Get("HistoryUpdatedTooltip", Binding.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    public string SearchIndex => string.Join("\n", Binding.DisplayName, Provider, SiteLabel, UserLabel, ListLocation, ChannelId, ThreadId, UserId, WorkspaceName, Workspace,
        SessionId, AgentLabel, ModelOverride, LatestExecutedModel, ReasoningEffort, ExecutionPolicy, Origin);

    private string AgentLabel
    {
        get
        {
            if (string.Equals(Binding.AgentArguments, "app-server", StringComparison.Ordinal)) return "Codex App Server";
            var leaf = Path.GetFileName(Binding.AgentExecutable.Trim().Trim('"'));
            var name = Path.GetFileNameWithoutExtension(leaf);
            return name.ToLowerInvariant() switch
            {
                "codex" => "Codex",
                "claude" => "Claude Code",
                "gemini" => "Gemini CLI",
                "copilot" or "github-copilot" => "GitHub Copilot CLI",
                _ => string.IsNullOrWhiteSpace(name) ? DesktopText.Get("CustomCliName") : name
            };
        }
    }

    private static string ExecutionStatusLabel(string? status) => status switch
    {
        "running" => DesktopText.Get("HistoryExecutionRunning"),
        "completed" => DesktopText.Get("HistoryExecutionCompleted"),
        "failed" => DesktopText.Get("HistoryExecutionFailed"),
        "cancelled" => DesktopText.Get("HistoryExecutionCancelled"),
        "interrupted" => DesktopText.Get("HistoryExecutionInterrupted"),
        _ => DesktopText.Get("UnknownProvider")
    };

    private static string PolicyLabel(string? value, string defaultLabel, string keyOne, string labelOne, string keyTwo, string labelTwo) =>
        value is null ? DesktopText.Get("GlobalSetting") : value == keyOne ? labelOne : value == keyTwo ? labelTwo : defaultLabel;
    internal static string PermissionLabel(string? value) => value switch
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
    public ObservableCollection<string> WorkspaceFilters { get; } = [DesktopText.Get("HistoryWorkspaceAll")];
    public IReadOnlyList<string> SessionSortOptions { get; } =
        [DesktopText.Get("HistorySortRecent"), DesktopText.Get("HistorySortName"), DesktopText.Get("HistorySortUsage")];
    public IReadOnlyList<string> ProviderFilters { get; } = [DesktopText.Get("HistoryProviderAll"), "Slack", "Mattermost", DesktopText.Get("Local")];
    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial string ProviderFilter { get; set; } = DesktopText.Get("HistoryProviderAll");
    [ObservableProperty] public partial string WorkspaceFilter { get; set; } = DesktopText.Get("HistoryWorkspaceAll");
    [ObservableProperty] public partial string SessionSort { get; set; } = DesktopText.Get("HistorySortRecent");
    [ObservableProperty] public partial bool IsNarrow { get; set; }
    [ObservableProperty] public partial bool HasExecutions { get; set; }
    [ObservableProperty] public partial bool HasSessions { get; set; }
    [ObservableProperty] public partial bool HasFilteredSessions { get; set; }
    [ObservableProperty] public partial bool HasAuditEvents { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand)), NotifyCanExecuteChangedFor(nameof(RenameSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(SwitchSelectedAgentCommand))]
    public partial SessionHistoryRow? SelectedSession { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand)), NotifyCanExecuteChangedFor(nameof(RenameSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(ClearSessionFiltersCommand)), NotifyCanExecuteChangedFor(nameof(SwitchSelectedAgentCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = DesktopText.Get("HistoryLoading");

    public Func<SessionHistoryRow, Task<bool>>? ConfirmDetachAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<ModelExecutionSelection?>>? EditSessionModelAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<SessionPolicySelection?>>? EditSessionPolicyAsync { get; set; }
    public Func<SessionHistoryRow, Task<string?>>? RenameSessionAsync { get; set; }
    public Func<SessionHistoryRow, IReadOnlyList<AgentConnectionProfile>, ConversationSummary?, Task<AgentSwitchSelection?>>? ChooseAgentSwitchAsync { get; set; }

    public string ConversationsTabTitle => DesktopText.Get(IsNarrow ? "ActivityChatsTabCompact" : "HistoryConversationsTab", Sessions.Count);
    public string ExecutionsTabTitle => DesktopText.Get(IsNarrow ? "ActivityRunsTabCompact" : "HistoryExecutionsTab", Executions.Count);
    public string AuditTabTitle => DesktopText.Get("HistoryAuditTab", AuditEvents.Count);
    public string SearchLabel => DesktopText.Get("HistorySearchLabel");
    public string SearchPlaceholder => DesktopText.Get("HistorySearchPlaceholder");
    public string ProviderLabel => DesktopText.Get("HistoryProviderLabel");
    public string WorkspaceFilterLabel => DesktopText.Get("HistoryWorkspaceFilterLabel");
    public string SortLabel => DesktopText.Get("HistorySortLabel");
    public string ClearSessionFiltersLabel => DesktopText.Get("HistoryClearSessionFilters");
    public string RenameSessionLabel => DesktopText.Get("HistoryRenameSession");
    public bool HasSessionFilters => !string.IsNullOrWhiteSpace(SearchQuery) ||
        !string.Equals(ProviderFilter, DesktopText.Get("HistoryProviderAll"), StringComparison.Ordinal) ||
        !string.Equals(WorkspaceFilter, DesktopText.Get("HistoryWorkspaceAll"), StringComparison.Ordinal);
    public string SessionResultSummary => DesktopText.Get("HistorySessionResults", FilteredSessions.Count, Sessions.Count);
    public string SessionListEmptyText => Sessions.Count == 0 ? DesktopText.Get("HistoryNoSessions") : DesktopText.Get("HistoryNoSessionMatches");
    public string SelectedSessionTitle => SelectedSession?.ListTitle ?? DesktopText.Get("HistorySelectConversation");
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
    public string SwitchAgentLabel => DesktopText.Get("AgentSwitchTitle");
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
    partial void OnWorkspaceFilterChanged(string value) => ApplySessionFilter();
    partial void OnSessionSortChanged(string value) => ApplySessionFilter();
    partial void OnSelectedSessionChanged(SessionHistoryRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(SelectedSessionTitle));
        RenameSelectedSessionCommand.NotifyCanExecuteChanged();
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
        var allWorkspaces = DesktopText.Get("HistoryWorkspaceAll");
        var workspace = WorkspaceFilter;
        var filtered = Sessions.Where(row =>
            (provider == DesktopText.Get("HistoryProviderAll") || row.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) &&
            (workspace == allWorkspaces || PathsEqual(Path.GetFullPath(row.Workspace), workspace)) &&
            (query.Length == 0 || row.SearchIndex.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var matches = SessionSort switch
        {
            var sort when sort == DesktopText.Get("HistorySortName") => filtered
                .OrderBy(static row => row.ListTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.WorkspaceName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static row => row.Binding.UpdatedAt).ToArray(),
            var sort when sort == DesktopText.Get("HistorySortUsage") => filtered
                .OrderByDescending(static row => row.ExecutionCount)
                .ThenByDescending(static row => row.Binding.UpdatedAt).ToArray(),
            _ => filtered.OrderByDescending(static row => row.Binding.UpdatedAt).ToArray()
        };

        FilteredSessions.Clear();
        foreach (var row in matches) FilteredSessions.Add(row);
        HasFilteredSessions = matches.Length > 0;
        SelectedSession = selectedKey is null ? null : matches.FirstOrDefault(row => row.Binding.Key == selectedKey);
        OnPropertyChanged(nameof(HasSessionFilters));
        OnPropertyChanged(nameof(SessionResultSummary));
        OnPropertyChanged(nameof(SessionListEmptyText));
        ClearSessionFiltersCommand.NotifyCanExecuteChanged();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void UpdateWorkspaceFilters(IReadOnlyList<SessionBinding> bindings)
    {
        var previous = WorkspaceFilter;
        var all = DesktopText.Get("HistoryWorkspaceAll");
        var paths = bindings.Select(item => Path.GetFullPath(item.WorkspaceRoot))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        WorkspaceFilters.Clear();
        WorkspaceFilters.Add(all);
        foreach (var path in paths) WorkspaceFilters.Add(path);
        WorkspaceFilter = paths.Any(path => PathsEqual(path, previous)) ? paths.First(path => PathsEqual(path, previous)) : all;
        // Rebuilding ItemsSource can clear ComboBox selection even when the chosen value is unchanged.
        OnPropertyChanged(nameof(WorkspaceFilter));
    }

    [RelayCommand(CanExecute = nameof(CanClearSessionFilters))]
    private void ClearSessionFilters()
    {
        SearchQuery = string.Empty;
        ProviderFilter = DesktopText.Get("HistoryProviderAll");
        WorkspaceFilter = DesktopText.Get("HistoryWorkspaceAll");
    }

    private bool CanClearSessionFilters() => HasSessionFilters && !IsBusy;

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
            await LoadCachedChatNamesAsync(sessions);
            SetExecutionRecords(executions);
            Sessions.Clear();
            foreach (var row in BuildSessionRows(sessions, executions)) Sessions.Add(row);
            UpdateWorkspaceFilters(sessions);
            AuditEvents.Clear();
            foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
                AuditEvents.Add(new AuditHistoryRow(audit, AuditChatLabel(audit)));
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
        StartChatNameRefresh();
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
    private bool CanEditSelectedSessionModel() => SelectedSession is { Binding.NextAgentProvider: null } && !IsBusy;

    private bool CanSwitchSelectedAgent() => SelectedSession is { } selected && !IsBusy &&
        selected.Binding.Key.UserId is not null && selected.Binding.Key.Origin is not null &&
        selected.Binding.Key.Provider is "slack" or "mattermost" &&
        ConversationAgentService.ProviderOf(selected.Binding) is AgentProfile.Codex or AgentProfile.Claude;

    [RelayCommand(CanExecute = nameof(CanSwitchSelectedAgent))]
    private async Task SwitchSelectedAgentAsync()
    {
        if (SelectedSession is not { } selected || IsBusy || ChooseAgentSwitchAsync is null) return;
        IsBusy = true;
        try
        {
            var connections = new AgentConnectionStore();
            var summaries = new ConversationSummaryStore();
            var profiles = await connections.ListAsync();
            ConversationSummary? summary;
            try { summary = await summaries.FindAsync(selected.Binding.Key); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Handoff notes are optional; a damaged/unreadable note must not block an explicit fresh switch.
                summary = null;
            }
            if (summary?.SessionId != selected.SessionId) summary = null;
            var selection = await ChooseAgentSwitchAsync(selected, profiles, summary);
            if (selection is null) return;
            await new ConversationAgentService(_sessionStore, connections, summaries).SwitchAsync(selected.Binding,
                await _settingsStore.LoadAsync(), selection, _ => Task.FromResult(true));
            await _auditStore.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "agent_changed", null,
                selected.Binding.Key.Provider, selected.ChannelId, selected.ThreadId));
            await RefreshContentsAsync(DesktopText.Get("AgentSwitchSaved", selection.Provider));
            SelectedSession = Sessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch { Status = DesktopText.Get("AgentSwitchFailed"); }
        finally { IsBusy = false; }
    }
    private bool CanEditSelectedSessionPolicy() => SelectedSession is { Binding.NextAgentProvider: null } && !IsBusy;
    private bool CanRenameSelectedSession() => SelectedSession is not null && !IsBusy;

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
            await _sessionStore.UpdateModelAndEffortAsync(selected.Binding,
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

    [RelayCommand(CanExecute = nameof(CanRenameSelectedSession))]
    private async Task RenameSelectedSessionAsync()
    {
        var selected = SelectedSession;
        if (selected is null || IsBusy || RenameSessionAsync is null) return;
        IsBusy = true;
        try
        {
            var displayName = await RenameSessionAsync(selected);
            if (displayName is null) return;
            await _sessionStore.UpdateDisplayNameAsync(selected.Binding.Key, displayName, CancellationToken.None);
            // An explicit rename can invalidate a search on the previous alias. Clear text search
            // only when the renamed row no longer matches, leaving provider/workspace scopes intact.
            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var renamed = selected with { Binding = selected.Binding with { DisplayName = displayName.Trim() } };
                if (!renamed.SearchIndex.Contains(SearchQuery.Trim(), StringComparison.OrdinalIgnoreCase))
                    SearchQuery = string.Empty;
            }
            await RefreshContentsAsync(DesktopText.Get("HistoryRenameSaved"));
            SelectedSession = FilteredSessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch (Exception ex)
        {
            Status = DesktopText.Get("HistoryRenameFailed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshContentsAsync(string successMessage)
    {
        var executions = await _executionStore.ListAsync();
        var sessions = await _sessionStore.ListAsync();
        var auditEvents = await _auditStore.ListAsync();
        var selectedKey = SelectedSession?.Binding.Key;
        await LoadCachedChatNamesAsync(sessions);
        SetExecutionRecords(executions);
        Sessions.Clear();
        foreach (var row in BuildSessionRows(sessions, executions)) Sessions.Add(row);
        UpdateWorkspaceFilters(sessions);
        AuditEvents.Clear();
        foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
            AuditEvents.Add(new AuditHistoryRow(audit, AuditChatLabel(audit)));
        HasExecutions = Executions.Count > 0;
        HasSessions = Sessions.Count > 0;
        HasAuditEvents = AuditEvents.Count > 0;
        SelectedSession = selectedKey is null ? null : Sessions.FirstOrDefault(row => row.Binding.Key == selectedKey);
        ApplySessionFilter();
        NotifySummariesChanged();
        Status = DesktopText.Get("HistorySuccessCounts", successMessage, Executions.Count, Sessions.Count, AuditEvents.Count);
        StartChatNameRefresh();
    }

    private IReadOnlyList<SessionHistoryRow> BuildSessionRows(
        IReadOnlyList<SessionBinding> bindings, IReadOnlyList<ExecutionRecord> records)
    {
        var ambiguous = new Dictionary<SessionExecutionIdentity, HashSet<string>>();
        foreach (var binding in bindings)
        {
            if (!TryGetIdentity(binding, out var identity) || binding.Key.Origin is not { } origin) continue;
            if (!ambiguous.TryGetValue(identity, out var origins)) ambiguous[identity] = origins = new HashSet<string>(StringComparer.Ordinal);
            origins.Add(origin);
        }
        var ambiguousIdentities = ambiguous.Where(static item => item.Value.Count > 1)
            .Select(static item => item.Key).ToHashSet();

        var executions = new Dictionary<SessionExecutionIdentity, ExecutionActivityAccumulator>();
        var seenIds = new HashSet<Guid>();
        foreach (var record in records)
        {
            if (!seenIds.Add(record.Id) || !TryGetIdentity(record, out var identity)) continue;
            if (!executions.TryGetValue(identity, out var activity)) executions[identity] = activity = new ExecutionActivityAccumulator();
            activity.Add(record);
        }

        return bindings.Select(binding =>
        {
            if (!TryGetIdentity(binding, out var identity) || ambiguousIdentities.Contains(identity) || !executions.TryGetValue(identity, out var activity))
                return new SessionHistoryRow(binding, ChatLabels: _chatNames.GetValueOrDefault(binding.Key));
            return new SessionHistoryRow(binding, activity.Count, activity.LatestStatus, activity.LatestModel,
                _chatNames.GetValueOrDefault(binding.Key));
        }).OrderByDescending(static row => row.Binding.UpdatedAt).ToArray();
    }

    private static bool TryGetIdentity(SessionBinding binding, out SessionExecutionIdentity identity)
    {
        if (binding.Key.UserId is { Length: > 0 } userId && binding.Key.Origin is { Length: > 0 } &&
            !string.IsNullOrWhiteSpace(binding.SessionId))
        {
            identity = new SessionExecutionIdentity(binding.Key.Provider.ToLowerInvariant(), binding.Key.ChannelId,
                binding.Key.ThreadId, userId, binding.SessionId);
            return true;
        }
        identity = default;
        return false;
    }

    private static bool TryGetIdentity(ExecutionRecord record, out SessionExecutionIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(record.UserId) && !string.IsNullOrWhiteSpace(record.SessionId))
        {
            identity = new SessionExecutionIdentity(record.Source.ToLowerInvariant(), record.ChannelId,
                record.ThreadId, record.UserId, record.SessionId);
            return true;
        }
        identity = default;
        return false;
    }

    private readonly record struct SessionExecutionIdentity(
        string Provider, string ChannelId, string ThreadId, string UserId, string SessionId);

    private sealed class ExecutionActivityAccumulator
    {
        public int Count { get; private set; }
        public DateTimeOffset LatestStartedAt { get; private set; }
        public string? LatestStatus { get; private set; }
        public string? LatestModel { get; private set; }

        public void Add(ExecutionRecord record)
        {
            Count++;
            if (Count > 1 && record.StartedAt <= LatestStartedAt) return;
            LatestStartedAt = record.StartedAt;
            LatestStatus = record.Status;
            LatestModel = string.IsNullOrWhiteSpace(record.Usage?.Model) ? record.AgentModel : record.Usage.Model;
        }
    }
}
