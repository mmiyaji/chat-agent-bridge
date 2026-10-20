using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Deployment;

public sealed partial class PortableUpdateControl : UserControl
{
    private bool _isSubscribed;
    private bool _isUpdatingPreference;
    private bool _savingPreference;
    private bool _dialogOpen;

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
        CheckUpdatesButton.IsEnabled = updates.IsSupported && !updates.IsBusy && !_savingPreference;
        ReleaseNotesLink.NavigateUri = updates.AvailableReleasePage ?? new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases");
        AutomationProperties.SetHelpText(ReleaseNotesLink, ReleaseNotesLink.NavigateUri.AbsoluteUri);
        ToolTipService.SetToolTip(ReleaseNotesLink, ReleaseNotesLink.NavigateUri.AbsoluteUri);
        UpdateActions.Visibility = hasUpdate || updates.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.Visibility = hasUpdate && !updates.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = updates.IsSupported && hasUpdate && !updates.IsDownloaded && !updates.IsBusy;
        ApplyUpdateButton.Visibility = updates.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.IsEnabled = updates.IsSupported && updates.IsDownloaded && !updates.IsBusy;
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
        try { await App.PortableUpdates.DownloadAsync(); }
        catch (Exception) { RefreshState(); }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("PortableUpdateApplyTitle"),
                Content = DesktopText.Get("PortableUpdateApplyMessage"),
                PrimaryButtonText = DesktopText.Get("PortableUpdateApplyPrimary"),
                CloseButtonText = DesktopText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "PortableUpdateApplyConfirmation");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await App.PortableUpdates.ApplyAsync();
        }
        catch (Exception) { RefreshState(); }
        finally { _dialogOpen = false; }
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => App.PortableUpdates.Cancel();
}
