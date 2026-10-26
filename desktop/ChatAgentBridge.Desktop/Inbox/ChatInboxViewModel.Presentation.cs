using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem
{
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StateDisplay));
        OnPropertyChanged(nameof(RequestLabel));
        OnPropertyChanged(nameof(PromptDisplay));
        OnPropertyChanged(nameof(IncomingFilesSummary));
    }

    public string StateTone => State switch
    {
        "承認待ち" => "pending",
        "実行中" or "自動実行を準備" or "追加指示の受領待ち" => "running",
        "完了" or "フォルダー変更済み" or "エージェント変更済み" or "実行中へ追加" => "success",
        "実行／記録エラー" or "添付受信エラー" or "フォルダー変更不可" or "エージェント変更不可" or
        "許可設定を確認できず実行不可" => "error",
        "キャンセル" or "拒否" or "許可取り消し・実行不可" => "stopped",
        "接続終了・再受信が必要" or "作業先変更・再投稿が必要" or "エージェント変更・再投稿が必要" or "追加指示の反映未確認" => "warning",
        _ when State.StartsWith("失敗 (", StringComparison.Ordinal) => "error",
        _ => "neutral"
    };
}

public sealed partial class InboxConversation
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string StateTone { get; set; } = "neutral";
    public string ListTitle
    {
        get
        {
            if (Requests.FirstOrDefault()?.ListPrompt is { Length: > 0 } text)
                return text;
            if (Snapshot?.DisplayName is { Length: > 0 } name) return name;
            if (Snapshot?.ChannelName is { Length: > 0 } channel) return channel;
            var folder = System.IO.Path.GetFileName(Workspace.TrimEnd('\\', '/'));
            return folder.Length > 0 ? folder : Title;
        }
    }
    public string PersonName => Snapshot?.UserName is { Length: > 0 } name &&
        !string.Equals(name, Snapshot.Key.UserId, StringComparison.Ordinal)
            ? name : DesktopText.Get("InboxUnknownParticipant");
    public string AvatarDisplayName => Snapshot?.UserName is { Length: > 0 } name &&
        !string.Equals(name, Snapshot.Key.UserId, StringComparison.Ordinal) ? name : "";
    public string DisplayLocation => Snapshot?.ChannelName is { Length: > 0 } channel
        ? channel : DesktopText.Get("InboxUnknownLocation");
    public string TitleLocation => $"{Title} · {DisplayLocation}";
    public string SuggestionTitle => ListTitle;
    public string SuggestionSubtitle => $"{PersonName} · {DisplayLocation} · {StateSummary}";
    public bool MatchesSearchMetadata(string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return true;
        return new[] { Identity, Title, Location, ListTitle, LatestPrompt, Workspace }
            .Any(value => value.Contains(needle, StringComparison.CurrentCultureIgnoreCase)) ||
            Requests.Any(item => item.Prompt.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
                item.Workspace.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
    }
    public string ActivityCompact => LastActivity == default ? "" : LastActivity.ToLocalTime().ToString(
        LastActivity.LocalDateTime.Date == DateTime.Today ? "t" : "d");
    public string ListTooltip => $"{ListTitle}\n{Title}\n{Location}\n{StateSummary}\n{Activity}";
    public bool HasSeveralRequests => Requests.Count > 1 && LatestPrompt.Length > 0;
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(RequestCountLabel));
        OnPropertyChanged(nameof(PersonName));
        OnPropertyChanged(nameof(AvatarDisplayName));
        OnPropertyChanged(nameof(DisplayLocation));
        OnPropertyChanged(nameof(TitleLocation));
        OnPropertyChanged(nameof(SuggestionTitle));
        OnPropertyChanged(nameof(SuggestionSubtitle));
        OnPropertyChanged(nameof(ListTooltip));
    }

    public void RefreshPresentation()
    {
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(LastActivity));
        OnPropertyChanged(nameof(HasSeveralRequests));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(PersonName));
        OnPropertyChanged(nameof(AvatarDisplayName));
        OnPropertyChanged(nameof(DisplayLocation));
        OnPropertyChanged(nameof(TitleLocation));
        OnPropertyChanged(nameof(SuggestionTitle));
        OnPropertyChanged(nameof(SuggestionSubtitle));
        OnPropertyChanged(nameof(ActivityCompact));
        OnPropertyChanged(nameof(ListTooltip));
    }
}

public sealed partial class ChatInboxViewModel
{
    public string? ConnectionProvider => _transport switch
    {
        ChatAgentBridge.Transports.MattermostWebSocketTransport => "mattermost",
        ChatAgentBridge.Transports.SlackSocketModeTransport => "slack",
        ChatAgentBridge.Transports.DiscordGatewayTransport => "discord",
        _ => null
    };
    // Derive from the live transport snapshot, never from an unsaved settings draft.
    public string ConnectionOverviewTarget
    {
        get
        {
            var provider = ConnectionProvider;
            if (provider == "mattermost")
                return Uri.TryCreate(_connectionSettings?.MattermostServerUrl, UriKind.Absolute, out var server)
                    && server.Scheme is "http" or "https" ? $"Mattermost · {server.IdnHost}" : "Mattermost";
            return provider switch { "slack" => "Slack", "discord" => "Discord", _ => "" };
        }
    }
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
        OnPropertyChanged(nameof(ReconnectLabel));
        OnPropertyChanged(nameof(ReconnectHint));
        OnPropertyChanged(nameof(PendingApprovalMessage));
        OnPropertyChanged(nameof(ConversationCountLabel));
        OnPropertyChanged(nameof(SelectedRequestCountLabel));
        OnPropertyChanged(nameof(EmptyListMessage));
        OnPropertyChanged(nameof(EmptyConversationHeading));
        OnPropertyChanged(nameof(EmptyConversationDescription));
        OnPropertyChanged(nameof(EmptyDetailHeading));
        OnPropertyChanged(nameof(EmptyDetailDescription));
        OnPropertyChanged(nameof(ClearConversationFiltersLabel));
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(SelectedUsage));
        OnPropertyChanged(nameof(RestoredConversationDescription));
        foreach (var request in Requests) request.RefreshLocalizedText();
        foreach (var conversation in Conversations)
        {
            if (conversation.Requests.Count == 0) ApplyRestoredPresentation(conversation);
            conversation.RefreshLocalizedText();
        }
    }

    partial void OnStatusChanged(string value)
    {
        if (!value.Equals(DesktopText.Get("InboxDisconnected"), StringComparison.Ordinal))
            _hasRuntimeStatus = true;
    }
    private void NotifyConnectionPresentation()
    {
        OnPropertyChanged(nameof(ConnectionProvider));
        OnPropertyChanged(nameof(ConnectionOverviewTarget));
        OnPropertyChanged(nameof(ConnectionTone));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ShowEmptyConversationList));
        OnPropertyChanged(nameof(EmptyConversationHeading));
        OnPropertyChanged(nameof(EmptyConversationDescription));
    }
    partial void OnIsConnectedChanged(bool value) => NotifyConnectionPresentation();
    partial void OnIsConnectionConfirmedChanged(bool value) => NotifyConnectionPresentation();
    partial void OnHasConnectionErrorChanged(bool value) => NotifyConnectionPresentation();
}
