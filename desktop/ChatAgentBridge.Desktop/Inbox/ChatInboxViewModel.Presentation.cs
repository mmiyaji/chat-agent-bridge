using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem
{
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StateDisplay));
        OnPropertyChanged(nameof(RequestLabel));
    }

    public string StateTone => State switch
    {
        "承認待ち" => "pending",
        "実行中" or "自動実行を準備" => "running",
        "完了" or "フォルダー変更済み" or "エージェント変更済み" => "success",
        "実行／記録エラー" or "フォルダー変更不可" or "エージェント変更不可" or
        "許可設定を確認できず実行不可" => "error",
        "キャンセル" or "拒否" or "許可取り消し・実行不可" => "stopped",
        "接続終了・再受信が必要" or "作業先変更・再投稿が必要" or "エージェント変更・再投稿が必要" => "warning",
        _ when State.StartsWith("失敗 (", StringComparison.Ordinal) => "error",
        _ => "neutral"
    };
}

public sealed partial class InboxConversation
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string StateTone { get; set; } = "neutral";
    public string ListTitle => Requests.FirstOrDefault()?.Prompt.Replace('\r', ' ').Replace('\n', ' ').Trim() is { Length: > 0 } text
        ? text[..Math.Min(text.Length, 120)] : Title;
    public string ActivityCompact => Requests.LastOrDefault()?.ReceivedAt.ToString("t") ?? "";
    public string ListTooltip => $"{ListTitle}\n{Title}\n{Location}\n{StateSummary}\n{Activity}";
    public bool HasSeveralRequests => RequestCount > 1;
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(RequestCountLabel));
        OnPropertyChanged(nameof(ListTooltip));
    }

    public void RefreshPresentation()
    {
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ActivityCompact));
        OnPropertyChanged(nameof(ListTooltip));
    }
}

public sealed partial class ChatInboxViewModel
{
    public string ConnectionTone => HasConnectionError ? "error" : IsConnectionConfirmed ? "success" : IsConnected ? "running" : "neutral";
    public string ConnectionLabel => DesktopText.Get(HasConnectionError ? "InboxConnectionErrorLabel" :
        IsConnectionConfirmed ? "InboxConnectionReadyLabel" : IsConnected ? "InboxConnectionWorkingLabel" : "InboxConnectionOfflineLabel");
    public string QueueSummary => DesktopText.Get("InboxQueueSummary",
        Requests.Count(item => item.State == "承認待ち"), Requests.Count(item => item.State == "実行中"));

    public void RefreshLocalizedText()
    {
        // Only replace the default status while the inbox is still idle. A live
        // connection, request, or runtime message keeps its current meaning.
        if (!_hasRuntimeStatus && !_closing && !IsConnected && !IsExecuting &&
            !HasConnectionError && Requests.Count == 0)
            Status = DesktopText.Get("InboxDisconnected");

        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(ConversationCountLabel));
        OnPropertyChanged(nameof(SelectedRequestCountLabel));
        OnPropertyChanged(nameof(EmptyListMessage));
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(SelectedUsage));
        foreach (var request in Requests) request.RefreshLocalizedText();
        foreach (var conversation in Conversations) conversation.RefreshLocalizedText();
    }

    partial void OnStatusChanged(string value)
    {
        if (!value.Equals(DesktopText.Get("InboxDisconnected"), StringComparison.Ordinal))
            _hasRuntimeStatus = true;
    }
    private void NotifyConnectionPresentation()
    {
        OnPropertyChanged(nameof(ConnectionTone));
        OnPropertyChanged(nameof(ConnectionLabel));
    }
    partial void OnIsConnectedChanged(bool value) => NotifyConnectionPresentation();
    partial void OnIsConnectionConfirmedChanged(bool value) => NotifyConnectionPresentation();
    partial void OnHasConnectionErrorChanged(bool value) => NotifyConnectionPresentation();
}
