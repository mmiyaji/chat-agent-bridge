using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ChatAgentBridge_Desktop;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinimumWidthDip = 500;
    private const int MinimumHeightDip = 600;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        App.Appearance.Attach(this, WindowRoot);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        UpdateMinimumWindowSize();
        AppWindow.Resize(new SizeInt32((int)(1160 * scale), (int)(800 * scale)));
        WindowRoot.Loaded += WindowRoot_Loaded;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)Math.Ceiling(MinimumWidthDip * scale);
        var height = (int)Math.Ceiling(MinimumHeightDip * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs args)
    {
        WindowRoot.Loaded -= WindowRoot_Loaded;
        WindowRoot.XamlRoot.Changed += WindowRootXamlRoot_Changed;
        UpdateMinimumWindowSize();
    }

    private void WindowRootXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateMinimumWindowSize();

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            PasswordRevealControl.Hide(WindowRoot);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        WindowRoot.Loaded -= WindowRoot_Loaded;
        if (WindowRoot.XamlRoot is { } root) root.Changed -= WindowRootXamlRoot_Changed;
        App.Appearance.Dispose();
        if (RootFrame.Content is MainPage page) page.ViewModel.CancelForWindowClose();
        ChatAgentBridge.Core.AppServer.CodexAppServerRunner.ShutdownSharedProcesses();
    }

    internal Task ShutdownAsync() => RootFrame.Content is MainPage page
        ? page.ShutdownAsync() : Task.CompletedTask;

    internal Task<bool> TryResolveUnsavedSettingsAsync() => RootFrame.Content is MainPage page
        ? page.TryResolveUnsavedSettingsAsync() : Task.FromResult(true);

    internal bool CanRestartForUpdate => RootFrame.Content is MainPage page && page.CanRestartForPortableUpdate;
    internal Task ReloadAfterDataMaintenanceAsync() => RootFrame.Content is MainPage page
        ? page.ReloadAfterDataMaintenanceAsync() : Task.CompletedTask;
    internal Task<bool> PrepareForDataMaintenanceAsync() => RootFrame.Content is MainPage page
        ? page.PrepareForDataMaintenanceAsync() : Task.FromResult(false);
    internal void FinishDataMaintenance()
    {
        if (RootFrame.Content is MainPage page) page.FinishDataMaintenance();
    }
    internal Task<bool> PrepareForPortableUpdateAsync() => RootFrame.Content is MainPage page
        ? page.PrepareForPortableUpdateAsync() : Task.FromResult(false);
    internal void CancelPortableUpdatePreparation()
    {
        if (RootFrame.Content is MainPage page) page.CancelPortableUpdatePreparation();
    }
}
