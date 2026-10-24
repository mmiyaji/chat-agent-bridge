namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool CanManageData => !_languageRestarting && !_portableUpdatePreparing && DataWorkIdle;
    private bool DataWorkIdle => !App.PortableUpdates.IsBusy && !HistoryPage.ViewModel.IsBusy &&
        !ViewModel.IsLoading && !ViewModel.IsRunning && !ViewModel.IsCheckingCli &&
        !ViewModel.IsOnboardingBusy && !ViewModel.ModelCatalog.IsLoading &&
        !ViewModel.SaveSettingsCommand.IsRunning && !ViewModel.HasUnsavedSettings &&
        !InboxPage.ViewModel.IsExecuting && !InboxPage.ViewModel.SendResultCommand.IsRunning &&
        !InboxPage.ViewModel.SendAttachmentsCommand.IsRunning &&
        !InboxPage.ViewModel.ConnectSlackCommand.IsRunning && !InboxPage.ViewModel.ConnectMattermostCommand.IsRunning &&
        !InboxPage.ViewModel.ConnectDiscordCommand.IsRunning &&
        !InboxPage.ViewModel.Requests.Any(item => item.State is "承認待ち" or "自動実行を準備" or "実行中");

    internal async Task<bool> PrepareForDataMaintenanceAsync()
    {
        // Recovery must remain available when settings are unreadable; work and authorization guards still apply.
        if (!CanManageData) return false;
        _portableUpdatePreparing = true;
        MainNavigation.IsEnabled = false;
        IsEnabled = false;
        try
        {
            await InboxPage.ViewModel.DisconnectCommand.ExecuteAsync(null);
            if (!DataWorkIdle) { CancelPortableUpdatePreparation(); return false; }
            App.PendingRequestNotifications.ClearAll();
            await HistoryPage.ViewModel.PauseChatNameUpdatesAsync();
            await InboxPage.ViewModel.PauseConversationPersistenceAsync();
            return true;
        }
        catch
        {
            CancelPortableUpdatePreparation();
            throw;
        }
    }

    internal void FinishDataMaintenance()
    {
        CancelPortableUpdatePreparation();
        HistoryPage.ViewModel.SetChatNameUpdatesEnabled(true);
        InboxPage.ViewModel.ResumeConversationPersistence();
    }

    internal async Task ReloadAfterDataMaintenanceAsync()
    {
        await ViewModel.LoadAsync();
        await InboxPage.ViewModel.RefreshAccessAsync();
        await InboxPage.ViewModel.ReloadConversationsAsync();
        await HistoryPage.ViewModel.RefreshAsync();
    }
}
