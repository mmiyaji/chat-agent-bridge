using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private void OpenManualWorkspaceSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAgentSettings_Click(sender, e);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsAgentSelected) return;
            // The explicit section jump supersedes the navigation's queued scroll reset.
            ++_scrollResetGeneration;
            AgentCommonSettings.IsExpanded = true;
            MainNavigation.UpdateLayout();
            MainContentScroll.UpdateLayout();
            var target = TrustedWorkspaceCard.TransformToVisual((UIElement)MainContentScroll.Content)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            MainContentScroll.ChangeView(null, target.Y, null, true);
        });
    }

    private async void ReviewCliTrust_Click(object sender, RoutedEventArgs e) =>
        await CliTrustDialog.ShowAsync(ViewModel, XamlRoot);

    private async void ChooseTrustedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPickingTrustedFolder) return;

        ViewModel.IsPickingTrustedFolder = true;
        ViewModel.TrustedFolderStatus = string.Empty;
        try
        {
            var picker = new FolderPicker(App.Window.AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) ViewModel.TryAddTrustedFolder(folder.Path);
        }
        catch (Exception)
        {
            ViewModel.SetTrustedFolderStatus("TrustedFolderPickerFailed");
        }
        finally
        {
            ViewModel.IsPickingTrustedFolder = false;
        }
    }

    private void AddCurrentWorkspaceTrustedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPickingTrustedFolder) return;
        ViewModel.TryAddTrustedFolder(ViewModel.WorkspaceRoot);
    }
}
