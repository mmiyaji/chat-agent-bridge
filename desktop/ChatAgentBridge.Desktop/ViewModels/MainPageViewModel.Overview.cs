using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private const string OverviewCheckGlyph = "\uE73E";
    private const string OverviewWarningGlyph = "\uE7BA";
    private const string OverviewPendingGlyph = "\uE895";

    [ObservableProperty] public partial string OverviewAgentStatus { get; set; } = ViewModelText.Get("OverviewChecking");
    [ObservableProperty] public partial string OverviewAgentGlyph { get; set; } = OverviewPendingGlyph;
    [ObservableProperty] public partial string OverviewAgentAccessibleName { get; set; } = ViewModelText.Get("OverviewAgentAccessibleName", ViewModelText.Get("OverviewChecking"));
    [ObservableProperty] public partial string OverviewConnectionStatus { get; set; } = ViewModelText.Get("OverviewChecking");
    [ObservableProperty] public partial string OverviewConnectionGlyph { get; set; } = OverviewPendingGlyph;
    [ObservableProperty] public partial string OverviewConnectionAccessibleName { get; set; } = ViewModelText.Get("OverviewConnectionAccessibleName", ViewModelText.Get("OverviewChecking"));
    [ObservableProperty] public partial string OverviewChatStatus { get; set; } = ViewModelText.Get("OverviewChecking");
    [ObservableProperty] public partial string OverviewChatGlyph { get; set; } = OverviewPendingGlyph;
    [ObservableProperty] public partial string OverviewChatAccessibleName { get; set; } = ViewModelText.Get("OverviewChatAccessibleName", ViewModelText.Get("OverviewChecking"));
    [ObservableProperty] public partial string OverviewCliStatus { get; set; } = ViewModelText.Get("OverviewCliNotChecked");
    [ObservableProperty] public partial string OverviewCliGlyph { get; set; } = OverviewPendingGlyph;
    [ObservableProperty] public partial string OverviewCliAccessibleName { get; set; } = ViewModelText.Get("OverviewCliAccessibleName", ViewModelText.Get("OverviewCliNotChecked"));

    /// <summary>Refreshes the concise overview statuses after setup or CLI state changes.</summary>
    public void RefreshOverviewStatus()
    {
        var checking = IsLoading || !IsSettingsLoaded;
        var agentReady = _settings is not null &&
            !string.IsNullOrWhiteSpace(_settings.AgentExecutable) &&
            !string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) && _savedWorkspaceExists;
        var mattermostReady = _settings is not null &&
            !string.IsNullOrWhiteSpace(_settings.MattermostServerUrl) &&
            !string.IsNullOrWhiteSpace(_settings.MattermostChannelId) &&
            !string.IsNullOrWhiteSpace(_settings.MattermostToken) && HasAllowedUsers(_settings.MattermostAllowedUserIds);
        var slackReady = _settings is not null &&
            !string.IsNullOrWhiteSpace(_settings.SlackBotToken) &&
            !string.IsNullOrWhiteSpace(_settings.SlackAppToken) && HasAllowedUsers(_settings.SlackAllowedUserIds);
        var connectionReady = mattermostReady || slackReady;

        OverviewAgentStatus = ViewModelText.Get(checking ? "OverviewChecking" : agentReady ? "OverviewSaved" : "OverviewNeedsSetup");
        OverviewAgentGlyph = checking ? OverviewPendingGlyph : agentReady ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewAgentAccessibleName = ViewModelText.Get("OverviewAgentAccessibleName", OverviewAgentStatus);

        OverviewConnectionStatus = ViewModelText.Get(checking ? "OverviewChecking" : connectionReady ? "OverviewSaved" : "OverviewNeedsSetup");
        OverviewConnectionGlyph = checking ? OverviewPendingGlyph : connectionReady ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewConnectionAccessibleName = ViewModelText.Get("OverviewConnectionAccessibleName", OverviewConnectionStatus);

        OverviewChatStatus = ViewModelText.Get(checking ? "OverviewChecking" : _chatConnectionConfirmed ? "OverviewConnected" : _chatConnectionPreparing ? "OverviewConnecting" : "OverviewOffline");
        OverviewChatGlyph = checking ? OverviewPendingGlyph : _chatConnectionConfirmed ? OverviewCheckGlyph : _chatConnectionPreparing ? OverviewPendingGlyph : OverviewWarningGlyph;
        OverviewChatAccessibleName = ViewModelText.Get("OverviewChatAccessibleName", OverviewChatStatus);

        var cliVerified = HasCurrentCliCheck();
        var cliChecking = IsCheckingCli;
        var cliFailed = !cliVerified && !cliChecking && CliVersion != ViewModelText.Get("NotChecked");
        var cliStatusKey = cliChecking ? "OverviewChecking" : cliVerified ? "OverviewCliVerified" : cliFailed ? "OverviewCliFailed" : "OverviewCliNotChecked";
        OverviewCliStatus = ViewModelText.Get(cliStatusKey);
        OverviewCliGlyph = cliChecking || !cliVerified && !cliFailed ? OverviewPendingGlyph : cliVerified ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewCliAccessibleName = ViewModelText.Get("OverviewCliAccessibleName", OverviewCliStatus);
    }
}
