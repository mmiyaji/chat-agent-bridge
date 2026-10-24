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
    public static Appearance.AppLayout Layout { get; private set; } = null!;
    public static PricingCatalogManager Pricing { get; private set; } = null!;
    public static Tray.TrayService Tray { get; private set; } = null!;
    public static PortableUpdateCoordinator PortableUpdates { get; private set; } = null!;
    public static PortableIdentityCoordinator PortableIdentity { get; private set; } = null!;
    public static bool IsNotificationOnlyLaunch { get; private set; }
    private readonly CancellationTokenSource _pricingLifetime = new();
    private readonly CancellationTokenSource _portableUpdateLifetime = new();
    private FileStream? _portableLease;

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
        var updateReadyEvent = Environment.GetEnvironmentVariable("CAB_PORTABLE_UPDATE_READY_EVENT");
        Environment.SetEnvironmentVariable("CAB_PORTABLE_UPDATE_READY_EVENT", null);
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
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        IsNotificationOnlyLaunch = NotificationLaunchArguments.IsNotificationLaunch(launchArguments);
        if (IsNotificationOnlyLaunch)
        {
            // A COM activation can arrive in a fresh process. Forward to the exact live
            // owner before loading settings, creating another window, or connecting chats.
            PendingRequestNotifications = new PendingRequestNotificationService(DispatcherQueue, new NotificationDiagnostics(dataDirectory));
            PendingRequestNotifications.Initialize();
            PendingRequestNotifications.ProcessStartupActivation();
            if (await PendingRequestNotifications.WaitForStartupForwardingAsync(TimeSpan.FromSeconds(5)))
            {
                PendingRequestNotifications.Shutdown(preserveRegistration: true);
                Exit();
                return;
            }
        }
        if (DeploymentRuntime.IsPortable)
        {
            try { _portableLease = ChatAgentBridge.Core.Updates.PortableApplicationLease.Open(AppContext.BaseDirectory); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                UpdateStartupNative.MessageBoxW(0, ChatAgentBridge_Desktop.Localization.DesktopText.Get("PortableUpdateInProgress"), "Chat Agent Bridge", 0x40);
                Exit();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Read-only portable locations can still run with an external data directory.
                // The updater remains unavailable until a writable location is used.
            }
        }

        try { SystemLanguageTag = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.InstalledUICulture.Name; }
        catch { SystemLanguageTag = System.Globalization.CultureInfo.InstalledUICulture.Name; }
        // The preference reads are independent; run them together so the
        // slowest file read, not their sum, delays the first window.
        var backgroundStore = new ChatAgentBridge.Core.BackgroundPreferencesStore(new ChatAgentBridge.Core.SettingsStore());
        Appearance = new(new ChatAgentBridge.Core.Appearance.AppearancePreferencesStore(new ChatAgentBridge.Core.SettingsStore()));
        var languageTask = LoadLanguagePreferenceAsync();
        var appearanceTask = Appearance.InitializeAsync();
        var backgroundTask = LoadBackgroundPreferencesAsync(backgroundStore);
        Layout = new(new ChatAgentBridge.Core.Appearance.LayoutPreferencesStore(new ChatAgentBridge.Core.SettingsStore()));
        var layoutTask = Layout.InitializeAsync();
        await Task.WhenAll(languageTask, appearanceTask, backgroundTask, layoutTask);
        ApplyLanguagePreference((await languageTask).Language);
        var background = await backgroundTask;

        // Pricing initializes after the window is visible (see RunPricingUpdatesAsync);
        // consumers read the bundled snapshot until its Changed event fires.
        Pricing = new(new ChatAgentBridge.Core.SettingsStore());
        PendingRequestNotifications ??= new PendingRequestNotificationService(DispatcherQueue, new NotificationDiagnostics(dataDirectory));
        PendingRequestNotifications.Initialize();
        PortableUpdates = new(dataDirectory, _portableLease is not null);
        PortableIdentity = new(dataDirectory, _portableLease is not null);
        // The lifetime update task initializes preferences and revalidates downloaded updates
        // after the window is visible; large archives must not delay first-window creation.

        var mainWindow = new MainWindow();
        Window = mainWindow;
        Tray = new(mainWindow, backgroundStore, background);
        Window.Closed += (_, _) =>
        {
            // Normal exits await this in TrayService before closing the window.
            // Keep a best-effort fallback for an OS session-ending close.
            _ = Layout.FlushAsync();
            Tray.Dispose();
            _portableUpdateLifetime.Cancel();
            PortableUpdates.Dispose();
            _portableUpdateLifetime.Dispose();
            _portableLease?.Dispose();
            PendingRequestNotifications.Shutdown();
            _pricingLifetime.Cancel();
        };
        Window.Activate();
        // Recovery uses only app-owned records and exclusive leases; it can run
        // alongside another window without touching active incoming attachments.
        _ = RecoverIncomingAttachmentsAsync(dataDirectory);
        if (!IsNotificationOnlyLaunch) PendingRequestNotifications.ProcessStartupActivation();
        if (DeploymentRuntime.IsPortable && updateReadyEvent is not null &&
            updateReadyEvent.StartsWith(@"Local\ChatAgentBridge.Update.", StringComparison.Ordinal) &&
            updateReadyEvent.Length == @"Local\ChatAgentBridge.Update.".Length + 32 &&
            updateReadyEvent[^32..].All(Uri.IsHexDigit))
        {
            try { using var ready = EventWaitHandle.OpenExisting(updateReadyEvent); ready.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        _ = RunPricingUpdatesAsync(_pricingLifetime.Token);
        _ = RunPortableUpdateChecksAsync();
    }

    private static async Task RecoverIncomingAttachmentsAsync(string dataDirectory)
    {
        try
        {
            await Task.Run(() => ChatAgentBridge.Core.IncomingAttachmentRecovery.RecoverAbandoned(dataDirectory));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            // Read-only/unavailable data locations must not prevent startup.
            // Keep ownership records for a later recovery attempt.
        }
    }

    private static async Task<LanguagePreferences> LoadLanguagePreferenceAsync()
    {
        try { return await new LanguagePreferencesStore(new ChatAgentBridge.Core.SettingsStore()).LoadAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable preference never prevents access to the language selector.
            // Preserve the file; a later explicit preference change can repair it.
            return new LanguagePreferences();
        }
    }

    private static async Task<ChatAgentBridge.Core.BackgroundPreferences> LoadBackgroundPreferencesAsync(ChatAgentBridge.Core.BackgroundPreferencesStore store)
    {
        try { return await store.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A damaged or inaccessible preference must leave the window reachable.
            return new ChatAgentBridge.Core.BackgroundPreferences();
        }
    }

    private async Task RunPortableUpdateChecksAsync()
    {
        try { await PortableUpdates.RunAutomaticChecksAsync(_portableUpdateLifetime.Token); }
        catch (OperationCanceledException) when (_portableUpdateLifetime.IsCancellationRequested) { }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Automatic portable update check: {error.GetType().Name}"); }
    }

    private async Task RunPricingUpdatesAsync(CancellationToken cancellationToken)
    {
        // The manager throttles checks to once per day and retains valid local data
        // on network errors. The window is already usable before any HTTP request.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            // Loads cached/manual prices off the startup path; Changed notifies open views.
            await Pricing.InitializeAsync(cancellationToken).ConfigureAwait(false);
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

internal static class UpdateStartupNative
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    internal static extern int MessageBoxW(nint owner, string text, string title, uint type);
}
