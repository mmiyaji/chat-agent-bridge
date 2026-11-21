using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _openingInboxWorkspace;

    private async void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.SettingsLocation;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        try { await Windows.System.Launcher.LaunchFolderPathAsync(path); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not open settings folder: {ex.Message}"); }
    }

    private async void OpenSavedAgentSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAgentSettings_Click(sender, e);
        await SelectAgentSettingsScopeAsync(ViewModel.AgentProvider);
    }

    private async void OpenSavedWorkspaceSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAgentSettings_Click(sender, e);
        if (!await SelectAgentSettingsScopeAsync("common")) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsAgentSelected) return;
            ++_scrollResetGeneration;
            AgentBasicSettings.IsExpanded = true;
            MainNavigation.UpdateLayout();
            _agentSettingsScroll?.UpdateLayout();
            WorkspaceRootInput.StartBringIntoView();
        });
    }

    private async void OpenInboxWorkspaceSettings()
    {
        if (_openingInboxWorkspace || !IsChatSelected || !ViewModel.IsSettingsLoaded) return;
        _openingInboxWorkspace = true;
        try
        {
            if (!await NavigateToPageAsync("agent")) return;
            InitializeAgentSettingsScope();
            if (_agentSettingsScope != "common")
            {
                await WaitForAgentEditorIdleAsync();
                if (!IsAgentSelected || !await SelectAgentSettingsScopeAsync("common", preserveSharedDraft: true)) return;
            }
            if (!IsAgentSelected) return;
            _agentScopeInitialized = _agentScopeNavigation is not null;

            ++_scrollResetGeneration;
            AgentBasicSettings.IsExpanded = true;
            MainNavigation.UpdateLayout();
            _agentSettingsScroll?.UpdateLayout();
            WorkspaceRootInput.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0.2,
                AnimationDesired = false
            });
            WorkspaceRootInput.Focus(FocusState.Keyboard);
            await PickWorkspaceAsync();
        }
        finally { _openingInboxWorkspace = false; }
    }

    private void SetupActions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 400;
        Grid.SetRow(SetupGuideAction, narrow ? 1 : 0);
        Grid.SetColumn(SetupGuideAction, narrow ? 0 : 1);
        Grid.SetColumnSpan(SetupGuideAction, narrow ? 2 : 1);
        Grid.SetColumnSpan(SetupReloadAction, narrow ? 2 : 1);
    }
}
