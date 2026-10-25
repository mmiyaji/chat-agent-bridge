using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    public event EventHandler? SettingsSearchRequested;

    private void MainNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Compare the XAML identity rather than managed WinRT wrapper instances.
        if (args.InvokedItemContainer?.Name == nameof(OpenSettingsSearch) && CanSearchSettings)
        {
            // NavigationView finishes moving focus (and closing a minimal pane) after
            // ItemInvoked returns. Opening synchronously lets that dismiss the flyout.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (CanSearchSettings) SettingsSearchRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void FocusSettingsSearchTrigger()
    {
        if (CanSearchSettings) OpenSettingsSearch.Focus(FocusState.Keyboard);
    }
}
