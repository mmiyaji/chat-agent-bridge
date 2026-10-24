using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Appearance;

/// <summary>
/// Adds a drag handle on the right edge of a NavigationView's open pane. The handle edits
/// OpenPaneLength, so the pane keeps its own layout, and reports the width once dragging pauses.
/// It is shown only while the pane is expanded beside the content, not in compact, minimal or top modes.
/// </summary>
internal static class NavigationPaneResizer
{
    private const double HandleWidth = 12;
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(300);

    public static PropertySizer Attach(NavigationView navigation, Panel host, double minimum, double maximum,
        double? initialWidth, Action<double> committed, string automationId, string accessibleName)
    {
        if (initialWidth is { } width) navigation.OpenPaneLength = Math.Clamp(width, minimum, maximum);
        var offset = new TranslateTransform();
        var sizer = new PropertySizer
        {
            Minimum = minimum,
            Maximum = maximum,
            Width = HandleWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = offset
        };
        sizer.SetBinding(PropertySizer.BindingProperty, new Binding
        {
            Source = navigation,
            Path = new PropertyPath(nameof(NavigationView.OpenPaneLength)),
            Mode = BindingMode.TwoWay
        });
        Grid.SetRow(sizer, Grid.GetRow(navigation));
        Grid.SetRowSpan(sizer, Grid.GetRowSpan(navigation));
        Grid.SetColumn(sizer, Grid.GetColumn(navigation));
        Grid.SetColumnSpan(sizer, Grid.GetColumnSpan(navigation));
        AutomationProperties.SetAutomationId(sizer, automationId);
        AutomationProperties.SetName(sizer, accessibleName);
        AutomationProperties.SetHelpText(sizer, Localization.DesktopText.Get("PaneSizerHelp"));
        ToolTipService.SetToolTip(sizer, accessibleName);
        host.Children.Add(sizer);

        var commitTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        commitTimer.Interval = CommitDelay;
        commitTimer.IsRepeating = false;
        commitTimer.Tick += (_, _) => committed(navigation.OpenPaneLength);

        void Position() => offset.X = navigation.OpenPaneLength - HandleWidth / 2;
        void UpdateVisibility()
        {
            var beside = navigation.PaneDisplayMode switch
            {
                NavigationViewPaneDisplayMode.Left => true,
                NavigationViewPaneDisplayMode.Auto => navigation.DisplayMode == NavigationViewDisplayMode.Expanded,
                _ => false
            };
            sizer.Visibility = beside && navigation.IsPaneOpen && navigation.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        navigation.RegisterPropertyChangedCallback(NavigationView.OpenPaneLengthProperty, (_, _) =>
        {
            Position();
            commitTimer.Stop();
            commitTimer.Start();
        });
        navigation.RegisterPropertyChangedCallback(NavigationView.PaneDisplayModeProperty, (_, _) => UpdateVisibility());
        navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateVisibility());
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
