using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ChatAgentBridge_Desktop.Notifications;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge_Desktop.Deployment;
using ChatAgentBridge.Core.Pricing;

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

    /// <summary>In-memory notification callbacks for pending chat decisions.</summary>
    public static PendingRequestNotificationService PendingRequestNotifications { get; private set; } = null!;
    public static string SystemLanguageTag { get; private set; } = "en-US";
    public static Appearance.AppAppearance Appearance { get; private set; } = null!;
    public static PricingCatalogManager Pricing { get; private set; } = null!;
    private readonly CancellationTokenSource _pricingLifetime = new();

    /// <summary>Applies a persisted language preference to this process and future MRT lookups.</summary>
    public static System.Globalization.CultureInfo ApplyLanguagePreference(string preference)
    {
        var culture = AppLanguage.Apply(preference, SystemLanguageTag);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        return culture;
    }

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
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // AUMID activation does not inherit the invoking terminal's environment.
        // An explicit data directory keeps packaged UI tests isolated.
        IReadOnlyList<string> launchArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launchArguments.Count == 0)
            launchArguments = ChatAgentBridge.Core.AgentArgumentParser.Parse(args.Arguments);
        string? explicitDataDirectory = launchArguments.Count == 2 && launchArguments[0] == "--data-dir" &&
            System.IO.Path.IsPathFullyQualified(launchArguments[1]) ? launchArguments[1] : null;
        string? packageLocalDirectory = DeploymentRuntime.IsPortable
            ? null
            : Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        var dataDirectory = DeploymentRuntime.ResolveDataDirectory(
            DeploymentRuntime.IsPortable,
            explicitDataDirectory,
            Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR"),
            AppContext.BaseDirectory,
            packageLocalDirectory);
        Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", dataDirectory);

        try { SystemLanguageTag = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.InstalledUICulture.Name; }
        catch { SystemLanguageTag = System.Globalization.CultureInfo.InstalledUICulture.Name; }
        var languagePreference = new LanguagePreferences();
        try { languagePreference = await new LanguagePreferencesStore(new ChatAgentBridge.Core.SettingsStore()).LoadAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable preference never prevents access to the language selector.
            // Preserve the file; a later explicit preference change can repair it.
        }
        ApplyLanguagePreference(languagePreference.Language);

        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Appearance = new(new ChatAgentBridge.Core.Appearance.AppearancePreferencesStore(new ChatAgentBridge.Core.SettingsStore()));
        await Appearance.InitializeAsync();
        Pricing = new(new ChatAgentBridge.Core.SettingsStore());
        await Pricing.InitializeAsync();
        PendingRequestNotifications = new PendingRequestNotificationService(DispatcherQueue);
        PendingRequestNotifications.Initialize();

        Window = new MainWindow();
        Window.Closed += (_, _) =>
        {
            PendingRequestNotifications.Shutdown();
            _pricingLifetime.Cancel();
        };
        Window.Activate();
        _ = RunPricingUpdatesAsync(_pricingLifetime.Token);
    }

    private async Task RunPricingUpdatesAsync(CancellationToken cancellationToken)
    {
        // The manager throttles checks to once per day and retains valid local data
        // on network errors. The window is already usable before any HTTP request.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                await Pricing.RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Pricing.Dispose();
            _pricingLifetime.Dispose();
        }
    }
}
