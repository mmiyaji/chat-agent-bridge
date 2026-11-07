using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private void UpdateSettingsNavigationEnabledState()
    {
        var enabled = CanUseForm(false, ViewModel.IsSettingsLoaded, ViewModel.IsLoading);
        if (_agentScopeNavigation is not null) _agentScopeNavigation.IsEnabled = enabled;
        if (_connectionScopeNavigation is not null) _connectionScopeNavigation.IsEnabled = enabled;
    }

    // The NavigationView owns the bounded menu pane (including its own overflow
    // scrolling); this viewer owns only the settings details in either pane mode.
    private static ScrollViewer CreateSettingsDetailScroll(UIElement content, string automationId)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            IsVerticalScrollChainingEnabled = false
        };
        AutomationProperties.SetAutomationId(scroll, automationId);
        return scroll;
    }

    private ScrollViewer? ScrollViewerForPage(string? tag) => tag switch
    {
        "agent" => _agentSettingsScroll,
        "connections" => _connectionSettingsScroll,
        _ => MainContentScroll
    };

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

    private void OpenAllowedUsers_Click(object sender, RoutedEventArgs e)
    {
        var provider = _connectionSettingsScope;
        SetConnectionSettingsScope("common");
        AllowedUsersProviderPicker.SelectedValue = provider;
        AllowedUsersSettingsCard.IsExpanded = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsConnectionsSelected) return;
            AllowedUsersSettingsCard.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = false
            });
        });
    }

    private void ShowAllowedUserHistory(object? sender, AllowedUserRow row)
    {
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "history");
        HistoryPage.ShowAccessHistory(row.Provider, row.UserId);
    }

    private async void ConfirmAllowedUserRemoval(object? sender, AllowedUserRow row)
    {
        if (_settingsPromptOpen || _sensitiveSettingsDialogOpen || _autoSaveAttemptRunning || XamlRoot is null) return;
        _settingsPromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("AllowedUserRemoveTitle"),
                Content = new TextBlock { Text = $"{row.ServiceName} · {row.Summary}", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = DesktopText.Get("AllowedUserRemoveAccept"),
                CloseButtonText = DesktopText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "AllowedUserRemoveConfirmation");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                row.RemoveCommand.Execute(null);
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Allowed user removal confirmation: {ex.GetType().Name}");
        }
        finally
        {
            _settingsPromptOpen = false;
            ScheduleSettingsAutoSave();
        }
    }

    private AllowedUserListModel SelectedAllowedUserModel => AllowedUsersProviderPicker.SelectedValue switch
    {
        "slack" => ViewModel.SlackAllowedUsers,
        "discord" => ViewModel.DiscordAllowedUsers,
        _ => ViewModel.MattermostAllowedUsers
    };

    private void AllowedUsersProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommonAllowedUserIdStatus is not null) UpdateAllowedUserAddFlyout();
    }

    private void AllowedUserAddFlyout_Opening(object sender, object e) => UpdateAllowedUserAddFlyout();

    private void UpdateAllowedUserAddFlyout()
    {
        var model = SelectedAllowedUserModel;
        CommonMattermostUsernameLookup.Visibility = ToVisibility(model.Provider == "mattermost");
        CommonSlackUsernameLookup.Visibility = ToVisibility(model.Provider == "slack");
        CommonAllowedUserIdInput.Text = model.NewUserId;
        CommonAllowedUserIdStatus.Text = "";
        CommonAllowedUserIdStatus.Visibility = Visibility.Collapsed;
    }

    private void AllowedUserAddFlyout_Closed(object sender, object e) => ScheduleSettingsAutoSave();

    private void CommonAllowedUserAdd_Click(object sender, RoutedEventArgs e)
    {
        var model = SelectedAllowedUserModel;
        var command = model.Provider == "mattermost"
            ? ViewModel.AddMattermostUsernameLookupResultCommand
            : ViewModel.AddSlackUsernameLookupResultCommand;
        if (!command.CanExecute(null)) return;
        var count = model.Rows.Count;
        command.Execute(null);
        if (model.Rows.Count > count) AllowedUserAddFlyout.Hide();
    }

    private void CommonAllowedUserAddById_Click(object sender, RoutedEventArgs e)
    {
        var model = SelectedAllowedUserModel;
        model.NewUserId = CommonAllowedUserIdInput.Text;
        model.Status = "";
        var count = model.Rows.Count;
        model.AddCommand.Execute(null);
        if (model.Rows.Count > count)
        {
            AllowedUserAddFlyout.Hide();
            return;
        }
        CommonAllowedUserIdStatus.Text = model.Status;
        CommonAllowedUserIdStatus.Visibility = ToVisibility(model.HasStatus);
    }

    private void ConnectionsJump_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string section } } picker) return;
        SetConnectionSettingsScope(section switch { "mattermost" => "mattermost", "slack" => "slack", "discord" => "discord", _ => "common" });
        Expander? target = section switch
        {
            "mattermost" => MattermostSettingsCard,
            "slack" => SlackSettingsCard,
            "discord" => DiscordSettingsCard,
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

    private async void AgentJump_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string section } } picker) return;
        picker.SelectedIndex = -1;
        if (section is "common" or "basic" or "explanation") await SelectAgentSettingsScopeAsync("common");
        else if (await SelectAgentSettingsScopeAsync(ViewModel.AgentProvider)) _agentProfileDetails?.OpenSection(section);
    }
}
