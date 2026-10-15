using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.History;

public sealed record ExecutionHistoryRow(ExecutionRecord Record)
{
    public string Started => Record.StartedAt.ToLocalTime().ToString("g");
    public string Source => string.IsNullOrWhiteSpace(Record.Source) ? "ローカル" : Record.Source;
    public string Status => Record.Status switch
    {
        "running" => "実行中または前回中断",
        "completed" => "完了",
        "failed" => "失敗",
        "cancelled" => "キャンセル",
        "interrupted" => "中断",
        _ => Record.Status
    };
    public string Workspace => Record.WorkspaceRoot;
    public string Session => Record.SessionId ?? "—";
    public string Conversation => string.Join(" / ", new[] { Record.ChannelId, Record.ThreadId, Record.UserId }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record AuditHistoryRow(AuditEvent Record)
{
    public string Timestamp => Record.Timestamp.ToLocalTime().ToString("g");
    public string Kind => Record.Kind;
    public string Source => string.IsNullOrWhiteSpace(Record.Source) ? "ローカル" : Record.Source;
    public string Conversation => string.Join(" / ", new[] { Record.ChannelId, Record.ThreadId }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record SessionHistoryRow(SessionBinding Binding)
{
    public string Provider => Binding.Key.Provider;
    public string ChannelId => Binding.Key.ChannelId;
    public string ThreadId => Binding.Key.ThreadId;
    public string Workspace => Binding.WorkspaceRoot;
    public string Agent => $"{Binding.AgentExecutable} {Binding.AgentArguments}".Trim();
    public string SessionId => Binding.SessionId;
    public string Conversation => $"{ChannelId} / {ThreadId}";
    public string AgentAndSession => $"{Agent} / {SessionId}";
    public string Updated => Binding.UpdatedAt.ToLocalTime().ToString("g");
}

public sealed partial class HistoryPageViewModel : ObservableObject
{
    private readonly ExecutionHistoryStore _executionStore = new();
    private readonly AuditStore _auditStore = new();
    private readonly SessionStore _sessionStore = new();

    public ObservableCollection<ExecutionHistoryRow> Executions { get; } = [];
    public ObservableCollection<AuditHistoryRow> AuditEvents { get; } = [];
    public ObservableCollection<SessionHistoryRow> Sessions { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand))]
    public partial SessionHistoryRow? SelectedSession { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(DetachSelectedSessionCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "履歴とセッションを読み込んでいます。";

    public Func<SessionHistoryRow, Task<bool>>? ConfirmDetachAsync { get; set; }

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
        SelectedSession = null;
        Status = $"{successMessage} 実行 {Executions.Count} 件、セッション {Sessions.Count} 件、監査イベント {AuditEvents.Count} 件";
    }
}
