using System.Reflection;
using System.Text.Json;
using ChatAgentBridge_Desktop.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace NotificationNativeProbe;

public sealed class ProbeWindow : Window
{
    private readonly PendingRequestNotificationService _service;
    private readonly string _eventPath;
    private readonly TextBlock _request = new() { Text = "No synthetic request", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _counts = new();
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false, Title = "Production notification service" };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning, Title = "Notification action" };
    private readonly TextBox _events = new() { Header = "Recorded events", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180 };
    private string _scenario = "none";
    private string _token = "";
    private bool _busy;
    private DispatcherTimer? _startupTimer;
    private int _approve, _decline, _completed, _attention, _generation;

    public ProbeWindow(PendingRequestNotificationService service, string dataDirectory)
    {
        _service = service;
        _eventPath = Path.Combine(dataDirectory, $"events-{Environment.ProcessId}.jsonl");
        Title = "Notification Native Probe";
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Windows notification activation test", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "Synthetic callbacks only. Click Approve or Deny on the Windows toast. No CLI or chat is connected.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_status);
        panel.Children.Add(_notice);
        panel.Children.Add(_request);
        panel.Children.Add(_counts);
        panel.Children.Add(Button("Create approve scenario", "CreateApprove", () => Create("approve")));
        panel.Children.Add(Button("Create reject scenario", "CreateReject", () => Create("reject")));
        panel.Children.Add(Button("Create busy scenario", "CreateBusy", () => Create("busy")));
        panel.Children.Add(Button("Release busy state", "ReleaseBusy", () => { _busy = false; Record("busy_released"); _request.Text = "Busy released. Click the re-shown Windows toast; no callback runs automatically."; }));
        panel.Children.Add(Button("Create expired scenario", "CreateExpired", () => Create("expired")));
        var direct = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        direct.Children.Add(Button("Direct approve (bypasses Windows)", "DirectApprove", () => _ = DirectActivateAsync("approve")));
        direct.Children.Add(Button("Direct deny (bypasses Windows)", "DirectDeny", () => _ = DirectActivateAsync("decline")));
        panel.Children.Add(direct);
        panel.Children.Add(_events);
        panel.Children.Add(new TextBlock { Text = $"Events: {_eventPath}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Content = new ScrollViewer { Content = panel };
        AutomationProperties.SetAutomationId(_request, "RequestStatus");
        AutomationProperties.SetAutomationId(_counts, "CallbackCounts");
        AutomationProperties.SetAutomationId(_status, "NotificationStatus");
        AutomationProperties.SetAutomationId(_notice, "NotificationActionNotice");
        AutomationProperties.SetAutomationId(_events, "Events");
        service.StatusChanged += () => { RefreshStatus(); Record("service_status", service.Status); };
        service.ActionCompleted += () => { _completed++; _notice.IsOpen = false; _request.Text = $"{_scenario}: completed"; RefreshCounts(); Record("action_completed"); };
        service.SetOpenAppCallback(() => { _attention++; _notice.Message = service.Status; _notice.IsOpen = true; Activate(); RefreshCounts(); Record("attention_requested"); });
        Closed += (_, _) => { _startupTimer?.Stop(); service.Shutdown(); Record("shutdown"); };
        RefreshStatus();
        RefreshCounts();
        Record("initialized", service.Status);
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(740 * scale), (int)(850 * scale)));
    }

    public void ScheduleStartupTest(string? scenario, bool simpleToast)
    {
        if (scenario is null && !simpleToast) return;
        _startupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _startupTimer.Tick += async (_, _) =>
        {
            _startupTimer.Stop();
            RecordShellNotificationState();
            if (scenario is not null) Create(scenario);
            if (simpleToast)
            {
                try
                {
                    var notification = new AppNotificationBuilder()
                        .AddText("Notification Native Probe")
                        .AddText("Minimal synthetic notification diagnostic.")
                        .BuildNotification();
                    AppNotificationManager.Default.Show(notification);
                    Record("simple_toast_shown", diagnostics: new { id = notification.Id });
                    await LogNotificationHistoryAsync("simple_toast");
                }
                catch (Exception error) { Record("simple_toast_error", error.ToString()); }
            }
        };
        _startupTimer.Start();
        Record("startup_test_scheduled", diagnostics: new { scenario, simpleToast, delaySeconds = 3 });
    }

    private static Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action();
        return button;
    }

    private void Create(string scenario)
    {
        try
        {
            _service.ClearAll();
            _scenario = scenario;
            _token = "";
            _busy = scenario == "busy";
            _notice.IsOpen = false;
            _approve = _decline = _completed = _attention = 0;
            int generation = ++_generation;
            RefreshCounts();
            bool shown;
            if (scenario == "expired") shown = ShowExpiryProbe();
            else shown = _service.TryNotifyPendingRequest(PendingNotificationKind.AiApproval,
                () => Decision("approve"), () => Decision("decline"), Open, out _token);
            _request.Text = shown ? $"{scenario}: pending. Use the Windows toast buttons." : $"{scenario}: notification unavailable.";
            Record("scenario_created", shown ? "shown" : "failed");
            _ = LogNotificationHistoryAsync(scenario);
            if (scenario == "expired" && shown) _ = MarkExpiredAsync(generation);
        }
        catch (Exception error) { _request.Text = $"Probe error: {error.GetType().Name}: {error.Message}"; Record("probe_error", _request.Text); }
    }

    private Task<PendingNotificationActionResult> Decision(string action)
    {
        if (action == "approve") _approve++; else _decline++;
        RefreshCounts();
        Record("callback_invoked", action);
        if (_busy)
        {
            _request.Text = "busy: callback returned Busy. Release busy state, then click the re-shown toast.";
            Record("callback_busy", action);
            return Task.FromResult(PendingNotificationActionResult.Busy);
        }
        _request.Text = $"{_scenario}: {action} accepted";
        Record("callback_completed", action);
        return Task.FromResult(PendingNotificationActionResult.Completed);
    }

    private Task Open() { Activate(); Record("open_callback"); return Task.CompletedTask; }

    private bool ShowExpiryProbe()
    {
        if (!_service.IsAvailable) return false;
        // Test-only: keep the real OS toast visible after the production registry entry expires.
        // The production public API correctly uses one five-minute expiry for both.
        var registry = (PendingNotificationRegistry)typeof(PendingRequestNotificationService)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        var entry = registry.Add(PendingNotificationKind.AiApproval, TimeSpan.FromSeconds(2),
            () => Decision("approve"), () => Decision("decline"), Open);
        _token = entry.Token;
        var notification = (AppNotification)typeof(PendingRequestNotificationService)
            .GetMethod("BuildNotification", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [entry.Kind, entry.Token, DateTimeOffset.UtcNow.AddMinutes(5)])!;
        AppNotificationManager.Default.Show(notification);
        return notification.Id != 0;
    }

    private async Task DirectActivateAsync(string action)
    {
        Record("direct_activation", action);
        if (string.IsNullOrEmpty(_token)) { Record("direct_activation_skipped", "No scenario token"); return; }
        try
        {
            // Harness-only service route check. This cannot establish OS delivery or native activation.
            await (Task)typeof(PendingRequestNotificationService)
                .GetMethod("HandleInvocationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_service, [_token, action])!;
        }
        catch (Exception error) { Record("probe_error", error.ToString()); }
    }

    private async Task MarkExpiredAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (generation != _generation) return;
        _request.Text = "expired: ready. Click either Windows toast button; callbacks must remain at zero.";
        Record("expiry_ready");
    }

    private async Task LogNotificationHistoryAsync(string source)
    {
        await Task.Delay(500);
        try
        {
            var notifications = await AppNotificationManager.Default.GetAllAsync();
            Record("notification_history", source, new
            {
                count = notifications.Count,
                notifications = notifications.Select(notification => new
                {
                    id = notification.Id,
                    tagLength = notification.Tag?.Length ?? 0,
                    group = notification.Group
                }).ToArray()
            });
        }
        catch (Exception error) { Record("notification_history_error", $"{source}: {error}"); }
    }

    private void RecordShellNotificationState()
    {
        try
        {
            int result = SHQueryUserNotificationState(out int state);
            Record("shell_notification_state", diagnostics: new { hresult = result, state });
        }
        catch (Exception error) { Record("shell_notification_state_error", error.GetType().Name); }
    }

    private void RefreshStatus() { _status.Message = _service.Status; _status.Severity = _service.IsAvailable ? InfoBarSeverity.Informational : InfoBarSeverity.Warning; }
    private void RefreshCounts() => _counts.Text = $"Approve: {_approve} | Deny: {_decline} | Completed: {_completed} | Attention: {_attention}";
    private void Record(string name, string? detail = null, object? diagnostics = null)
    {
        var record = new { utc = DateTimeOffset.UtcNow, scenario = _scenario, @event = name, detail, diagnostics, approve = _approve, decline = _decline, completed = _completed, attention = _attention };
        File.AppendAllText(_eventPath, JsonSerializer.Serialize(record) + Environment.NewLine);
        _events.Text = $"{name}: {detail ?? _scenario}\r\n" + _events.Text;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int notificationState);
}
