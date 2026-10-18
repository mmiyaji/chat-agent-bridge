using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public enum OverviewIconState { Neutral, Ready, Attention, Error }

public partial class MainPageViewModel
{
    private const string OverviewCheckGlyph = "\uE73E";
    private const string OverviewWarningGlyph = "\uE7BA";
    private const string OverviewPendingGlyph = "\uE895";
    private const string OverviewErrorGlyph = "\uE783";
    [ObservableProperty] public partial OverviewIconState OverviewAgentIconState { get; set; }
    [ObservableProperty] public partial OverviewIconState OverviewConnectionIconState { get; set; }
    [ObservableProperty] public partial OverviewIconState OverviewChatIconState { get; set; }
    [ObservableProperty] public partial OverviewIconState OverviewCliIconState { get; set; }

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
        var checking = IsLoading;
        var loadFailed = !IsLoading && !IsSettingsLoaded;
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

        var workspaceMissing = _settings is not null && !string.IsNullOrWhiteSpace(_settings.WorkspaceRoot) && !_savedWorkspaceExists;
        var agentError = loadFailed || workspaceMissing;
        OverviewAgentStatus = ViewModelText.Get(checking ? "OverviewChecking" : agentError ? "OverviewError" : agentReady ? "OverviewSaved" : "OverviewNeedsSetup");
        OverviewAgentGlyph = checking ? OverviewPendingGlyph : agentError ? OverviewErrorGlyph : agentReady ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewAgentIconState = checking ? OverviewIconState.Neutral : agentError ? OverviewIconState.Error : agentReady ? OverviewIconState.Ready : OverviewIconState.Attention;
        OverviewAgentAccessibleName = ViewModelText.Get("OverviewAgentAccessibleName", OverviewAgentStatus);

        OverviewConnectionStatus = ViewModelText.Get(checking ? "OverviewChecking" : loadFailed ? "OverviewError" : connectionReady ? "OverviewSaved" : "OverviewNeedsSetup");
        OverviewConnectionGlyph = checking ? OverviewPendingGlyph : loadFailed ? OverviewErrorGlyph : connectionReady ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewConnectionIconState = checking ? OverviewIconState.Neutral : loadFailed ? OverviewIconState.Error : connectionReady ? OverviewIconState.Ready : OverviewIconState.Attention;
        OverviewConnectionAccessibleName = ViewModelText.Get("OverviewConnectionAccessibleName", OverviewConnectionStatus);

        var chatError = loadFailed || _chatConnectionFailed;
        OverviewChatStatus = ViewModelText.Get(checking ? "OverviewChecking" : _chatConnectionConfirmed ? "OverviewConnected" : chatError ? "OverviewConnectionError" : _chatConnectionPreparing ? "OverviewConnecting" : "OverviewOffline");
        OverviewChatGlyph = checking ? OverviewPendingGlyph : _chatConnectionConfirmed ? OverviewCheckGlyph : chatError ? OverviewErrorGlyph : _chatConnectionPreparing ? OverviewPendingGlyph : OverviewWarningGlyph;
        OverviewChatIconState = checking ? OverviewIconState.Neutral : _chatConnectionConfirmed ? OverviewIconState.Ready : chatError ? OverviewIconState.Error : _chatConnectionPreparing ? OverviewIconState.Neutral : OverviewIconState.Attention;
        OverviewChatAccessibleName = ViewModelText.Get("OverviewChatAccessibleName", OverviewChatStatus);

        var cliVerified = HasCurrentCliCheck();
        var cliChecking = IsCheckingCli;
        var cliFailed = loadFailed || CliCheckFailed;
        var cliStatusKey = cliChecking ? "OverviewChecking" : cliVerified ? "OverviewCliVerified" : cliFailed ? "OverviewCliFailed" : "OverviewCliNotChecked";
        OverviewCliStatus = ViewModelText.Get(cliStatusKey);
        OverviewCliGlyph = cliChecking ? OverviewPendingGlyph : cliFailed ? OverviewErrorGlyph : cliVerified ? OverviewCheckGlyph : OverviewWarningGlyph;
        OverviewCliIconState = cliChecking || checking ? OverviewIconState.Neutral : cliFailed ? OverviewIconState.Error : cliVerified ? OverviewIconState.Ready : OverviewIconState.Attention;
        OverviewCliAccessibleName = ViewModelText.Get("OverviewCliAccessibleName", OverviewCliStatus);
    }
}
