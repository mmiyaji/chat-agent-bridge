using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.Services.Store;
using Windows.System;
using ChatAgentBridge_Desktop.Deployment;

namespace ChatAgentBridge_Desktop.About;

public sealed partial class AboutPage : Page
{
    private readonly bool _isStorePackage;
    private string? _storeUri;
    private bool _storeCheckInProgress;
    private bool _dialogOpen;

    public AboutPage()
    {
        InitializeComponent();
        string version;
        string packageName;
        if (DeploymentRuntime.IsPortable)
        {
            version = DeploymentRuntime.GetAssemblyVersion();
            packageName = "Portable";
        }
        else
        {
            try
            {
                var package = Package.Current;
                var packageVersion = package.Id.Version;
                version = $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
                packageName = package.Id.Name;
                _isStorePackage = package.SignatureKind == PackageSignatureKind.Store;
                if (_isStorePackage)
                    _storeUri = $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(package.Id.FamilyName)}";
            }
            catch (InvalidOperationException)
            {
                version = DeploymentRuntime.GetAssemblyVersion();
                packageName = DesktopText.Get("AboutUnpackaged");
            }
        }

        ProductValue.Text = DesktopText.Get("AboutProductName");
        VersionValue.Text = version;
        PackageValue.Text = packageName;
        StoreStatus.Text = _isStorePackage ? DesktopText.Get("AboutStoreReady") : DesktopText.Get("AboutStoreDevelopment");
        StorePageButton.IsEnabled = _isStorePackage;
        CheckUpdatesButton.IsEnabled = _isStorePackage;
        StoreUpdateCard.Visibility = DeploymentRuntime.IsPortable ? Visibility.Collapsed : Visibility.Visible;
        PackageReleaseNotesLink.Visibility = DeploymentRuntime.IsPortable ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ResetScroll() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => AboutScroll.ChangeView(null, 0, null, true));

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard { Tag: string uri } &&
            uri is "https://ruhenheim.org" or "https://github.com/mmiyaji/chat-agent-bridge/issues")
        {
            try
            {
                ActionStatus.Text = await Launcher.LaunchUriAsync(new Uri(uri))
                    ? string.Empty : DesktopText.Get("AboutOpenLinkFailed");
            }
            catch (Exception) { ActionStatus.Text = DesktopText.Get("AboutOpenLinkFailed"); }
            ActionStatus.Visibility = string.IsNullOrEmpty(ActionStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OpenStore_Click(object sender, RoutedEventArgs e)
    {
        if (!_isStorePackage || _storeUri is null) return;
        try
        {
            StoreStatus.Text = await Launcher.LaunchUriAsync(new Uri(_storeUri))
                ? DesktopText.Get("AboutStoreReady") : DesktopText.Get("AboutStoreOpenFailed");
        }
        catch (Exception) { StoreStatus.Text = DesktopText.Get("AboutStoreOpenFailed"); }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!_isStorePackage || _storeCheckInProgress) return;
        _storeCheckInProgress = true;
        CheckUpdatesButton.IsEnabled = false;
        StoreStatus.Text = DesktopText.Get("AboutStoreChecking");
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, App.WindowHandle);
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            StoreStatus.Text = updates.Count == 0
                ? DesktopText.Get("AboutStoreNoUpdates")
                : DesktopText.Get("AboutStoreUpdatesAvailable");
        }
        catch (Exception)
        {
            StoreStatus.Text = DesktopText.Get("AboutStoreCheckFailed");
        }
        finally
        {
            _storeCheckInProgress = false;
            CheckUpdatesButton.IsEnabled = _isStorePackage;
        }
    }

    private async void Terms_Click(object sender, RoutedEventArgs e) => await Onboarding.PolicyDialog.ShowAsync(XamlRoot, privacy: false);
    private async void Privacy_Click(object sender, RoutedEventArgs e) => await Onboarding.PolicyDialog.ShowAsync(XamlRoot, privacy: true);

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("AboutExitTitle"),
                Content = DesktopText.Get("AboutExitMessage"),
                PrimaryButtonText = DesktopText.Get("AboutExitConfirm"),
                CloseButtonText = DesktopText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "AboutExitConfirmation");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await App.Tray.ExitAsync();
        }
        finally { _dialogOpen = false; }
    }
}
