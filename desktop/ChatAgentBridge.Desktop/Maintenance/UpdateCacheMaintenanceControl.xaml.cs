using ChatAgentBridge_Desktop.Deployment;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Maintenance;

public sealed partial class UpdateCacheMaintenanceControl : UserControl
{
    private bool _isSubscribed;
    private bool _cleaning;

    public UpdateCacheMaintenanceControl()
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
        CleanCacheButton.IsEnabled = App.PortableUpdates.IsSupported && !App.PortableUpdates.IsBusy && !_cleaning;
    }

    private async void CleanCache_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaning || App.PortableUpdates.IsBusy) return;
        _cleaning = true;
        CacheCleanupStatus.Visibility = Visibility.Visible;
        CacheCleanupStatus.Text = DesktopText.Get("PortableUpdateCacheCleaning");
        RefreshState();
        try
        {
            var result = await App.PortableUpdates.CleanCacheAsync();
            CacheCleanupStatus.Text = DesktopText.Get("PortableUpdateCacheCleaned", result.RemovedBytes / (1024d * 1024d));
        }
        catch (OperationCanceledException)
        {
            CacheCleanupStatus.Text = DesktopText.Get("PortableUpdateCacheCleanupCanceled");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            CacheCleanupStatus.Text = DesktopText.Get("PortableUpdateCacheCleanupFailed");
        }
        finally
        {
            _cleaning = false;
            RefreshState();
        }
    }
}
