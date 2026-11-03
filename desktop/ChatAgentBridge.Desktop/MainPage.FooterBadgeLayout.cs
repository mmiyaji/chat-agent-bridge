using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private void QueueFooterBadgeLayout() => DispatcherQueue.TryEnqueue(UpdateFooterBadgeLayout);

    private void UpdateFooterBadgeLayout()
    {
        if (MainNavigation is null || AboutUpdateBadge is null || ConnectionStatusBadge is null) return;
        var compact = !MainNavigation.IsPaneOpen;
        // Native compact navigation moves InfoBadge to the upper right. These two
        // footer badges need distinct placements instead of overlapping their icons.
        var updatePositioned = ConfigureFooterBadge(AboutUpdateBadge, compact, isUpdate: true);
        AboutNavigationIcon.Opacity = compact && updatePositioned && AboutUpdateBadge.Visibility == Visibility.Visible ? 0 : 1;
        ConfigureFooterBadge(ConnectionStatusBadge, compact, isUpdate: false);
    }

    private static bool ConfigureFooterBadge(InfoBadge badge, bool compact, bool isUpdate)
    {
        var size = compact && !isUpdate ? 8 : 24;
        badge.Width = badge.Height = badge.MinWidth = badge.MinHeight = size;
        badge.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 4, 0);
        // Adjust the native presenter rather than copying NavigationViewItem's template.
        // If a future WinUI template renames it, the default badge remains functional.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(badge); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ContentPresenter { Name: "InfoBadgePresenter" } presenter) continue;
            presenter.HorizontalAlignment = compact
                ? (isUpdate ? HorizontalAlignment.Center : HorizontalAlignment.Right)
                : HorizontalAlignment.Stretch;
            presenter.VerticalAlignment = compact && !isUpdate ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            presenter.Margin = compact && !isUpdate ? new Thickness(0, 0, 4, 2) : new Thickness(0);
            return true;
        }
        return false;
    }
}
