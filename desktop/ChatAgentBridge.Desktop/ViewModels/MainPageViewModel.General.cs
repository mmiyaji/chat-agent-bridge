using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private bool CanSaveGeneralSettings() => !_closing && IsSettingsLoaded && !IsLoading &&
        !IsRunning && !SaveSettingsCommand.IsRunning;

    [RelayCommand(CanExecute = nameof(CanSaveGeneralSettings))]
    private async Task SaveGeneralSettingsAsync()
    {
        if (!CanSaveGeneralSettings()) return;
        var provider = AutoConnectProvider;
        var saved = false;
        IsLoading = true;
        try
        {
            // Read persisted settings so this action cannot commit drafts from other tabs
            // or overwrite settings changed by chat operations since this page loaded.
            var baseline = await _settingsStore.LoadAsync();
            var snapshot = baseline with { AutoConnectProvider = provider };
            await _settingsStore.SaveAsync(snapshot);
            _settings = snapshot;
            _hasSavedSettings = true;
            _savedWorkspaceExists = Directory.Exists(snapshot.WorkspaceRoot);
            UpdateSavedAgentSummary();
            saved = true;
            SetStatus(ViewModelText.Get("GeneralSettingsSaved"), ViewModelText.Get("GeneralSettingsSavedDetails"), "Success");
        }
        catch (Exception ex)
        {
            SetStatus(ViewModelText.Get("SettingsSaveFailed"), ex.Message, "Error");
        }
        finally
        {
            IsLoading = false;
        }
        if (saved) SettingsSaved?.Invoke();
    }
}
