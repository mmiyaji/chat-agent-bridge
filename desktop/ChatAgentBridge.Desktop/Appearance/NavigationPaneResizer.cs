using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Appearance;

/// <summary>
/// Adds a drag handle on the right edge of a NavigationView's open pane. The handle edits
/// OpenPaneLength, so the pane keeps its own layout, and reports each change immediately.
/// It is shown only while the pane is expanded beside the content, not in compact, minimal or top modes,
/// and its grip only appears when the edge is hovered. A collapsible pane also folds to its compact
/// icon strip when dragged past its minimum, and unfolds when dragged back out.
/// </summary>
internal static class NavigationPaneResizer
{
    private const double HandleWidth = 12;

    public static SizerBase Attach(NavigationView navigation, Panel host, double minimum, double maximum,
        double? initialWidth, Action<double> committed, string automationId, string accessibleName, bool collapsible = false)
    {
        if (initialWidth is { } width) navigation.OpenPaneLength = Math.Clamp(width, minimum, maximum);
        var offset = new TranslateTransform();
        var sizer = new NavigationPaneSizer
        {
            Navigation = navigation,
            Minimum = minimum,
            Maximum = maximum,
            Collapsible = collapsible,
            Width = HandleWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = offset,
            // The grip appears only while the edge is hovered, dragged or focused; otherwise the border stays plain.
            IsThumbVisible = false,
            // A transparent fill keeps the whole edge hit-testable while the grip is hidden.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        var pointerOver = false;
        void RevealThumb() => sizer.IsThumbVisible = pointerOver || sizer.PointerCaptures?.Count > 0 || sizer.FocusState != FocusState.Unfocused;
        sizer.PointerEntered += (_, _) => { pointerOver = true; RevealThumb(); };
        sizer.PointerExited += (_, _) => { pointerOver = false; RevealThumb(); };
        sizer.PointerCaptureLost += (_, _) => RevealThumb();
        sizer.GotFocus += (_, _) => RevealThumb();
        sizer.LostFocus += (_, _) => RevealThumb();
        Grid.SetRow(sizer, Grid.GetRow(navigation));
        Grid.SetRowSpan(sizer, Grid.GetRowSpan(navigation));
        Grid.SetColumn(sizer, Grid.GetColumn(navigation));
        Grid.SetColumnSpan(sizer, Grid.GetColumnSpan(navigation));
        AutomationProperties.SetAutomationId(sizer, automationId);
        AutomationProperties.SetName(sizer, accessibleName);
        AutomationProperties.SetHelpText(sizer, Localization.DesktopText.Get("PaneSizerHelp"));
        ToolTipService.SetToolTip(sizer, accessibleName);
        host.Children.Add(sizer);

        // A folded pane keeps its handle on the compact strip's edge so it can be dragged back out.
        void Position() => offset.X = (navigation.IsPaneOpen ? navigation.OpenPaneLength : navigation.CompactPaneLength) - HandleWidth / 2;
        void UpdateVisibility()
        {
            var beside = navigation.PaneDisplayMode switch
            {
                NavigationViewPaneDisplayMode.Left => true,
                NavigationViewPaneDisplayMode.Auto => navigation.DisplayMode == NavigationViewDisplayMode.Expanded,
                _ => false
            };
            sizer.Visibility = beside && (navigation.IsPaneOpen || collapsible) && navigation.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        navigation.RegisterPropertyChangedCallback(NavigationView.OpenPaneLengthProperty, (_, _) =>
        {
            Position();
            // AppLayout batches disk writes. Keep its in-memory snapshot current
            // so exiting directly after a drag can flush the final width.
            committed(navigation.OpenPaneLength);
        });
        navigation.RegisterPropertyChangedCallback(NavigationView.PaneDisplayModeProperty, (_, _) => UpdateVisibility());
        navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => { Position(); UpdateVisibility(); });
        navigation.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateVisibility());
        navigation.DisplayModeChanged += (_, _) => UpdateVisibility();
        // DisplayMode settles during the first layout pass, which may not raise DisplayModeChanged.
        navigation.Loaded += (_, _) => UpdateVisibility();
        navigation.SizeChanged += (_, _) => UpdateVisibility();
        Position();
        UpdateVisibility();
        return sizer;
    }
}

/// <summary>Resizes the pane from the pointer's cumulative drag, folding it once the edge passes the snap point.</summary>
/// <remarks>
/// Derives from PropertySizer only to reuse its default style; it drives the pane itself instead of a binding.
/// MainPage.xaml names the type once so XAML type metadata exists; without it the style cannot be applied.
/// </remarks>
public sealed partial class NavigationPaneSizer : PropertySizer
{
    public NavigationView? Navigation { get; set; }
    public bool Collapsible { get; set; }

    private double _startWidth;
    private double _openWidth;

    protected override void OnDragStarting()
    {
        if (Navigation is not { } navigation) return;
        _startWidth = navigation.IsPaneOpen ? navigation.OpenPaneLength : navigation.CompactPaneLength;
        _openWidth = navigation.OpenPaneLength;
    }

    protected override bool OnDragHorizontal(double horizontalChange)
    {
        if (Navigation is not { } navigation) return false;
        if (FlowDirection == FlowDirection.RightToLeft) horizontalChange = -horizontalChange;
        var width = _startWidth + horizontalChange;
        // Snap to whichever state is nearer: below the midpoint between the icon strip and the minimum width, fold.
        if (Collapsible && width < (navigation.CompactPaneLength + Minimum) / 2)
        {
            // Reopening from the menu button restores the width the drag started from, not the clamped minimum.
            navigation.OpenPaneLength = _openWidth;
            navigation.IsPaneOpen = false;
            return true;
        }
        navigation.IsPaneOpen = true;
        navigation.OpenPaneLength = Math.Clamp(width, Minimum, Maximum);
        return true;
    }

    protected override bool OnDragVertical(double verticalChange) => false;
}
