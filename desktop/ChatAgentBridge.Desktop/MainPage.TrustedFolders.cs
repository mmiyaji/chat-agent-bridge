using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
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
