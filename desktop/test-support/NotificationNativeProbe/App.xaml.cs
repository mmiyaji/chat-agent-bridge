using Microsoft.UI.Xaml;
using ChatAgentBridge_Desktop.Notifications;

namespace NotificationNativeProbe;

public partial class App : Application
{
    private ProbeWindow? _window;
    private string? _dataDirectory;

    public App()
    {
        UnhandledException += (_, error) =>
        {
            string directory = _dataDirectory ?? Path.Combine(Path.GetTempPath(), "NotificationNativeProbe");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "unhandled-errors.txt"), error.Exception.ToString() + Environment.NewLine);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Never read the production application's environment variables or settings.
        string[] arguments = Environment.GetCommandLineArgs();
        string? autoScenario = null;
        string? relaySmoke = null;
        string? explicitAppId = null;
        bool simpleToast = arguments.Contains("--simple-toast", StringComparer.Ordinal);
        bool legacyToast = arguments.Contains("--legacy-toast", StringComparer.Ordinal);
        bool explicitAssets = arguments.Contains("--explicit-assets", StringComparer.Ordinal);
        string dataDirectory = Path.Combine(Path.GetTempPath(), "NotificationNativeProbe", Guid.NewGuid().ToString("N"));
        string language;
        try { language = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.InstalledUICulture.Name; }
        catch { language = System.Globalization.CultureInfo.InstalledUICulture.Name; }
        for (int i = 1; i + 1 < arguments.Length; i++)
        {
            if (arguments[i] == "--data-dir" && Path.IsPathFullyQualified(arguments[i + 1])) dataDirectory = arguments[++i];
            else if (arguments[i] == "--language" && arguments[i + 1] is "ja" or "en") language = arguments[++i] == "ja" ? "ja-JP" : "en-US";
            else if (arguments[i] == "--auto-scenario" && arguments[i + 1] is "approve" or "reject" or "busy" or "expired") autoScenario = arguments[++i];
            else if (arguments[i] == "--app-id") explicitAppId = arguments[++i];
            else if (arguments[i] == "--relay-smoke") relaySmoke = arguments[++i];
        }
        if (arguments.Contains("--relay-smoke", StringComparer.Ordinal))
        {
            if (relaySmoke is null || !Path.IsPathFullyQualified(relaySmoke) || !File.Exists(relaySmoke) ||
                !string.Equals(Path.GetExtension(relaySmoke), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--relay-smoke requires an absolute path to NotificationRelayProbe.dll.");
            if (!arguments.Contains("--data-dir", StringComparer.Ordinal) || simpleToast || legacyToast || explicitAssets || autoScenario is not null || explicitAppId is not null)
                throw new ArgumentException("--relay-smoke requires its own --data-dir and ordinary service initialization without other probe modes.");
        }
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        Directory.CreateDirectory(dataDirectory);
        _dataDirectory = dataDirectory;
        if (legacyToast && explicitAppId is null) throw new ArgumentException("--legacy-toast requires an explicit --app-id diagnostic identity.");
        if (explicitAssets && (!simpleToast || autoScenario is not null || legacyToast))
            throw new ArgumentException("--explicit-assets requires --simple-toast and cannot be combined with --auto-scenario or --legacy-toast.");
        if (explicitAppId is not null)
        {
            if (explicitAppId.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(explicitAppId,
                @"\AChatAgentBridge\.Diagnostics\.NotificationProbe\.[A-Za-z0-9][A-Za-z0-9.-]*\z"))
                throw new ArgumentException("--app-id must use the ChatAgentBridge.Diagnostics.NotificationProbe. diagnostic namespace (maximum 128 characters, ASCII letters/digits/dot/hyphen only).");
            // Diagnostic identity must precede every AppNotificationManager.Default access.
            int setResult = SetCurrentProcessExplicitAppUserModelID(explicitAppId);
            int getResult = GetCurrentProcessExplicitAppUserModelID(out nint identityPointer);
            string? readback;
            try { readback = identityPointer == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringUni(identityPointer); }
            finally { if (identityPointer != 0) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(identityPointer); }
            File.AppendAllText(Path.Combine(dataDirectory, $"identity-events-{Environment.ProcessId}.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, @event = "explicit_app_identity", requested = explicitAppId, setHresult = setResult, getHresult = getResult, readback }) + Environment.NewLine);
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(setResult);
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(getResult);
            if (!string.Equals(explicitAppId, readback, StringComparison.Ordinal)) throw new InvalidOperationException("Explicit diagnostic identity readback mismatch.");
        }
        string rawEvents = Path.Combine(dataDirectory, $"native-events-{Environment.ProcessId}.jsonl");
        var rawEventLock = new object();
        Microsoft.Windows.AppNotifications.AppNotificationManager.Default.NotificationInvoked += (_, activation) =>
        {
            activation.Arguments.TryGetValue("action", out string? action);
            var record = new { utc = DateTimeOffset.UtcNow, @event = "native_notification_invoked", action,
                hasRequest = activation.Arguments.ContainsKey("request") };
            lock (rawEventLock)
                File.AppendAllText(rawEvents, System.Text.Json.JsonSerializer.Serialize(record) + Environment.NewLine);
        };
        var service = new PendingRequestNotificationService(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        // Keep the production registration/startup-activation ordering.
        if (explicitAssets)
        {
            string iconPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            if (!File.Exists(iconPath)) throw new FileNotFoundException("Bundled diagnostic icon missing.", iconPath);
            // Preserve the original Windows filesystem path for the SDK RawUri asset validation.
            var iconUri = new Uri(iconPath, UriKind.Absolute);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register("Chat Agent Bridge Notification Test", iconUri);
            File.AppendAllText(Path.Combine(dataDirectory, $"registration-events-{Environment.ProcessId}.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, @event = "explicit_assets_registered", displayName = "Chat Agent Bridge Notification Test", iconPath, uriOriginalString = iconUri.OriginalString, setting = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Setting.ToString() }) + Environment.NewLine);
        }
        else service.Initialize();
        _window = new ProbeWindow(service, dataDirectory, enableDecisions: !explicitAssets);
        if (explicitAssets)
        {
            _window.Closed += (_, _) => Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister();
            _window.ShowExplicitAssetsStatus();
        }
        _window.Activate();
        if (!explicitAssets) service.ProcessStartupActivation();
        if (relaySmoke is not null) _window.ScheduleRelaySmoke(relaySmoke);
        else _window.ScheduleStartupTest(autoScenario, simpleToast, legacyToast, explicitAppId);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
