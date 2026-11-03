using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _footerBadgeLayoutQueued;

    private void QueueFooterBadgeLayout()
    {
        if (_footerBadgeLayoutQueued) return;
        _footerBadgeLayoutQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _footerBadgeLayoutQueued = false;
            UpdateFooterBadgeLayout();
        });
    }

    private void UpdateFooterBadgeLayout()
    {
        if (MainNavigation is null || AboutUpdateBadge is null || ConnectionStatusBadge is null) return;
        var compact = !MainNavigation.IsPaneOpen;
        var hasUpdate = AboutUpdateBadge.Visibility == Visibility.Visible;
        AboutNavigationIcon.Opacity = compact && hasUpdate ? 0 : 1;
        PositionFooterBadge(AboutUpdateBadge, AboutNavigationIcon, compact, isUpdate: true);
        if (NavChatConnectionStatus.Icon is { } serviceIcon)
            PositionFooterBadge(ConnectionStatusBadge, serviceIcon, compact, isUpdate: false);
    }

    private void PositionFooterBadge(InfoBadge badge, IconElement icon, bool compact, bool isUpdate)
    {
        var size = compact && !isUpdate ? 8 : 24;
        badge.Width = badge.Height = badge.MinWidth = badge.MinHeight = size;
        badge.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 4, 0);
        badge.RenderTransform = null;
        if (!compact || badge.Visibility != Visibility.Visible || !badge.IsLoaded || !icon.IsLoaded) return;
        // Measure after WinUI applies its compact state. RenderTransform is independent
        // of native visual-state setters, unlike changing the badge presenter's alignment.
        MainNavigation.UpdateLayout();
        var iconBounds = icon.TransformToVisual(MainNavigation).TransformBounds(new Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
        var badgeBounds = badge.TransformToVisual(MainNavigation).TransformBounds(new Rect(0, 0, badge.ActualWidth, badge.ActualHeight));
        var centerX = isUpdate ? iconBounds.X + iconBounds.Width / 2 : iconBounds.Right + 2;
        var centerY = isUpdate ? iconBounds.Y + iconBounds.Height / 2 : iconBounds.Bottom + 2;
        badge.RenderTransform = new TranslateTransform
        {
            X = centerX - (badgeBounds.X + badgeBounds.Width / 2),
            Y = centerY - (badgeBounds.Y + badgeBounds.Height / 2)
        };
    }
}
