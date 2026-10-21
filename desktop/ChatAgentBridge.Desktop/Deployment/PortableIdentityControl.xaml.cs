using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Deployment;

public sealed partial class PortableIdentityControl : UserControl
{
    private bool _subscribed;
    private bool _operationBusy;
    private bool _refreshing;
    private bool _dialogOpen;

    public PortableIdentityControl()
    {
        InitializeComponent();
        Visibility = DeploymentRuntime.IsPortable ? Visibility.Visible : Visibility.Collapsed;
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
    }

    private async void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (!DeploymentRuntime.IsPortable) return;
        if (!_subscribed)
        {
            App.PortableIdentity.Changed += PortableIdentity_Changed;
            _subscribed = true;
        }
        await RefreshAsync();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        App.PortableIdentity.Changed -= PortableIdentity_Changed;
        _subscribed = false;
    }

    private void PortableIdentity_Changed(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshState);
            return;
        }
        RefreshState();
    }

    private async Task RefreshAsync()
    {
        if (!DeploymentRuntime.IsPortable || _operationBusy || _refreshing) return;
        _refreshing = true;
        RefreshState();
        try { await App.PortableIdentity.RefreshAsync(); }
        catch (Exception) { /* Coordinator exposes its current actionable status. */ }
        finally
        {
            _refreshing = false;
            RefreshState();
        }
    }

    private void RefreshState()
    {
        if (!DeploymentRuntime.IsPortable) return;
        var identity = App.PortableIdentity;
        IdentityExpander.Header = DesktopText.Get("PortableIdentityHeading.Header");
        IdentityExpander.Description = DesktopText.Get("PortableIdentityHeading.Description");
        StatusInfoBar.Title = DesktopText.Get("PortableIdentityStatusLabel");
        StatusInfoBar.Message = identity.Status;
        StatusInfoBar.Severity = identity.IsActive ? InfoBarSeverity.Success : InfoBarSeverity.Informational;

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(identity.Publisher))
            details.Add(DesktopText.Get("PortableIdentityPublisher", identity.Publisher));
        if (!string.IsNullOrWhiteSpace(identity.CertificateFingerprint))
            details.Add(DesktopText.Get("PortableIdentityFingerprint", identity.CertificateFingerprint));
        if (!string.IsNullOrWhiteSpace(identity.RegisteredLocation))
            details.Add(DesktopText.Get("PortableIdentityRegisteredLocation", identity.RegisteredLocation));
        details.Add(DesktopText.Get(identity.IsRegistered ? "PortableIdentityRegistered" : "PortableIdentityNotRegistered"));
        details.Add(DesktopText.Get(identity.IsActive ? "PortableIdentityActive" : "PortableIdentityInactive"));
        IdentityDetails.Text = string.Join(Environment.NewLine, details);

        RestartHelp.Text = DesktopText.Get("PortableIdentityRestartHelp");
        OperationProgress.IsActive = _operationBusy || _refreshing || identity.IsBusy;
        OperationProgress.Visibility = OperationProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        ConfigureButton.IsEnabled = identity.CanConfigure && !_operationBusy && !_refreshing && !identity.IsBusy && !_dialogOpen;
        RemoveButton.Visibility = identity.CanUnregister ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = identity.CanUnregister && !_operationBusy && !_refreshing && !identity.IsBusy && !_dialogOpen;
        RefreshButton.IsEnabled = !_operationBusy && !_refreshing && !identity.IsBusy && !_dialogOpen;
        ConfigureButton.Content = DesktopText.Get(identity.IsRegistered ? "PortableIdentityConfigureAgain" : "PortableIdentityConfigure");
        RemoveButton.Content = DesktopText.Get("PortableIdentityRemove");
        RefreshButton.Content = DesktopText.Get("PortableIdentityRefresh");
        StateHeading.Text = DesktopText.Get("PortableIdentityStateHeading");
        ActionsHeading.Text = DesktopText.Get("PortableIdentityActionsHeading");
        AutomationProperties.SetName(StatusInfoBar, StatusInfoBar.Title + ". " + StatusInfoBar.Message);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _operationBusy) return;
        await RefreshAsync();
    }

    private async void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _operationBusy || _refreshing || !App.PortableIdentity.CanConfigure) return;
        _dialogOpen = true;
        RefreshState();
        try
        {
            await App.PortableIdentity.RefreshAsync();
            RefreshState();
            var identity = App.PortableIdentity;
            if (!identity.CanConfigure || identity.IsBusy) return;

            CheckBox? trustConsent = null;
            CheckBox? relocationConsent = null;
            var content = new StackPanel { Spacing = 12 };
            var restartText = new TextBlock { Text = DesktopText.Get("PortableIdentityConfigureRestart"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(restartText, "PortableIdentityConfigureRestartText");
            content.Children.Add(restartText);
            var publisherText = new TextBlock { Text = DesktopText.Get("PortableIdentityPublisher", identity.Publisher), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(publisherText, "PortableIdentityConsentPublisher");
            content.Children.Add(publisherText);
            var fingerprintText = new TextBlock { Text = DesktopText.Get("PortableIdentityFingerprint", identity.CertificateFingerprint), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(fingerprintText, "PortableIdentityConsentFingerprint");
            content.Children.Add(fingerprintText);

            if (identity.NeedsCertificateTrust)
            {
                trustConsent = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = DesktopText.Get("PortableIdentityTrustConsent", identity.Publisher),
                        TextWrapping = TextWrapping.Wrap
                    },
                    IsChecked = false
                };
                AutomationProperties.SetAutomationId(trustConsent, "PortableIdentityTrustConsent");
                AutomationProperties.SetName(trustConsent, DesktopText.Get("PortableIdentityTrustConsent", identity.Publisher));
                content.Children.Add(trustConsent);
            }

            if (identity.HasOtherLocation)
            {
                var locationWarning = new TextBlock
                {
                    Text = DesktopText.Get("PortableIdentityRelocationWarning", identity.RegisteredLocation ?? string.Empty),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                };
                AutomationProperties.SetAutomationId(locationWarning, "PortableIdentityRelocationWarning");
                content.Children.Add(locationWarning);
                relocationConsent = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = DesktopText.Get("PortableIdentityRelocationConsent"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    IsChecked = false
                };
                AutomationProperties.SetAutomationId(relocationConsent, "PortableIdentityRelocationConsent");
                content.Children.Add(relocationConsent);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("PortableIdentityConfigureTitle"),
                Content = content,
                PrimaryButtonText = DesktopText.Get("PortableIdentityConfigurePrimary"),
                CloseButtonText = DesktopText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "PortableIdentityConfigureConfirmation");
            dialog.IsPrimaryButtonEnabled = IsConsentSatisfied(identity.NeedsCertificateTrust, identity.HasOtherLocation, trustConsent, relocationConsent);
            void UpdateConsent(object? _, RoutedEventArgs __) => dialog.IsPrimaryButtonEnabled = IsConsentSatisfied(
                identity.NeedsCertificateTrust, identity.HasOtherLocation, trustConsent, relocationConsent);
            if (trustConsent is not null)
            {
                trustConsent.Checked += UpdateConsent;
                trustConsent.Unchecked += UpdateConsent;
            }
            if (relocationConsent is not null)
            {
                relocationConsent.Checked += UpdateConsent;
                relocationConsent.Unchecked += UpdateConsent;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunOperationAsync(() => App.PortableIdentity.ConfigureAsync(
                trustConsent?.IsChecked == true, relocationConsent?.IsChecked == true));
        }
        catch (Exception) { RefreshState(); }
        finally
        {
            _dialogOpen = false;
            RefreshState();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _operationBusy || _refreshing || !App.PortableIdentity.CanUnregister) return;
        _dialogOpen = true;
        RefreshState();
        try
        {
            var message = new StackPanel { Spacing = 8 };
            var warning = new TextBlock { Text = DesktopText.Get("PortableIdentityRemoveMessage"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(warning, "PortableIdentityRemoveMessage");
            message.Children.Add(warning);
            var retention = new TextBlock
            {
                Text = DesktopText.Get("PortableIdentityRemoveRetained"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            AutomationProperties.SetAutomationId(retention, "PortableIdentityRemoveRetention");
            message.Children.Add(retention);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("PortableIdentityRemoveTitle"),
                Content = message,
                PrimaryButtonText = DesktopText.Get("PortableIdentityRemovePrimary"),
                CloseButtonText = DesktopText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "PortableIdentityRemoveConfirmation");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await RunOperationAsync(() => App.PortableIdentity.RemoveAsync());
        }
        catch (Exception) { RefreshState(); }
        finally
        {
            _dialogOpen = false;
            RefreshState();
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        _operationBusy = true;
        RefreshState();
        try { await operation(); }
        catch (Exception) { /* Coordinator stores an actionable localized status. */ }
        finally
        {
            _operationBusy = false;
            RefreshState();
        }
    }

    private static bool IsConsentSatisfied(bool needsTrust, bool hasOtherLocation, CheckBox? trust, CheckBox? relocate) =>
        (!needsTrust || trust?.IsChecked == true) && (!hasOtherLocation || relocate?.IsChecked == true);
}
