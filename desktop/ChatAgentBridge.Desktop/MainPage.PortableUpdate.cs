using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _portableUpdatePreparing;
    private string? _portableUpdateReconnectProvider;
    private BridgeSettings? _portableUpdateConnectionSettings;
    internal bool CanRestartForPortableUpdate => !_languageRestarting && !_portableUpdatePreparing && CanRestartForLanguage() &&
        !InboxPage.ViewModel.SendAttachmentsCommand.IsRunning && !InboxPage.ViewModel.ConnectMattermostCommand.IsRunning &&
        !InboxPage.ViewModel.ConnectSlackCommand.IsRunning && !InboxPage.ViewModel.ConnectDiscordCommand.IsRunning;

    internal async Task<bool> PrepareForPortableUpdateAsync()
    {
        if (!CanRestartForPortableUpdate) return false;
        _portableUpdatePreparing = true;
        MainNavigation.IsEnabled = false;
        IsEnabled = false;
        try
        {
            var inbox = InboxPage.ViewModel;
            _portableUpdateReconnectProvider = inbox.IsConnected && inbox.IsConnectionConfirmed
                ? inbox.ConnectionProvider : null;
            _portableUpdateConnectionSettings = _portableUpdateReconnectProvider is not null
                ? inbox.ActiveConnectionSettings : null;
            await InboxPage.ViewModel.DisconnectCommand.ExecuteAsync(null);
            if (CanRestartForLanguage()) return true;
            await CancelPortableUpdatePreparationAsync();
            return false;
        }
        catch
        {
            await CancelPortableUpdatePreparationAsync();
            throw;
        }
    }

    internal async Task CancelPortableUpdatePreparationAsync()
    {
        if (!_portableUpdatePreparing) return;
        var provider = _portableUpdateReconnectProvider;
        var connectedSettings = _portableUpdateConnectionSettings;
        try
        {
            var inbox = InboxPage.ViewModel;
            if (provider is not null && connectedSettings is not null && !inbox.IsConnected && !inbox.IsExecuting)
            {
                var currentSettings = await new SettingsStore().LoadAsync();
                if (ChatConnectionPolicy.Matches(provider, currentSettings, connectedSettings))
                {
                    var command = provider switch
                    {
                        "mattermost" => inbox.ConnectMattermostCommand,
                        "slack" => inbox.ConnectSlackCommand,
                        "discord" => inbox.ConnectDiscordCommand,
                        _ => null
                    };
                    if (command?.CanExecute(null) == true)
                        await command.ExecuteAsync(null);
                }
            }
        }
        catch (Exception)
        {
            // Keep recovery failures in the Inbox's existing connection error presentation.
            var inbox = InboxPage.ViewModel;
            if (!inbox.IsConnected)
            {
                inbox.HasConnectionError = true;
                inbox.Status = DesktopText.Get("InboxReconnectFailed");
            }
        }
        finally
        {
            _portableUpdateReconnectProvider = null;
            _portableUpdateConnectionSettings = null;
            FinishPortableUpdatePreparation();
        }
    }

    internal void CancelPortableUpdatePreparation()
    {
        if (!_portableUpdatePreparing) return;
        _portableUpdateReconnectProvider = null;
        _portableUpdateConnectionSettings = null;
        FinishPortableUpdatePreparation();
    }

    private void FinishPortableUpdatePreparation()
    {
        _portableUpdatePreparing = false;
        MainNavigation.IsEnabled = true;
        IsEnabled = true;
    }
}
