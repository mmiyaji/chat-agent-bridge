namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _portableUpdatePreparing;
    internal bool CanRestartForPortableUpdate => !_languageRestarting && !_portableUpdatePreparing && CanRestartForLanguage() &&
        !InboxPage.ViewModel.SendAttachmentsCommand.IsRunning && !InboxPage.ViewModel.ConnectMattermostCommand.IsRunning &&
        !InboxPage.ViewModel.ConnectSlackCommand.IsRunning;

    internal async Task<bool> PrepareForPortableUpdateAsync()
    {
        if (!CanRestartForPortableUpdate) return false;
        _portableUpdatePreparing = true;
        MainNavigation.IsEnabled = false;
        IsEnabled = false;
        try
        {
            await InboxPage.ViewModel.DisconnectCommand.ExecuteAsync(null);
            if (CanRestartForLanguage()) return true;
            CancelPortableUpdatePreparation();
            return false;
        }
        catch
        {
            CancelPortableUpdatePreparation();
            throw;
        }
    }

    internal void CancelPortableUpdatePreparation()
    {
        if (!_portableUpdatePreparing) return;
        _portableUpdatePreparing = false;
        MainNavigation.IsEnabled = true;
        IsEnabled = true;
    }
}
