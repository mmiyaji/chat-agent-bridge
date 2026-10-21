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
        string dataDirectory = Path.Combine(Path.GetTempPath(), "NotificationNativeProbe", Guid.NewGuid().ToString("N"));
        string language;
        try { language = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.InstalledUICulture.Name; }
        catch { language = System.Globalization.CultureInfo.InstalledUICulture.Name; }
        for (int i = 1; i + 1 < arguments.Length; i++)
        {
            if (arguments[i] == "--data-dir" && Path.IsPathFullyQualified(arguments[i + 1])) dataDirectory = arguments[++i];
            else if (arguments[i] == "--language" && arguments[i + 1] is "ja" or "en") language = arguments[++i] == "ja" ? "ja-JP" : "en-US";
        }
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        Directory.CreateDirectory(dataDirectory);
        _dataDirectory = dataDirectory;
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
        service.Initialize();
        _window = new ProbeWindow(service, dataDirectory);
        _window.Activate();
        service.ProcessStartupActivation();
    }
}
