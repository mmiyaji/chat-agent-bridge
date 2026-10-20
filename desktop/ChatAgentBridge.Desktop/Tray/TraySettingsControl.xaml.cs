using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Tray;

public sealed partial class TraySettingsControl : UserControl
{
    private bool isInitializing = true;
    private bool isSubscribed;
    private bool isSaving;

    public TraySettingsControl()
    {
        InitializeComponent();
        Loaded += TraySettingsControl_Loaded;
        Unloaded += TraySettingsControl_Unloaded;
    }

    private void TraySettingsControl_Loaded(object sender, RoutedEventArgs e)
    {
        isInitializing = true;
        CloseToTrayToggle.IsOn = App.Tray.CloseToTray;
        isInitializing = false;

        if (!isSubscribed)
        {
            App.Tray.AvailabilityChanged += Tray_AvailabilityChanged;
            isSubscribed = true;
        }

        RefreshAvailability();
    }

    private void TraySettingsControl_Unloaded(object sender, RoutedEventArgs e)
    {
        if (isSubscribed)
        {
            App.Tray.AvailabilityChanged -= Tray_AvailabilityChanged;
            isSubscribed = false;
        }
    }

    private async void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (isInitializing)
        {
            return;
        }

        if (isSaving)
        {
            isInitializing = true;
            CloseToTrayToggle.IsOn = App.Tray.CloseToTray;
            isInitializing = false;
            return;
        }

        bool previousValue = App.Tray.CloseToTray;
        bool requestedValue = CloseToTrayToggle.IsOn;
        isSaving = true;
        CloseToTrayToggle.IsEnabled = false;
        StatusText.Text = string.Empty;

        try
        {
            await App.Tray.SetCloseToTrayAsync(requestedValue);
        }
        catch
        {
            isInitializing = true;
            CloseToTrayToggle.IsOn = previousValue;
            isInitializing = false;
            StatusText.Text = GetString("TraySaveFailed");
        }
        finally
        {
            isInitializing = true;
            CloseToTrayToggle.IsOn = App.Tray.CloseToTray;
            isInitializing = false;
            isSaving = false;
            CloseToTrayToggle.IsEnabled = true;
        }
    }

    private void MoveToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Tray.IsAvailable)
        {
            App.Tray.HideWindow();
        }
    }

    private async void ExitCompletelyButton_Click(object sender, RoutedEventArgs e)
    {
        ExitCompletelyButton.IsEnabled = false;
        try
        {
            await App.Tray.ExitAsync();
        }
        catch
        {
            StatusText.Text = GetString("TrayExitFailed");
            ExitCompletelyButton.IsEnabled = true;
        }
    }

    private void Tray_AvailabilityChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) RefreshAvailability();
        else DispatcherQueue.TryEnqueue(RefreshAvailability);
    }

    private void RefreshAvailability()
    {
        bool isAvailable = App.Tray.IsAvailable;
        MoveToTrayButton.IsEnabled = isAvailable;
        if (!isAvailable)
        {
            StatusText.Text = GetString("TrayUnavailable");
        }
        else if (StatusText.Text == GetString("TrayUnavailable"))
        {
            // Preserve save and exit errors while the tray is available.
            StatusText.Text = string.Empty;
        }
    }

    private static string GetString(string resourceName) =>
        MrtText.Get(resourceName);
}
