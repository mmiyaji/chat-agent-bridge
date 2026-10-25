using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
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

    private void SetupActions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 400;
        Grid.SetRow(SetupGuideAction, narrow ? 1 : 0);
        Grid.SetColumn(SetupGuideAction, narrow ? 0 : 1);
        Grid.SetColumnSpan(SetupGuideAction, narrow ? 2 : 1);
        Grid.SetColumnSpan(SetupReloadAction, narrow ? 2 : 1);
    }
}
