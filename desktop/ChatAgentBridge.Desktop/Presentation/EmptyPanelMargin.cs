using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Presentation;

/// <summary>
/// Drops a panel's margin while none of its children are visible, so an action row that is empty for the
/// current state does not leave a gap under the page title. The panel's own Visibility stays untouched.
/// </summary>
public static class EmptyPanelMargin
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EmptyPanelMargin), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty OriginalMarginProperty = DependencyProperty.RegisterAttached(
        "OriginalMargin", typeof(Thickness), typeof(EmptyPanelMargin), new PropertyMetadata(default(Thickness)));

    public static bool GetIsEnabled(DependencyObject panel) => (bool)panel.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject panel, bool value) => panel.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Panel panel || args.NewValue is not true) return;
        var attached = false;
        panel.Loaded += (_, _) =>
        {
            // Loaded fires again after the page is re-entered; hook the children once.
            if (attached) { Update(panel); return; }
            attached = true;
            panel.SetValue(OriginalMarginProperty, panel.Margin);
            foreach (var child in panel.Children)
                child.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Update(panel));
            Update(panel);
        };
    }

    private static void Update(Panel panel)
    {
        var hasVisibleChild = panel.Children.Any(child => child.Visibility == Visibility.Visible);
        panel.Margin = hasVisibleChild ? (Thickness)panel.GetValue(OriginalMarginProperty) : new Thickness(0);
    }
}
