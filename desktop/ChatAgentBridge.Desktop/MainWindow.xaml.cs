using Microsoft.UI.Xaml;
using Microsoft.UI;
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
        AppWindow.Resize(new SizeInt32((int)(1160 * scale), (int)(800 * scale)));
        Closed += MainWindow_Closed;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.Appearance.Dispose();
        if (RootFrame.Content is MainPage page) page.ViewModel.CancelForWindowClose();
        ChatAgentBridge.Core.AppServer.CodexAppServerRunner.ShutdownSharedProcesses();
    }
}
