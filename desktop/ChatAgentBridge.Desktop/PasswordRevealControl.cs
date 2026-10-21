using System.Runtime.CompilerServices;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

/// <summary>Wires an explicit, accessible reveal toggle to a PasswordBox.</summary>
internal static class PasswordRevealControl
{
    private sealed record Association(PasswordBox PasswordBox);
    private static readonly ConditionalWeakTable<ToggleButton, Association> Associations = new();

    public static void Attach(ToggleButton toggle, PasswordBox passwordBox)
    {
        passwordBox.PasswordRevealMode = PasswordRevealMode.Hidden;
        toggle.IsChecked = false;
        Associations.Add(toggle, new Association(passwordBox));
        toggle.Checked += Toggle_Changed;
        toggle.Unchecked += Toggle_Changed;
        passwordBox.Unloaded += (_, _) => Hide(toggle, passwordBox);
        UpdateToggle(toggle, passwordBox);
    }

    /// <summary>Hides all revealed passwords below a page or window root.</summary>
    public static void Hide(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ToggleButton toggle && Associations.TryGetValue(toggle, out var association))
                Hide(toggle, association.PasswordBox);
            Hide(child);
        }
    }

    private static void Toggle_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton toggle || !Associations.TryGetValue(toggle, out var association)) return;
        association.PasswordBox.PasswordRevealMode = toggle.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;
        UpdateToggle(toggle, association.PasswordBox);
    }

    private static void Hide(ToggleButton toggle, PasswordBox passwordBox)
    {
        passwordBox.PasswordRevealMode = PasswordRevealMode.Hidden;
        toggle.IsChecked = false;
        UpdateToggle(toggle, passwordBox);
    }

    private static void UpdateToggle(ToggleButton toggle, PasswordBox passwordBox)
    {
        var isRevealed = passwordBox.PasswordRevealMode == PasswordRevealMode.Visible;
        var fieldName = AutomationProperties.GetName(passwordBox);
        var action = DesktopText.Get(isRevealed ? "HideToken" : "ShowToken");
        var accessibleName = string.IsNullOrWhiteSpace(fieldName)
            ? action
            : DesktopText.Get(isRevealed ? "HideNamedToken" : "ShowNamedToken", fieldName);
        AutomationProperties.SetName(toggle, accessibleName);
        ToolTipService.SetToolTip(toggle, accessibleName);
        toggle.Content = new FontIcon { Glyph = isRevealed ? "\uED1A" : "\uE890" };
    }
}
