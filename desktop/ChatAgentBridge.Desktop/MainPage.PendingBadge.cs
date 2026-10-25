using ChatAgentBridge_Desktop.Inbox;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    // Requests waiting for a decision stay visible from any page as a count on the sidebar Chat item.
    private void InitializePendingBadge()
    {
        var chat = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");
        // The sidebar gives labels the item's full width, so keep the badge off the highlight's right edge.
        var badge = new InfoBadge { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(badge, "NavChatPendingBadge");
        chat.InfoBadge = badge;
        void Update()
        {
            var count = InboxPage.ViewModel.PendingApprovalCount;
            badge.Value = count;
            badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(badge, DesktopText.Get("NavChatPendingBadge", count));
        }
        InboxPage.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatInboxViewModel.PendingApprovalCount)) Update();
        };
        Update();
    }
}
