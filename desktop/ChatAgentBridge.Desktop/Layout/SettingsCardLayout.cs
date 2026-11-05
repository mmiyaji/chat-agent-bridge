using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop.Layout;

/// <summary>Keeps settings readable when the page or its sidebar leaves little horizontal space.</summary>
public static class SettingsCardLayout
{
    public static readonly DependencyProperty IsAdaptiveProperty = DependencyProperty.RegisterAttached(
        "IsAdaptive", typeof(bool), typeof(SettingsCardLayout), new PropertyMetadata(false, OnIsAdaptiveChanged));

    public static bool GetIsAdaptive(DependencyObject value) => (bool)value.GetValue(IsAdaptiveProperty);
    public static void SetIsAdaptive(DependencyObject value, bool enabled) => value.SetValue(IsAdaptiveProperty, enabled);

    private static void OnIsAdaptiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not SettingsCard card) return;
        card.SizeChanged -= Card_SizeChanged;
        if (args.NewValue is true)
        {
            card.SizeChanged += Card_SizeChanged;
            Update(card, card.ActualWidth);
        }
    }

    private static void Card_SizeChanged(object sender, SizeChangedEventArgs args) => Update((SettingsCard)sender, args.NewSize.Width);

    private static void Update(SettingsCard card, double width)
    {
        var alignment = width < 680 ? ContentAlignment.Vertical : ContentAlignment.Right;
        if (card.ContentAlignment != alignment) card.ContentAlignment = alignment;
        if (card.Content is Microsoft.UI.Xaml.Controls.StackPanel row &&
            row.Children.Count > 0 && row.Children.All(child => child is Microsoft.UI.Xaml.Controls.RadioButton))
        {
            // Options sit in one row beside the text; a narrow card stacks them so none is cut off.
            var orientation = width < 520 ? Microsoft.UI.Xaml.Controls.Orientation.Vertical : Microsoft.UI.Xaml.Controls.Orientation.Horizontal;
            if (row.Orientation != orientation) row.Orientation = orientation;
        }
    }
}
