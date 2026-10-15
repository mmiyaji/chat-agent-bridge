using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ChatAgentBridge_Desktop;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // AUMID activation does not inherit the invoking terminal's environment.
        // An explicit data directory keeps packaged UI tests isolated.
        IReadOnlyList<string> launchArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launchArguments.Count == 0)
            launchArguments = ChatAgentBridge.Core.AgentArgumentParser.Parse(args.Arguments);
        if (launchArguments.Count == 2 && launchArguments[0] == "--data-dir" && System.IO.Path.IsPathFullyQualified(launchArguments[1]))
            Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", launchArguments[1]);
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR")))
            Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", Windows.Storage.ApplicationData.Current.LocalFolder.Path);
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
