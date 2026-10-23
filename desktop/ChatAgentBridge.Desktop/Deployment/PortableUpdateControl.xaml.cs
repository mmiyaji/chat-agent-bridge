using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Deployment;

public sealed partial class PortableUpdateControl : UserControl
{
    private bool _isSubscribed;
    private bool _isUpdatingPreference;
    private bool _savingPreference;
    private bool _dialogOpen;
    private bool _downloadRequested;

    public PortableUpdateControl()
    {
        InitializeComponent();
        Visibility = DeploymentRuntime.IsPortable ? Visibility.Visible : Visibility.Collapsed;
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (!DeploymentRuntime.IsPortable || _isSubscribed) return;
        App.PortableUpdates.Changed += PortableUpdates_Changed;
        _isSubscribed = true;
        RefreshState();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed) return;
        App.PortableUpdates.Changed -= PortableUpdates_Changed;
        _isSubscribed = false;
    }

    private void PortableUpdates_Changed(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshState);
            return;
        }

        RefreshState();
    }

    private void RefreshState()
    {
        if (!DeploymentRuntime.IsPortable) return;
        if (_isSubscribed && XamlRoot is { IsHostVisible: true } root &&
            App.PortableUpdates.WasRestoredStage && App.PortableUpdates.TryClaimRestoredStagePrompt())
            _ = PromptRestoredStageAsync(root);

        var updates = App.PortableUpdates;
        StatusText.Text = updates.Status;
        _isUpdatingPreference = true;
        if (!_savingPreference)
        {
            AutomaticChecksCheckBox.IsChecked = updates.AutomaticChecksEnabled;
            IncludePrereleasesCheckBox.IsChecked = updates.IncludePrereleases;
        }
        _isUpdatingPreference = false;
        AutomaticChecksCheckBox.IsEnabled = updates.IsSupported && !updates.IsBusy && !_savingPreference;
        IncludePrereleasesCheckBox.IsEnabled = updates.IsSupported && !updates.IsBusy && !_savingPreference;

        var hasUpdate = !string.IsNullOrWhiteSpace(updates.AvailableVersion);
        var updateAvailable = hasUpdate && !updates.IsDownloaded && !updates.IsBusy &&
            updates.Status == DesktopText.Get("PortableUpdateAvailable", updates.AvailableVersion);
        var updateReady = updates.IsDownloaded && !updates.IsBusy &&
            updates.Status == DesktopText.Get("PortableUpdateReady", updates.AvailableVersion ?? string.Empty);
        var showUpdateNotice = updateAvailable || updateReady;
        UpdateNotice.IsOpen = showUpdateNotice;
        UpdateNotice.Title = DesktopText.Get(updateReady ? "PortableUpdateNoticeReady" : "PortableUpdateNoticeAvailable");
        UpdateNotice.Message = updates.Status;
        UpdateNotice.Severity = updateReady ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        UpdateNoticeAction.Content = DesktopText.Get(updateReady ? "PortableUpdateApplyPrimary" : "PortableUpdateDownloadPrimary");
        AutomationProperties.SetName(UpdateNoticeAction, UpdateNoticeAction.Content?.ToString() ?? string.Empty);
        UpdateNoticeAction.IsEnabled = updates.IsSupported && !_dialogOpen && !_downloadRequested;
        StatusText.Visibility = showUpdateNotice ? Visibility.Collapsed : Visibility.Visible;

        CheckUpdatesButton.IsEnabled = updates.IsSupported && !updates.IsBusy && !_savingPreference && !_dialogOpen && !_downloadRequested;
        ReleaseNotesLink.NavigateUri = updates.AvailableReleasePage ?? new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases");
        AutomationProperties.SetHelpText(ReleaseNotesLink, ReleaseNotesLink.NavigateUri.AbsoluteUri);
        ToolTipService.SetToolTip(ReleaseNotesLink, ReleaseNotesLink.NavigateUri.AbsoluteUri);
        UpdateActions.Visibility = !showUpdateNotice && (hasUpdate || updates.IsBusy) ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.Visibility = hasUpdate && !updates.IsDownloaded && !updateAvailable ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = updates.IsSupported && hasUpdate && !updates.IsDownloaded && !updates.IsBusy && !_dialogOpen && !_downloadRequested;
        ApplyUpdateButton.Visibility = updates.IsDownloaded && !updateReady ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.IsEnabled = updates.IsSupported && updates.IsDownloaded && !updates.IsBusy && !_dialogOpen && !_downloadRequested;
        CancelUpdateButton.Visibility = updates.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.IsEnabled = updates.IsBusy;

        var downloading = updates.IsDownloading;
        DownloadProgressBar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgressBar.Value = Math.Clamp(updates.DownloadProgress, 0, 100);
    }

    private async void IncludePrereleases_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPreference || _savingPreference || IncludePrereleasesCheckBox.IsChecked is not bool includePrereleases) return;
        await SavePreferenceAsync(() => App.PortableUpdates.SetIncludePrereleasesAsync(includePrereleases));
    }

    private async void AutomaticChecks_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPreference || _savingPreference || AutomaticChecksCheckBox.IsChecked is not bool enabled) return;
        await SavePreferenceAsync(() => App.PortableUpdates.SetAutomaticChecksEnabledAsync(enabled));
    }

    private async Task SavePreferenceAsync(Func<Task> save)
    {
        _savingPreference = true;
        PreferenceError.Visibility = Visibility.Collapsed;
        RefreshState();
        try { await save(); }
        catch (Exception)
        {
            PreferenceError.Text = DesktopText.Get("PortableUpdatePreferenceSaveFailed");
            PreferenceError.Visibility = Visibility.Visible;
        }
        finally
        {
            _savingPreference = false;
            RefreshState();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try { await App.PortableUpdates.CheckAsync(); }
        catch (Exception) { RefreshState(); }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadRequested || _dialogOpen) return;
        _downloadRequested = true;
        // Keep the window's root: the user can visit another tab during download.
        var root = XamlRoot;
        try
        {
            await App.PortableUpdates.DownloadAsync();
            if (root is not null && App.PortableUpdates.IsDownloaded && !App.PortableUpdates.IsBusy)
                await ConfirmApplyUpdateAsync(root, downloadCompleted: true);
        }
        catch (Exception) { RefreshState(); }
        finally
        {
            _downloadRequested = false;
            RefreshState();
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (XamlRoot is { } root)
                await ConfirmApplyUpdateAsync(root, downloadCompleted: false);
        }
        catch (Exception) { RefreshState(); }
    }

    private void UpdateNoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (App.PortableUpdates.IsDownloaded)
            ApplyUpdate_Click(sender, e);
        else
            DownloadUpdate_Click(sender, e);
    }

    private async Task ConfirmApplyUpdateAsync(XamlRoot root, bool downloadCompleted)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        RefreshState();
        try
        {
            var updates = App.PortableUpdates;
            // An approval or settings dialog already on screen takes precedence.
            while (downloadCompleted && root.IsHostVisible && updates.IsDownloaded && !updates.IsBusy &&
                   VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.IsOpen))
                await Task.Delay(250);
            if (!root.IsHostVisible || !updates.IsDownloaded || updates.IsBusy) return;

            var canRestart = App.Window is MainWindow { CanRestartForUpdate: true };
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get(downloadCompleted ? "PortableUpdateRestartTitle" : "PortableUpdateApplyTitle"),
                Content = DesktopText.Get(canRestart ? "PortableUpdateRestartMessage" : "PortableUpdateRestartBusyMessage", updates.AvailableVersion),
                PrimaryButtonText = DesktopText.Get("PortableUpdateApplyPrimary"),
                CloseButtonText = DesktopText.Get("PortableUpdateLater"),
                IsPrimaryButtonEnabled = canRestart,
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "PortableUpdateApplyConfirmation");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await updates.ApplyAsync();
        }
        finally
        {
            _dialogOpen = false;
            RefreshState();
        }
    }

    private async Task PromptRestoredStageAsync(XamlRoot root)
    {
        // Let the window finish loading before opening a dialog; the confirmation path waits for existing popups.
        await Task.Yield();
        if (!root.IsHostVisible) return;
        await ConfirmApplyUpdateAsync(root, downloadCompleted: true);
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => App.PortableUpdates.Cancel();
}
