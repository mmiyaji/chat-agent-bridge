using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private BreadcrumbBar? _agentScopeBreadcrumb;
    private BreadcrumbBar? _connectionScopeBreadcrumb;

    // Gallery-style trail above a provider page, such as "Chat settings › Mattermost". The first crumb returns
    // to the shared settings scope; the trail is hidden on the shared scope itself, where it would only repeat the title.
    private static BreadcrumbBar CreateScopeBreadcrumb(string automationId, Action openCommonScope)
    {
        var breadcrumb = new BreadcrumbBar { Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(breadcrumb, automationId);
        breadcrumb.ItemClicked += (_, args) =>
        {
            if (args.Index == 0) openCommonScope();
        };
        return breadcrumb;
    }

    private void UpdateScopeBreadcrumb(BreadcrumbBar? breadcrumb, string pageTag, NavigationView? scopes, bool isCommonScope)
    {
        if (breadcrumb is null) return;
        var page = MainNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == pageTag)?.Content as string;
        var scope = scopes?.SelectedItem is NavigationViewItem selected ? AutomationProperties.GetName(selected) : null;
        var show = !isCommonScope && !string.IsNullOrEmpty(page) && !string.IsNullOrEmpty(scope);
        breadcrumb.ItemsSource = show ? new[] { page!, scope! } : null;
        AutomationProperties.SetName(breadcrumb, show ? $"{page} › {scope}" : string.Empty);
        breadcrumb.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
