using ChatAgentBridge.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop.Tray;

/// <summary>Owns window visibility; hiding never tears down the chat or notification lifetime.</summary>
public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly BackgroundPreferencesStore _store;
    private readonly NativeTrayIcon _native;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _exitTask;
    private bool _allowClose;
    private bool _disposed;

    public bool CloseToTray { get; private set; }
    public bool IsAvailable => !_disposed && _exitTask is null && _native.IsAvailable;
    public event EventHandler? AvailabilityChanged;

    public TrayService(MainWindow window, BackgroundPreferencesStore store, BackgroundPreferences preferences)
    {
        _window = window;
        _store = store;
        CloseToTray = preferences.CloseToTray;
        _native = new(WinRT.Interop.WindowNative.GetWindowHandle(window), ShowWindow, () => _ = ExitAsync());
        _native.AvailabilityChanged += NativeAvailabilityChanged;
        window.AppWindow.Closing += WindowClosing;
    }

    public async Task SetCloseToTrayAsync(bool enabled)
    {
        await _saveGate.WaitAsync();
        try
        {
            await _store.SaveAsync(new(enabled));
            CloseToTray = enabled;
        }
        finally { _saveGate.Release(); }
    }

    public void HideWindow()
    {
        // A failed shell registration must never strand an invisible application.
        if (IsAvailable) _window.AppWindow.Hide();
    }

    public void ShowWindow()
    {
        if (_disposed || _exitTask is not null) return;
        _window.AppWindow.Show();
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.Activate();
    }

    private void NativeAvailabilityChanged()
    {
        if (!_native.IsAvailable) ShowWindow();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _native.SessionEnding) return;
        args.Cancel = true;
        if (CloseToTray && IsAvailable) HideWindow();
        else _ = ExitAsync();
    }

    public Task ExitAsync() => _exitTask ??= ExitCoreAsync();

    private async Task ExitCoreAsync()
    {
        // Publish the task before callbacks or a second close can re-enter.
        await Task.Yield();
        try
        {
            if (_window.Content is UIElement content) content.IsHitTestVisible = false;
            App.PendingRequestNotifications.ClearAll();
            // Cancellation kills CLI process trees and stops reconnects immediately.
            // Allow a bounded grace period for history writes and transport disposal.
            await Task.WhenAll(_window.ShutdownAsync(), DrainPreferenceSaveAsync())
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Application shutdown: {ex.GetType().Name}");
        }
        finally
        {
            _allowClose = true;
            _window.Close();
        }
    }

    private async Task DrainPreferenceSaveAsync()
    {
        await _saveGate.WaitAsync();
        _saveGate.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.AppWindow.Closing -= WindowClosing;
        _native.AvailabilityChanged -= NativeAvailabilityChanged;
        _native.Dispose();
        // An in-flight preference save can still release this gate during exit.
    }
}
