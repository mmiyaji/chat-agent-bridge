using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private void OpenStartupSettings_Click(object sender, RoutedEventArgs e) => OpenAutomaticSettings(false);
    private void OpenAutoConnectSettings_Click(object sender, RoutedEventArgs e) => OpenAutomaticSettings(true);

    private void OpenAutomaticSettings(bool focusConnection)
    {
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .First(item => item.Tag is "overview");
        // Navigation first queues its scroll reset. Run the section jump after it.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsOverviewSelected) return;
            StartupSettingsCard.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = false
            });
            Control target = focusConnection ? AutoConnectProviderPicker : StartupToggle;
            if (target.IsEnabled) target.Focus(FocusState.Programmatic);
        });
    }

    public static Visibility ActionButtonsVisibility(bool selected, bool settingsLoaded) =>
        ToVisibility(selected && settingsLoaded);

    private void ConnectionsJump_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string section } } picker) return;
        Expander? target = section switch
        {
            "mattermost" => MattermostSettingsCard,
            "slack" => SlackSettingsCard,
            "approval" => ChatApprovalSettingsCard,
            "attachments" => FileAttachmentSettings,
            "codex-sharing" when ViewModel.IsCodexProvider => CodexChatSharingSettings,
            _ => null
        };
        picker.SelectedIndex = -1;
        // Let the choice popup close before scrolling its underlying page.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsConnectionsSelected || target is null) return;
            target.IsExpanded = true;
            target.UpdateLayout();
            target.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = false
            });
        });
    }

    private void AgentJump_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string section } } picker) return;
        Expander? target = section switch
        {
            "basic" => AgentBasicSettings,
            "common" => AgentCommonSettings,
            "model" when ViewModel.SupportsModel => AgentModelSettings,
            "permissions" when !ViewModel.IsBasicProvider => AgentPermissionsSettings,
            "codex" when ViewModel.IsCodexProvider => CodexSettings,
            "advanced" => AgentAdvancedSettings,
            "registrations" => AgentRegistrationsSettings,
            "explanation" => AgentExecutionExplanation,
            _ => null
        };
        picker.SelectedIndex = -1;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsAgentSelected || target is null) return;
            target.IsExpanded = true;
            target.UpdateLayout();
            target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        });
    }
}
