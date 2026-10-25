using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _navigationShortcutsAttached;

    // Ctrl+1 to Ctrl+7 open the sidebar pages in their listed order, and each item's tooltip names its key,
    // which also reveals a label the narrow sidebar trimmed. The connection status item repeats Chat and is skipped.
    private void InitializeNavigationShortcuts()
    {
        if (_navigationShortcutsAttached) return;
        _navigationShortcutsAttached = true;
        var items = MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .Where(item => item.Tag is string)
            .Append(AboutNavigationItem).Take(9).ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var accelerator = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.Number1 + index };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                if (MainNavigation.IsEnabled) MainNavigation.SelectedItem = item;
            };
            item.KeyboardAccelerators.Add(accelerator);
            // The tooltip below already names the key; the automatic one would repeat it.
            item.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
            if (item.Content is string label)
                ToolTipService.SetToolTip(item, DesktopText.Get("NavShortcutTooltip", label, index + 1));
        }
    }
}
