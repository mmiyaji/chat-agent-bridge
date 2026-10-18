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
    public string Conversation => DesktopText.Get("HistorySessionConversation", ChannelId, ThreadId, Environment.NewLine,
        Binding.Key.UserId ?? DesktopText.Get("LegacyUnknownOwner"));
    public string AgentAndSession => $"{Agent} / {SessionId}";
    public string ModelOverride => Binding.Model ?? string.Empty;
    public string Model => string.IsNullOrWhiteSpace(Binding.Model)
        ? DesktopText.Get("HistoryNextModelDefault")
        : DesktopText.Get("HistoryNextModel", Binding.Model);
    public string ReasoningEffort => DesktopText.Get("HistoryNextReasoning", Binding.ReasoningEffort ?? DesktopText.Get("InheritAgentEffort"));
    public string ExecutionPolicy => DesktopText.Get("HistoryExecutionPolicy", PolicyLabel(Binding.ChatApprovalMode, DesktopText.Get("AskEveryTime"), "first", DesktopText.Get("AskFirstTime"), "auto", DesktopText.Get("AutoApprove")), PolicyLabel(Binding.ChatResultMode, DesktopText.Get("ManualShare"), "auto", DesktopText.Get("AutoShare"), "none", DesktopText.Get("NoSharing")), PermissionLabel(Binding.AgentPermissionMode));
    public string Updated => Binding.UpdatedAt.ToLocalTime().ToString("g");

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
    [ObservableProperty] public partial bool HasExecutions { get; set; }
    [ObservableProperty] public partial bool HasSessions { get; set; }
    [ObservableProperty] public partial bool HasAuditEvents { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand))]
    public partial SessionHistoryRow? SelectedSession { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionModelCommand)), NotifyCanExecuteChangedFor(nameof(EditSelectedSessionPolicyCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = DesktopText.Get("HistoryLoading");

    public Func<SessionHistoryRow, Task<bool>>? ConfirmDetachAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<ModelExecutionSelection?>>? EditSessionModelAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<SessionPolicySelection?>>? EditSessionPolicyAsync { get; set; }

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

            Executions.Clear();
            foreach (var record in executions) Executions.Add(new ExecutionHistoryRow(record));
            Sessions.Clear();
            foreach (var binding in sessions) Sessions.Add(new SessionHistoryRow(binding));
            AuditEvents.Clear();
            foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
                AuditEvents.Add(new AuditHistoryRow(audit));
            HasExecutions = Executions.Count > 0;
            HasSessions = Sessions.Count > 0;
            HasAuditEvents = AuditEvents.Count > 0;
            SelectedSession = null;
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
        Executions.Clear();
        foreach (var record in executions) Executions.Add(new ExecutionHistoryRow(record));
        Sessions.Clear();
        foreach (var binding in sessions) Sessions.Add(new SessionHistoryRow(binding));
        AuditEvents.Clear();
        foreach (var audit in auditEvents.OrderByDescending(static item => item.Timestamp))
            AuditEvents.Add(new AuditHistoryRow(audit));
        HasExecutions = Executions.Count > 0;
        HasSessions = Sessions.Count > 0;
        HasAuditEvents = AuditEvents.Count > 0;
        SelectedSession = null;
        Status = DesktopText.Get("HistorySuccessCounts", successMessage, Executions.Count, Sessions.Count, AuditEvents.Count);
    }
}
