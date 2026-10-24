using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge.Core;
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
        CloseBehaviorComboBox.SelectedIndex = App.Tray.CloseBehavior switch
        {
            WindowCloseBehavior.MoveToSystemTray => 1,
            WindowCloseBehavior.ExitApp => 2,
            _ => 0
        };
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

    private void CloseBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || CloseBehaviorComboBox.SelectedIndex < 0) return;
        if (isSaving) return;
        isSaving = true;
        // Let ComboBox finish selecting/collapsing before changing its enabled state.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            async () => await SaveCloseBehaviorAsync()))
        {
            isSaving = false;
            RestoreSelectedBehavior();
            StatusText.Text = GetString("TraySaveFailed");
        }
    }

    private async Task SaveCloseBehaviorAsync()
    {
        var requestedValue = CloseBehaviorComboBox.SelectedIndex switch
        {
            1 => WindowCloseBehavior.MoveToSystemTray,
            2 => WindowCloseBehavior.ExitApp,
            _ => WindowCloseBehavior.AskEveryTime
        };
        CloseBehaviorComboBox.IsEnabled = false;
        StatusText.Text = string.Empty;

        try
        {
            await App.Tray.SetCloseBehaviorAsync(requestedValue);
        }
        catch
        {
            RestoreSelectedBehavior();
            StatusText.Text = GetString("TraySaveFailed");
        }
        finally
        {
            RestoreSelectedBehavior();
            isSaving = false;
            CloseBehaviorComboBox.IsEnabled = true;
        }
    }

    private void RestoreSelectedBehavior()
    {
        isInitializing = true;
        CloseBehaviorComboBox.SelectedIndex = App.Tray.CloseBehavior switch
        {
            WindowCloseBehavior.MoveToSystemTray => 1,
            WindowCloseBehavior.ExitApp => 2,
            _ => 0
        };
        isInitializing = false;
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
