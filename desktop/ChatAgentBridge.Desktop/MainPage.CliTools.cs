using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private readonly ProcessEnvironmentRefresher _environmentRefresher = new();

    private async void BrowseAgentExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                CommitButtonText = DesktopText.Get("ChooseExecutable")
            };
            ConfigureExecutablePicker(picker, ViewModel.AgentProvider);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) ViewModel.AgentExecutable = file.Path;
        }
        catch
        {
            ViewModel.StatusTitle = DesktopText.Get("ExecutablePickerFailedTitle");
            ViewModel.StatusMessage = DesktopText.Get("ExecutablePickerFailedMessage");
            ViewModel.StatusSeverity = "Error";
            ViewModel.IsStatusOpen = true;
        }
        finally { button.IsEnabled = true; }
    }

    public static void ConfigureExecutablePicker(FileOpenPicker picker, string provider)
    {
        picker.FileTypeFilter.Add(".exe");
        if (provider is "gemini" or "github-copilot" or "codex") picker.FileTypeFilter.Add(".cmd");
        if (provider == "codex") picker.FileTypeFilter.Add(".ps1");
    }

    private bool CanRefreshProcessEnvironment() => !ViewModel.IsLoading &&
        !ViewModel.IsRunning && !ViewModel.IsCheckingCli && !ViewModel.ModelCatalog.IsLoading &&
        !ViewModel.IsOnboardingBusy && !InboxPage.ViewModel.IsExecuting &&
        !ViewModel.SaveSettingsCommand.IsRunning;

    public static bool EnvironmentReloadAllowed(bool isLoading, bool isRunning, bool isCheckingCli,
        bool isCatalogLoading, bool isOnboardingBusy, bool isInboxExecuting, bool isSaving) =>
        !isLoading && !isRunning && !isCheckingCli && !isCatalogLoading && !isOnboardingBusy && !isInboxExecuting && !isSaving;

    private void ReloadProcessEnvironment()
    {
        if (!CanRefreshProcessEnvironment()) return;
        try
        {
            _environmentRefresher.Refresh();
            ChatAgentBridge.Core.CodexExecutableResolver.ClearCache();
            ChatAgentBridge.Core.NodeCliPackageResolver.ClearCache();
            ChatAgentBridge.Core.AppServer.CodexAppServerRunner.ClearSharedIdleProcesses();
            ViewModel.OnProcessEnvironmentReloaded();
            ViewModel.StatusTitle = ViewModelText.Get("EnvironmentReloadedTitle");
            ViewModel.StatusMessage = ViewModelText.Get("EnvironmentReloadedMessage");
            ViewModel.StatusSeverity = "Success";
            ViewModel.IsStatusOpen = true;
        }
        catch
        {
            ViewModel.StatusTitle = ViewModelText.Get("EnvironmentReloadFailedTitle");
            ViewModel.StatusMessage = ViewModelText.Get("EnvironmentReloadFailedMessage");
            ViewModel.StatusSeverity = "Error";
            ViewModel.IsStatusOpen = true;
        }
    }

    private void ReloadProcessEnvironment_Click(object sender, RoutedEventArgs e) => ReloadProcessEnvironment();
}
