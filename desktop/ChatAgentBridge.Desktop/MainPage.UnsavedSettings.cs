using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _restoringSettingsNavigation;
    private bool _settingsNavigationPending;
    private bool _allowNextSettingsNavigation;
    private bool _settingsPromptOpen;

    private static bool IsSettingsSurface(string? tag) => tag is "overview" or "agent" or "connections";

    public static string SettingsSaveState(bool agent, bool chat, bool general)
    {
        var sections = new List<string>();
        if (agent) sections.Add(DesktopText.Get("SettingsDraftAgent"));
        if (chat) sections.Add(DesktopText.Get("SettingsDraftChat"));
        if (general) sections.Add(DesktopText.Get("SettingsDraftGeneral"));
        return sections.Count == 0 ? DesktopText.Get("SettingsDraftClean")
            : DesktopText.Get("SettingsDraftChanged", string.Join(" / ", sections));
    }

    private async void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringSettingsNavigation || args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        // The connection indicator is a shortcut to the inbox, rather than a
        // separate page. Keep its destination selected in the main menu too.
        if (tag == "chat" && sender.FooterMenuItems.Contains(item))
        {
            sender.SelectedItem = sender.MenuItems.OfType<NavigationViewItem>()
                .First(destination => destination.Tag as string == "chat");
            return;
        }
        if (_allowNextSettingsNavigation)
        {
            _allowNextSettingsNavigation = false;
            ApplyNavigationSelection(sender, args);
            return;
        }
        if (_settingsNavigationPending)
        {
            RestoreSettingsNavigation();
            return;
        }
        CommitSettingsNumberEdit();
        var leavingAgentDraft = _selectedNavigationTag == "agent" && tag != "agent" &&
            (_agentProfileDetails?.HasPendingEdits ?? false);
        if (_hasLoaded && !ViewModel.IsOnboardingActive &&
            (leavingAgentDraft || (!IsSettingsSurface(tag) && tag != _selectedNavigationTag && ViewModel.HasUnsavedSettings)))
        {
            // SelectionChanged cannot be cancelled. Restore the selection before
            // changing page visibility, then replay only the approved destination.
            RestoreSettingsNavigation();
            _settingsNavigationPending = true;
            sender.IsEnabled = false;
            try
            {
                if (await TryResolveUnsavedSettingsAsync())
                {
                    _allowNextSettingsNavigation = true;
                    sender.SelectedItem = item;
                }
            }
            finally
            {
                sender.IsEnabled = true;
                _settingsNavigationPending = false;
            }
            return;
        }
        ApplyNavigationSelection(sender, args);
    }

    private void RestoreSettingsNavigation()
    {
        _restoringSettingsNavigation = true;
        try
        {
            MainNavigation.SelectedItem = MainNavigation.MenuItems.Concat(MainNavigation.FooterMenuItems)
                .OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == _selectedNavigationTag);
        }
        finally { _restoringSettingsNavigation = false; }
    }

    private void CommitSettingsNumberEdit()
    {
        // NumberBox commits typed text on focus loss. A nonclient close button
        // does not move XAML focus, so commit before testing the draft state.
        if (XamlRoot is null) return;
        var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (current is not null)
        {
            if (current is NumberBox)
            {
                FocusManager.TryMoveFocus(FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = MainContentGrid });
                break;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    internal async Task<bool> TryResolveUnsavedSettingsAsync()
    {
        if (_settingsPromptOpen || _sensitiveSettingsDialogOpen) return false;
        if (_agentProfileDetails is { } editor)
        {
            if (editor.Editor.IsCheckingCli)
            {
                editor.Editor.CancelCliCheckCommand.Execute(null);
                if (editor.Editor.CheckCliCommand.ExecutionTask is { } pendingCheck) await pendingCheck;
            }
            editor.Editor.CancelModelCatalogLookup();
            if (!await editor.TryFlushAsync())
            {
                if (!editor.HasPendingEdits || XamlRoot is null) return false;
                _settingsPromptOpen = true;
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, RequestedTheme = App.Appearance.RequestedTheme,
                        Title = DesktopText.Get("SettingsDraftDialogTitle"),
                        Content = new TextBlock { Text = DesktopText.Get("SettingsDraftDialogMessage", DesktopText.Get("SettingsDraftAgent")), TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = DesktopText.Get("SettingsDraftSave"),
                        SecondaryButtonText = DesktopText.Get("SettingsDraftDiscard"),
                        CloseButtonText = DesktopText.Get("SettingsDraftCancel"), DefaultButton = ContentDialogButton.Close
                    };
                    AutomationProperties.SetAutomationId(dialog, "UnsavedAgentProfileDialog");
                    var choice = await dialog.ShowAsync();
                    if (choice == ContentDialogResult.None) return false;
                    if (choice == ContentDialogResult.Primary)
                    {
                        if (!await editor.TryFlushAsync()) return false;
                    }
                    else if (!await editor.Editor.DiscardPendingSettingsAsync()) return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Agent profile resolution: {ex.GetType().Name}");
                    return false;
                }
                finally { _settingsPromptOpen = false; }
            }
        }
        CommitSettingsNumberEdit();
        if (ViewModel.IsOnboardingActive || !ViewModel.HasUnsavedSettings) return true;
        if (XamlRoot is null) return false;
        _settingsPromptOpen = true;
        try
        {
            App.Window.AppWindow.Show();
            App.Window.Activate();
            _settingsAutoSaveTimer?.Stop();
            if (!ViewModel.IsSensitiveConfirmationPending)
            {
                if (await ViewModel.SavePendingSettingsAsync()) return true;
                // Cancelling the security confirmation keeps the current screen.
                if (ViewModel.IsSensitiveConfirmationPending || !ViewModel.HasUnsavedSettings) return false;
            }
            var body = new TextBlock
            {
                Text = DesktopText.Get("SettingsDraftDialogMessage", SettingsSaveState(
                    ViewModel.HasAgentSettingsChanges, ViewModel.HasChatSettingsChanges, ViewModel.HasGeneralSettingsChanges)),
                TextWrapping = TextWrapping.Wrap
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("SettingsDraftDialogTitle"),
                Content = body,
                PrimaryButtonText = DesktopText.Get("SettingsDraftSave"),
                SecondaryButtonText = DesktopText.Get("SettingsDraftDiscard"),
                CloseButtonText = DesktopText.Get("SettingsDraftCancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "UnsavedSettingsDialog");
            AutomationProperties.SetAutomationId(body, "UnsavedSettingsDialogMessage");
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return false;
            var resolved = result == ContentDialogResult.Primary
                ? await ViewModel.SavePendingSettingsAsync(retrySensitiveConfirmation: true)
                : await ViewModel.DiscardPendingSettingsAsync();
            if (!resolved && ViewModel.StatusSeverity != "Error") ShowSettingsDraftFailure();
            return resolved;
        }
        catch (Exception ex)
        {
            // Another ContentDialog may already be active. Never lose a draft
            // or proceed with navigation/exit when its decision is unavailable.
            System.Diagnostics.Debug.WriteLine($"Unsaved settings: {ex.GetType().Name}");
            ShowSettingsDraftFailure();
            return false;
        }
        finally { _settingsPromptOpen = false; }
    }

    private void ShowSettingsDraftFailure()
    {
        ViewModel.StatusTitle = DesktopText.Get("SettingsDraftResolutionFailedTitle");
        ViewModel.StatusMessage = DesktopText.Get("SettingsDraftResolutionFailedMessage");
        ViewModel.StatusSeverity = "Warning";
        ViewModel.IsStatusOpen = true;
    }

    private async void StartOnboarding_Click(object sender, RoutedEventArgs e)
    {
        if (await TryResolveUnsavedSettingsAsync() && ViewModel.StartOnboardingCommand.CanExecute(null))
            await ViewModel.StartOnboardingCommand.ExecuteAsync(null);
    }
}
