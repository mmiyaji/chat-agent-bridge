using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Layout;

/// <summary>
/// Keeps wheel movement crisp in opted-in text detail panes.
/// </summary>
internal sealed class MouseWheelScrollBehavior
{
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const uint WheelPageScroll = uint.MaxValue;
    // Native traces measured one notch at 15% of the viewport with the system's 3-line setting.
    private readonly ScrollViewer _viewer;
    private double _distanceRemainder;
    private double? _pendingWheelOffset;

    private MouseWheelScrollBehavior(ScrollViewer viewer)
    {
        _viewer = viewer;
        if (viewer.Content is UIElement content)
        {
            // Empty panel gaps must receive the same wheel input as the text.
            if (content is Panel { Background: null } panel)
                panel.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            content.PointerWheelChanged += OnPointerWheelChanged;
        }
        _viewer.ViewChanged += OnViewChanged;
    }

    public static void Attach(ScrollViewer viewer) => _ = new MouseWheelScrollBehavior(viewer);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_viewer);
        if (e.Handled || e.KeyModifiers != Windows.System.VirtualKeyModifiers.None ||
            e.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            point.Properties.IsHorizontalMouseWheel || _viewer.ScrollableHeight <= 0 ||
            HasInnerWheelTarget(e.OriginalSource))
        {
            _pendingWheelOffset = null;
            _distanceRemainder = 0;
            return;
        }

        // A direct, non-animated ChangeView in this routed event supersedes the native
        // eased mouse-wheel step. Other input continues through ScrollViewer unchanged.
        e.Handled = true;
        var delta = point.Properties.MouseWheelDelta / 120d;
        if (delta == 0) return;

        if (!SystemParametersInfo(SpiGetWheelScrollLines, 0, out var lines, 0))
            lines = 3;

        var distance = lines == WheelPageScroll
            ? _viewer.ViewportHeight * delta
            : lines * (_viewer.ViewportHeight / 20) * delta;
        var scale = _viewer.XamlRoot?.RasterizationScale ?? 1;
        var pixelDistance = Math.Round((_distanceRemainder + distance) * scale,
            MidpointRounding.AwayFromZero) / scale;
        _distanceRemainder += distance - pixelDistance;
        if (Math.Abs(pixelDistance) < 0.001) return;

        var target = Math.Clamp((_pendingWheelOffset ?? _viewer.VerticalOffset) - pixelDistance,
            0, _viewer.ScrollableHeight);
        target = Math.Round(target * scale, MidpointRounding.AwayFromZero) / scale;
        target = Math.Clamp(target, 0, _viewer.ScrollableHeight);

        if (_pendingWheelOffset is null && Math.Abs(target - _viewer.VerticalOffset) < 0.001)
        {
            _pendingWheelOffset = null;
            return;
        }

        if (_viewer.ChangeView(_viewer.HorizontalOffset, target, _viewer.ZoomFactor, disableAnimation: true))
            _pendingWheelOffset = target;
        else
            _pendingWheelOffset = null;
    }

    private bool HasInnerWheelTarget(object source)
    {
        // Let nested editors, lists and horizontal charts consume or chain their own
        // input. Otherwise a parent pane could move as well as the hovered control.
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, _viewer);
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer or TextBox or PasswordBox or RichEditBox or ComboBox or
                NumberBox or Slider or ScrollBar or ListViewBase)
                return true;
        }
        return false;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) _pendingWheelOffset = null;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
