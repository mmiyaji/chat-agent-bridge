using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Presentation;

/// <summary>
/// Gives flyouts the Windows desktop acrylic backdrop. The window uses Mica, which is drawn by the system behind
/// the XAML content, so the default in-app acrylic of a flyout cannot sample it and renders as flat gray.
/// Desktop acrylic samples the desktop like Mica does, so flyouts blend with the window as Windows 11 menus do.
/// </summary>
public static class PopupBackdrop
{
    public static readonly DependencyProperty UseSystemBackdropProperty = DependencyProperty.RegisterAttached(
        "UseSystemBackdrop", typeof(bool), typeof(PopupBackdrop), new PropertyMetadata(false, OnUseSystemBackdropChanged));

    public static bool GetUseSystemBackdrop(DependencyObject flyout) => (bool)flyout.GetValue(UseSystemBackdropProperty);
    public static void SetUseSystemBackdrop(DependencyObject flyout, bool value) => flyout.SetValue(UseSystemBackdropProperty, value);

    /// <summary>Applies the backdrop to a flyout created in code.</summary>
    public static T Apply<T>(T flyout) where T : FlyoutBase
    {
        SetUseSystemBackdrop(flyout, true);
        return flyout;
    }

    private static void OnUseSystemBackdropChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FlyoutBase flyout || args.NewValue is not true) return;
        flyout.SystemBackdrop = new DesktopAcrylicBackdrop();
        if (flyout is Flyout contentFlyout)
        {
            // Wrap the presenter style when the flyout opens, after any FlyoutPresenterStyle set in XAML.
            contentFlyout.Opening -= ClearPresenterBackground;
            contentFlyout.Opening += ClearPresenterBackground;
        }
    }

    private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static void ClearPresenterBackground(object? sender, object args)
    {
        if (sender is not Flyout flyout) return;
        var current = flyout.FlyoutPresenterStyle;
        if (current?.Setters.OfType<Setter>().Any(setter => setter.Property == Control.BackgroundProperty && setter.Value == Transparent) == true)
            return;
        // The presenter's own background would otherwise cover the backdrop.
        var style = new Style(typeof(FlyoutPresenter)) { BasedOn = current };
        style.Setters.Add(new Setter(Control.BackgroundProperty, Transparent));
        flyout.FlyoutPresenterStyle = style;
    }
}
