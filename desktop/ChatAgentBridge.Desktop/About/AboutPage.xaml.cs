using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Reflection;
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
    private bool _licensesOpen;

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
        // The header is a text block, so name the expander for screen readers: product and version.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AppDetailsExpander, $"{ProductValue.Text} {version}");
        BuildTimestampValue.Text = GetBuildTimestampText();
        PackageValue.Text = packageName;
        StoreStatus.Text = _isStorePackage ? DesktopText.Get("AboutStoreReady") : DesktopText.Get("AboutStoreDevelopment");
        StorePageButton.IsEnabled = _isStorePackage;
        CheckUpdatesButton.IsEnabled = _isStorePackage;
        StoreUpdateCard.Visibility = DeploymentRuntime.IsPortable ? Visibility.Collapsed : Visibility.Visible;
        PackageReleaseNotesLink.Visibility = DeploymentRuntime.IsPortable ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GetBuildTimestampText()
    {
        var timestampValue = typeof(AboutPage).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimestampUtc")?.Value;
        if (!DateTimeOffset.TryParse(timestampValue, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestampUtc))
            return DesktopText.Get("AboutBuildTimestampUnknown");

        return DesktopText.Get("AboutBuildTimestamp", timestampUtc.ToLocalTime());
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

    private async void ThirdPartyLicenses_Click(object sender, RoutedEventArgs e)
    {
        if (_licensesOpen) return;
        _licensesOpen = true;
        try
        {
            await ThirdPartyLicensesDialog.ShowAsync(XamlRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ActionStatus.Text = DesktopText.Get("ThirdPartyLicensesReadFailed");
            ActionStatus.Visibility = Visibility.Visible;
            ActionStatus.StartBringIntoView();
        }
        finally { _licensesOpen = false; }
    }

    private async void Exit_Click(object sender, RoutedEventArgs e) => await App.Tray.ShowCloseConfirmationAsync();
}
