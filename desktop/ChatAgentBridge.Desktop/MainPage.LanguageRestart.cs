using ChatAgentBridge_Desktop.Localization;
using Microsoft.Windows.AppLifecycle;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _languageRestarting;

    private void InitializeLanguageRestart()
    {
        SettingsLanguage.CanRestartNow = () => !_languageRestarting && CanRestartForLanguage();
        SettingsLanguage.RestartRequested += RestartForLanguage;
    }

    private bool CanRestartForLanguage() => ViewModel.IsSettingsLoaded &&
        !ViewModel.IsLoading && !ViewModel.IsRunning && !ViewModel.IsCheckingCli &&
        !ViewModel.IsOnboardingBusy && !ViewModel.ModelCatalog.IsLoading &&
        !ViewModel.SaveSettingsCommand.IsRunning && !ViewModel.HasSetupDraftNotice &&
        !InboxPage.ViewModel.IsExecuting && !InboxPage.ViewModel.SendResultCommand.IsRunning &&
        !InboxPage.ViewModel.Requests.Any(item => item.State is "承認待ち" or "自動実行を準備" or "実行中");

    private async void RestartForLanguage()
    {
        if (_languageRestarting || !CanRestartForLanguage()) return;
        _languageRestarting = true;
        MainNavigation.IsEnabled = false;
        try
        {
            // Stop receiving new requests, then check again before terminating the process.
            await InboxPage.ViewModel.DisconnectCommand.ExecuteAsync(null);
            if (!CanRestartForLanguage())
            {
                ShowLanguageRestartFailure("LanguageRestartBusy");
                return;
            }

            // Preserve the exact settings directory, including isolated test launches.
            // Windows paths cannot contain quotes. Trimming trailing separators avoids
            // escaping the closing quote when the directory is a drive root.
            var directory = ViewModel.SettingsLocation.TrimEnd('\\', '/');
            if (directory.EndsWith(':')) directory += "\\.";
            _ = AppInstance.Restart($"--data-dir \"{directory}\"");
            // A successful restart does not return.
            ShowLanguageRestartFailure("LanguageRestartFailed");
        }
        catch (Exception)
        {
            ShowLanguageRestartFailure("LanguageRestartFailed");
        }
        finally
        {
            MainNavigation.IsEnabled = true;
            _languageRestarting = false;
        }
    }

    private void ShowLanguageRestartFailure(string messageKey)
    {
        ViewModel.StatusTitle = ViewModelText.Get("LanguageRestartTitle");
        ViewModel.StatusMessage = ViewModelText.Get(messageKey);
        ViewModel.StatusSeverity = "Warning";
        ViewModel.IsStatusOpen = true;
    }
}
