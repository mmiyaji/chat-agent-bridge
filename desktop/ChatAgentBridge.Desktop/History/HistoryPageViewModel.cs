using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
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
        "running" => "実行中または前回中断",
        "completed" => "完了",
        "failed" => "失敗",
        "cancelled" => "キャンセル",
        "interrupted" => "中断",
        _ => Record.Status
    };
    public string Summary => $"{Started} · {Source} · {Status}";
    public string Workspace => $"作業フォルダー: {Record.WorkspaceRoot}";
    public string Session => $"セッション ID: {Record.SessionId ?? "—"}";
    public string Model => $"指定モデル: {Record.AgentModel ?? "既定モデル"}";
    public string ReasoningEffort => $"推論レベル: {Record.AgentReasoningEffort ?? "CLIの既定値"}";
    public string Usage => UsageSummaryFormatter.Format(Record.Usage, Record.Metrics);
    public string Conversation => $"会話: チャンネル {Record.ChannelId} / スレッド {Record.ThreadId} / ユーザー {Record.UserId}";

    private static string SourceLabel(string source) => source switch
    {
        "local" => "ローカル",
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        _ => string.IsNullOrWhiteSpace(source) ? "不明な提供元" : source
    };
}

public sealed record AuditHistoryRow(AuditEvent Record)
{
    public string Timestamp => Record.Timestamp.ToLocalTime().ToString("g");
    public string Kind => Record.Kind switch
    {
        "received" => "依頼を受信",
        "approved" => "承認",
        "rejected" => "拒否",
        "started" => "実行を開始",
        "completed" => "実行が完了",
        "failed" => "実行に失敗",
        "cancelled" => "実行をキャンセル",
        "reply_failed" => "状態通知に失敗",
        "reply_sent" => "結果を送信",
        "detached" => "セッションを切り離し",
        _ => "その他の操作"
    };
    public string Source => Record.Source switch
    {
        "local" => "ローカル",
        "slack" => "Slack",
        "mattermost" => "Mattermost",
        _ => "提供元不明"
    };
    public string Summary => $"{Timestamp} · {Source} · {Kind}";
    public string Conversation => $"会話: チャンネル {Record.ChannelId} / スレッド {Record.ThreadId}";
}

public sealed record SessionHistoryRow(SessionBinding Binding)
{
    public string Provider => Binding.Key.Provider switch
    {
        "mattermost" => "Mattermost",
        "slack" => "Slack",
        "local" => "ローカル",
        _ => "提供元不明"
    };
    public string ChannelId => Binding.Key.ChannelId;
    public string ThreadId => Binding.Key.ThreadId;
    public string Workspace => Binding.WorkspaceRoot;
    public string Agent => Binding.AgentArguments == "app-server"
        ? "Codex App Server"
        : $"{Binding.AgentExecutable} {Binding.AgentArguments}".Trim();
    public string SessionId => Binding.SessionId;
    public string Conversation => $"{ChannelId} / {ThreadId}\n会話の所有者: {Binding.Key.UserId ?? "旧形式・所有者不明（自動継続しません）"}";
    public string AgentAndSession => $"{Agent} / {SessionId}";
    public string ModelOverride => Binding.Model ?? string.Empty;
    public string Model => string.IsNullOrWhiteSpace(Binding.Model)
        ? "次回の実行モデル: 既定モデルを使用"
        : $"次回の実行モデル: {Binding.Model}";
    public string ReasoningEffort => $"次回の推論レベル: {Binding.ReasoningEffort ?? "エージェント設定を継承（モデルが異なる場合はCLIの既定値）"}";
    public string ExecutionPolicy => $"会話ポリシー: 承認 {PolicyLabel(Binding.ChatApprovalMode, "毎回確認", "first", "最初だけ確認", "auto", "自動承認")} / 結果 {PolicyLabel(Binding.ChatResultMode, "手動で確認して共有", "auto", "自動共有", "none", "共有しない")} / AI操作 {PermissionLabel(Binding.AgentPermissionMode)}";
    public string Updated => Binding.UpdatedAt.ToLocalTime().ToString("g");

    private static string PolicyLabel(string? value, string defaultLabel, string keyOne, string labelOne, string keyTwo, string labelTwo) =>
        value is null ? "全体設定" : value == keyOne ? labelOne : value == keyTwo ? labelTwo : defaultLabel;
    private static string PermissionLabel(string? value) => value switch
    {
        null => "全体設定", "read-only" => "読み取り・計画のみ", "workspace-write" => "フォルダー内の編集を自動許可",
        "full-access" => "フルアクセス", "ask" => "操作ごとに確認", _ => "CLI既定"
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
    [ObservableProperty] public partial string Status { get; set; } = "履歴とセッションを読み込んでいます。";

    public Func<SessionHistoryRow, Task<bool>>? ConfirmDetachAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<ModelExecutionSelection?>>? EditSessionModelAsync { get; set; }
    public Func<SessionHistoryRow, BridgeSettings, Task<SessionPolicySelection?>>? EditSessionPolicyAsync { get; set; }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "履歴とセッションを更新しています…";
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
            Status = $"実行 {Executions.Count} 件、セッション {Sessions.Count} 件、監査イベント {AuditEvents.Count} 件";
        }
        catch (Exception ex)
        {
            Status = $"履歴を読み込めませんでした: {ex.Message}";
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
            Status = "セッションを切り離しています…";
            await _sessionStore.RemoveAsync(selected.Binding.Key);
            try
            {
                var key = selected.Binding.Key;
                await _auditStore.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, "detached", null,
                    key.Provider, key.ChannelId, key.ThreadId), CancellationToken.None);
            }
            catch { auditFailed = true; }

            successMessage = auditFailed
                ? "セッションを切り離しましたが、監査記録に失敗しました。実行履歴は保持されています。"
                : "セッションを切り離しました。実行履歴は保持されています。";
            try
            {
                await RefreshContentsAsync(successMessage);
            }
            catch
            {
                Status = $"{successMessage} 切り離しは完了しましたが、一覧を更新できませんでした。";
            }
        }
        catch (Exception ex)
        {
            Status = $"セッションを切り離せませんでした: {ex.Message}";
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
            await RefreshContentsAsync("この会話の実行ポリシーを保存しました。");
            SelectedSession = Sessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch (Exception ex)
        {
            Status = $"この会話の実行ポリシーを保存できませんでした: {ex.Message}";
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
            await RefreshContentsAsync("次回の実行モデルを保存しました。推論レベルも更新しました。");
            SelectedSession = Sessions.FirstOrDefault(row => row.Binding.Key == selected.Binding.Key);
        }
        catch
        {
            Status = "次回の実行モデルを保存できませんでした。セッションが残っているか確認してください。";
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
        Status = $"{successMessage} 実行 {Executions.Count} 件、セッション {Sessions.Count} 件、監査イベント {AuditEvents.Count} 件";
    }
}
