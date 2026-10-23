using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ChatAgentBridge_Desktop.Onboarding;

/// <summary>
/// Outlines a required input in red until the user edits it. The input is wrapped once in a border of its own,
/// because the focused visual state of text controls replaces their own border and would hide the mark.
/// </summary>
internal static class FieldHighlight
{
    private const string Tag = "MissingFieldHighlight";

    /// <param name="target">The element to outline; for a password box this is the grid that also holds its reveal button.</param>
    /// <param name="input">The control whose edit clears the mark.</param>
    public static void Mark(FrameworkElement target, FrameworkElement input)
    {
        var border = Wrap(target);
        var color = target.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 255, 153, 164) : Color.FromArgb(255, 196, 43, 28);
        border.BorderBrush = new SolidColorBrush(color);
        border.Background = new SolidColorBrush(Color.FromArgb(24, color.R, color.G, color.B));
        Subscribe(input, border);
    }

    public static void Clear(FrameworkElement target)
    {
        if (target.Parent is Border { Tag: Tag } border)
        {
            border.BorderBrush = new SolidColorBrush(Colors.Transparent);
            border.Background = null;
        }
    }

    private static Border Wrap(FrameworkElement target)
    {
        if (target.Parent is Border { Tag: Tag } existing) return existing;
        var border = new Border
        {
            Tag = Tag,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            // The padding keeps the outline clear of the focus ring; the negative margin cancels it so wrapping
            // does not move neighbours. Full-width inputs keep their horizontal margin because the scroll
            // area would clip an outline that extends past its edges.
            Padding = new Thickness(4)
        };
        var stretched = target.HorizontalAlignment == HorizontalAlignment.Stretch;
        border.Margin = new Thickness(stretched ? 0 : -6, -6, stretched ? 0 : -6, -6);
        if (target.Parent is not Panel panel) return border;
        var index = panel.Children.IndexOf(target);
        panel.Children.RemoveAt(index);
        border.Child = target;
        Grid.SetRow(border, Grid.GetRow(target));
        Grid.SetColumn(border, Grid.GetColumn(target));
        Grid.SetRowSpan(border, Grid.GetRowSpan(target));
        Grid.SetColumnSpan(border, Grid.GetColumnSpan(target));
        panel.Children.Insert(index, border);
        return border;
    }

    private static void Subscribe(FrameworkElement input, Border border)
    {
        void Done()
        {
            border.BorderBrush = new SolidColorBrush(Colors.Transparent);
            border.Background = null;
        }
        switch (input)
        {
            case TextBox text:
                TextChangedEventHandler? changed = null;
                changed = (_, _) => { text.TextChanged -= changed; Done(); };
                text.TextChanged += changed;
                break;
            case PasswordBox password:
                RoutedEventHandler? passwordChanged = null;
                passwordChanged = (_, _) => { password.PasswordChanged -= passwordChanged; Done(); };
                password.PasswordChanged += passwordChanged;
                break;
            case ComboBox combo:
                SelectionChangedEventHandler? selected = null;
                selected = (_, _) => { combo.SelectionChanged -= selected; Done(); };
                combo.SelectionChanged += selected;
                break;
            case Button button:
                RoutedEventHandler? clicked = null;
                clicked = (_, _) => { button.Click -= clicked; Done(); };
                button.Click += clicked;
                break;
        }
    }
}
