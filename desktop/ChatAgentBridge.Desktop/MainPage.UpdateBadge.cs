using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private void InitializePortableUpdateBadge()
    {
        Loaded += UpdateBadge_Loaded;
        Unloaded += UpdateBadge_Unloaded;
    }

    private void UpdateBadge_Loaded(object sender, RoutedEventArgs args)
    {
        App.PortableUpdates.Changed -= PortableUpdates_Changed;
        App.PortableUpdates.Changed += PortableUpdates_Changed;
        RefreshPortableUpdateBadge();
    }

    private void UpdateBadge_Unloaded(object sender, RoutedEventArgs args) =>
        App.PortableUpdates.Changed -= PortableUpdates_Changed;

    private void PortableUpdates_Changed(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess) RefreshPortableUpdateBadge();
        else DispatcherQueue.TryEnqueue(RefreshPortableUpdateBadge);
    }

    private void RefreshPortableUpdateBadge()
    {
        if (AboutNavigationItem is null) return;
        var updates = App.PortableUpdates;
        var available = updates.IsSupported && !string.IsNullOrWhiteSpace(updates.AvailableVersion);
        AboutUpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        var label = available
            ? DesktopText.Get("PortableUpdateNavigationAvailable", updates.AvailableVersion)
            : MrtText.Get("NavAbout.Content");
        AutomationProperties.SetName(AboutNavigationItem, label);
        AutomationProperties.SetName(AboutUpdateBadge, DesktopText.Get("PortableUpdateBadgeAvailable"));
        ToolTipService.SetToolTip(AboutNavigationItem, available ? label : null);
    }

}
