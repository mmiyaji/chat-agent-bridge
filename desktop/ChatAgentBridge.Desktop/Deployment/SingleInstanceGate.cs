using ChatAgentBridge.Core.Deployment;
using Microsoft.Windows.AppLifecycle;

namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>
/// Keeps one live window per data folder. A later launch on the same folder hands its activation to
/// the running window, which comes to the front (even from the tray), and then exits without loading anything.
/// </summary>
internal static class SingleInstanceGate
{
    private static string? _key;
    private static Action? _showWindow;
    private static bool _showPending;

    /// <summary>Returns true when this process owns the folder; false after the launch was handed to the owner.</summary>
    public static async Task<bool> ClaimOrRedirectAsync(string dataDirectory)
    {
        _key = AppInstanceKey.For(dataDirectory);
        var owner = AppInstance.FindOrRegisterForKey(_key);
        if (owner.IsCurrent)
        {
            owner.Activated += OwnerActivated;
            return true;
        }
        // The user just started this process, so it may pass its foreground right to the running window.
        Native.AllowSetForegroundWindow(owner.ProcessId);
        await owner.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        return false;
    }

    /// <summary>Sets how a redirected launch shows the window; one that arrived during startup is applied now.</summary>
    public static void SetShowWindow(Action showWindow)
    {
        _showWindow = showWindow;
        if (_showPending)
        {
            _showPending = false;
            showWindow();
        }
    }

    /// <summary>Frees the folder for a relaunch of this process, such as a language restart.</summary>
    public static void Release() => AppInstance.GetCurrent().UnregisterKey();

    /// <summary>Takes the folder back when a relaunch did not happen.</summary>
    public static void Reclaim()
    {
        if (_key is not null) AppInstance.FindOrRegisterForKey(_key);
    }

    // Raised on a background thread; the window is only touched on the UI thread.
    private static void OwnerActivated(object? sender, AppActivationArguments args) => App.DispatcherQueue.TryEnqueue(() =>
    {
        if (_showWindow is { } show) show();
        else _showPending = true;
    });

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool AllowSetForegroundWindow(uint processId);
    }
}
