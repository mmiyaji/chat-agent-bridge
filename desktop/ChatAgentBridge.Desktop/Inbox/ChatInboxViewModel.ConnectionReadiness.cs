using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private bool _hasCheckedConnectReadiness;
    private bool _isMattermostReadyToConnect;
    private bool _isSlackReadyToConnect;
    private bool _isDiscordReadyToConnect;

    public bool IsMattermostReadyToConnect => _isMattermostReadyToConnect;
    public bool IsSlackReadyToConnect => _isSlackReadyToConnect;
    public bool IsDiscordReadyToConnect => _isDiscordReadyToConnect;
    public bool HasAnyChatProviderReady => IsMattermostReadyToConnect || IsSlackReadyToConnect || IsDiscordReadyToConnect;
    public bool ShowConnectionSettingsButton => !IsConnected && !HasConnectionError && !_closing;
    public bool ShowChatSetupNotice => _hasCheckedConnectReadiness && !HasAnyChatProviderReady &&
        !IsConnected && !HasConnectionError && !_closing;
    public string MattermostConnectionSetupHint => IsMattermostReadyToConnect ? "" : DesktopText.Get("ConnectMattermostSetupHint");
    public string SlackConnectionSetupHint => IsSlackReadyToConnect ? "" : DesktopText.Get("ConnectSlackSetupHint");
    public string DiscordConnectionSetupHint => IsDiscordReadyToConnect ? "" : DesktopText.Get("ConnectDiscordSetupHint");

    private bool CanConnectMattermost() => CanConnect("mattermost");
    private bool CanConnectSlack() => CanConnect("slack");
    private bool CanConnectDiscord() => CanConnect("discord");

    private bool CanConnect(string provider) => !_closing && !IsConnected && !IsExecuting &&
        !IsConnectionActionInProgress && _hasCheckedConnectReadiness && IsProviderReady(provider);

    private bool IsProviderReady(string provider) => provider switch
    {
        "mattermost" => _isMattermostReadyToConnect,
        "slack" => _isSlackReadyToConnect,
        "discord" => _isDiscordReadyToConnect,
        _ => false
    };

    private void SetConnectReadiness(BridgeSettings? settings)
    {
        _hasCheckedConnectReadiness = settings is not null;
        _isMattermostReadyToConnect = settings is not null && ChatConnectionReadiness.IsReady(settings, "mattermost");
        _isSlackReadyToConnect = settings is not null && ChatConnectionReadiness.IsReady(settings, "slack");
        _isDiscordReadyToConnect = settings is not null && ChatConnectionReadiness.IsReady(settings, "discord");
        ConnectMattermostCommand.NotifyCanExecuteChanged();
        ConnectSlackCommand.NotifyCanExecuteChanged();
        ConnectDiscordCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReconnectHint));
        OnPropertyChanged(nameof(IsMattermostReadyToConnect));
        OnPropertyChanged(nameof(IsSlackReadyToConnect));
        OnPropertyChanged(nameof(IsDiscordReadyToConnect));
        OnPropertyChanged(nameof(HasAnyChatProviderReady));
        OnPropertyChanged(nameof(ShowConnectionSettingsButton));
        OnPropertyChanged(nameof(ShowChatSetupNotice));
        OnPropertyChanged(nameof(MattermostConnectionSetupHint));
        OnPropertyChanged(nameof(SlackConnectionSetupHint));
        OnPropertyChanged(nameof(DiscordConnectionSetupHint));
    }
}
