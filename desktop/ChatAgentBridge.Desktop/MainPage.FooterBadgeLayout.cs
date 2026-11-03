using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _footerBadgeLayoutQueued;
    private bool _footerPaneTransition;
    private bool _footerBadgesNeedReveal;

    private void MainNavigation_PaneTransitionStarting(NavigationView sender, object args)
    {
        _footerPaneTransition = true;
        _footerBadgesNeedReveal = true;
        foreach (var badge in new[] { AboutUpdateBadge, ConnectionStatusBadge })
        {
            if (badge is null) continue;
            var visual = ElementCompositionPreview.GetElementVisual(badge);
            visual.StopAnimation("Opacity");
            visual.Opacity = 0;
        }
    }

    private void RevealFooterBadge(InfoBadge badge)
    {
        var visual = ElementCompositionPreview.GetElementVisual(badge);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (!_uiSettings.AnimationsEnabled) return;
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(100);
        visual.StartAnimation("Opacity", fade);
    }

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
        if (_footerPaneTransition || MainNavigation is null || AboutUpdateBadge is null || ConnectionStatusBadge is null) return;
        var compact = !MainNavigation.IsPaneOpen;
        var hasUpdate = AboutUpdateBadge.Visibility == Visibility.Visible;
        AboutNavigationIcon.Opacity = compact && hasUpdate ? 0 : 1;
        PositionFooterBadge(AboutUpdateBadge, AboutNavigationIcon, compact, isUpdate: true);
        if (NavChatConnectionStatus.Icon is { } serviceIcon)
            PositionFooterBadge(ConnectionStatusBadge, serviceIcon, compact, isUpdate: false);
        if (_footerBadgesNeedReveal)
        {
            _footerBadgesNeedReveal = false;
            RevealFooterBadge(AboutUpdateBadge);
            RevealFooterBadge(ConnectionStatusBadge);
        }
    }

    private void PositionFooterBadge(InfoBadge badge, IconElement icon, bool compact, bool isUpdate)
    {
        badge.Width = badge.MinWidth = isUpdate ? 24 : 8;
        badge.Height = badge.MinHeight = compact && !isUpdate ? 8 : 24;
        // The 8px dot + 12px right gap has the same center as the 24px update
        // icon + 4px gap, while leaving room for the translated state label.
        badge.Margin = compact ? new Thickness(0) : new Thickness(0, 0, isUpdate ? 4 : 12, 0);
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
