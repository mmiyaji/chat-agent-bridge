using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.AppLifecycle;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>
/// Shows generic notifications for pending chat decisions. Callback registrations live
/// only in this process and expire after five minutes.
/// </summary>
public sealed class PendingRequestNotificationService
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    private const string TokenArgument = "request";
    private const string ActionArgument = "action";
    private const string OwnerArgument = "owner";
    private const string NotificationGroup = "pending-decisions";

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppNotificationManager _manager;
    private readonly PendingNotificationRegistry _pending = new();
    private readonly NotificationActivationRelay _relay = new();
    private readonly NotificationDiagnostics? _diagnostics;
    private readonly TaskCompletionSource<bool> _startupForwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _relayStarted;
    private Action? _openApp;
    private bool _attentionPending;
    private bool _initialized;
    private bool _registered;
    private bool _available;
    private bool _shutdown;
    private string _status = DesktopText.Get("NotificationNotInitialized");

    public PendingRequestNotificationService(DispatcherQueue dispatcherQueue, NotificationDiagnostics? diagnostics = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _manager = AppNotificationManager.Default;
        _diagnostics = diagnostics;
    }

    // A notification-only COM launch waits here before creating a window or connecting chats.
    public async Task<bool> WaitForStartupForwardingAsync(TimeSpan timeout)
    {
        if (!_registered) return false;
        try { return await _startupForwarded.Task.WaitAsync(timeout); }
        catch (TimeoutException) { _diagnostics?.Write("activation.startup_timeout"); return false; }
    }

    /// <summary>A short, user-visible explanation of whether notifications are available.</summary>
    public string Status => _status;

    public bool IsAvailable => _available && _registered && !_shutdown;

    public event Action? StatusChanged;
    public event Action? ActionCompleted;

    public void TraceDecision(string reason) => _diagnostics?.Write("decision." + reason);

    public void SetOpenAppCallback(Action openApp)
    {
        if (_shutdown) return;
        _openApp = openApp;
        if (_attentionPending) RequestAttention();
    }

    // Register must precede GetActivatedEventArgs. Both warm events and startup
    // activation use the same opaque-token route; cold starts cannot approve old work.
    public void ProcessStartupActivation()
    {
        if (!_registered) return;
        try
        {
            if (AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs args })
                OnNotificationInvoked(_manager, args);
        }
        catch (Exception error)
        {
            _diagnostics?.Write("activation.startup_failed", error: error);
            _startupForwarded.TrySetResult(false);
            SetStatus(DesktopText.Get("NotificationActivationFailed"));
        }
    }

    /// <summary>
    /// Hooks activation before registering with Windows. Call once during app startup,
    /// before creating the main window.
    /// </summary>
    public bool Initialize()
    {
        if (_initialized)
            return IsAvailable;

        _initialized = true;
        try
        {
            _relay.Start((token, action) => !_shutdown && _dispatcherQueue.TryEnqueue(() => _ = HandleInvocationAsync(token, action, "relay")));
            _relayStarted = true;
            _diagnostics?.Write("relay.ready");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            // Same-process buttons remain usable if local IPC is unavailable.
            _diagnostics?.Write("relay.unavailable", error: error);
        }
        try
        {
            _diagnostics?.Write("notification.register_begin");
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            _diagnostics?.Write("notification.registered");

            string setting = _manager.Setting.ToString();
            if (!string.Equals(setting, "Enabled", StringComparison.Ordinal))
            {
                _available = false;
                SetStatus(DesktopText.Get("NotificationDisabled", setting));
                return false;
            }

            _available = true;
            SetStatus(DesktopText.Get("NotificationAvailable"));
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics?.Write("notification.register_failed", error: ex);
            _manager.NotificationInvoked -= OnNotificationInvoked;
            _registered = false;
            _available = false;
            SetStatus(DesktopText.Get("NotificationInitializeFailed", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Shows a notification with fixed text and associates its opaque random token with
    /// in-memory callbacks. Only the callback selected by the user can be invoked.
    /// </summary>
    public bool TryNotifyPendingRequest(
        PendingNotificationKind kind,
        Func<Task> approve,
        Func<Task> decline,
        Func<Task> open,
        out string token)
    {
        if (approve is null || decline is null || open is null) { token = ""; return false; }
        return TryNotifyPendingRequest(kind, async () => { await approve(); return PendingNotificationActionResult.Completed; },
            async () => { await decline(); return PendingNotificationActionResult.Completed; }, open, out token);
    }

    public bool TryNotifyPendingRequest(
        PendingNotificationKind kind,
        Func<Task<PendingNotificationActionResult>> approve,
        Func<Task<PendingNotificationActionResult>> decline,
        Func<Task> open,
        out string token)
    {
        token = string.Empty;
        if (!IsAvailable || approve is null || decline is null || open is null)
            return false;

        var entry = _pending.Add(kind, PendingLifetime, approve, decline, open);
        var candidate = entry.Token;

        try
        {
            AppNotification notification = BuildNotification(kind, candidate, entry.ExpiresAt);
            _manager.Show(notification);
            _diagnostics?.Write("notification.shown", candidate, outcome: notification.Id == 0 ? "rejected" : "accepted");
            if (notification.Id == 0)
            {
                _pending.Remove(candidate);
                RemoveToast(candidate);
                SetStatus(DesktopText.Get("NotificationShowFailed"));
                return false;
            }
            token = candidate;
            _ = ExpireAfterAsync(candidate, PendingLifetime);
            SetStatus(DesktopText.Get("NotificationPendingShown"));
            return true;
        }
        catch (Exception ex)
        {
            _pending.Remove(candidate);
            SetStatus(DesktopText.Get("NotificationShowFailedWithError", ex.Message));
            return false;
        }
    }

    /// <summary>Removes a pending callback and its toast after the request is resolved.</summary>
    public bool Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_pending.Remove(token))
            return false;

        RemoveToast(token);
        return true;
    }

    /// <summary>Invalidates all in-memory callbacks and removes their displayed toasts.</summary>
    public void ClearAll()
    {
        foreach (var entry in _pending.Clear()) RemoveToast(entry.Token);
    }

    /// <summary>Clears pending work and unregisters notification activation on window close.</summary>
    public void Shutdown(bool preserveRegistration = false)
    {
        if (_shutdown)
            return;

        _shutdown = true;
        _available = false;
        _relay.Dispose();
        // A forwarding-only process must not remove the running owner's notifications/registration.
        if (!preserveRegistration) ClearAll();
        if (_registered)
        {
            try
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
                if (!preserveRegistration) _manager.Unregister();
            }
            catch (Exception ex)
            {
                SetStatus(DesktopText.Get("NotificationShutdownFailed", ex.Message));
            }
            finally
            {
                _registered = false;
            }
        }
        SetStatus(DesktopText.Get("NotificationStopped"));
        _diagnostics?.Write("notification.stopped", outcome: preserveRegistration ? "relay_only" : "normal");
        _diagnostics?.Dispose();
    }

    private AppNotification BuildNotification(PendingNotificationKind kind, string token, DateTimeOffset expiresAt)
    {
        (string title, string body) = kind switch
        {
            PendingNotificationKind.ChatRequest => (DesktopText.Get("NotificationChatRequestTitle"), DesktopText.Get("NotificationChatRequestBody")),
            PendingNotificationKind.AiApproval => (DesktopText.Get("NotificationAiApprovalTitle"), DesktopText.Get("NotificationAiApprovalBody")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var builder = new AppNotificationBuilder()
            .AddArgument(TokenArgument, token)
            .AddArgument(ActionArgument, "open")
            .AddText(title)
            .AddText(body)
            .AddButton(CreateButton(DesktopText.Get("ApproveAndRun"), token, "approve"))
            .AddButton(CreateButton(DesktopText.Get("Deny"), token, "decline"))
            .SetDuration(AppNotificationDuration.Long);
        if (_relayStarted) builder.AddArgument(OwnerArgument, _relay.OwnerId);
        AppNotification notification = builder.BuildNotification();

        notification.Expiration = expiresAt;
        notification.ExpiresOnReboot = false;
        notification.Tag = token;
        notification.Group = NotificationGroup;
        return notification;
    }

    private AppNotificationButton CreateButton(string text, string token, string action)
    {
        var button = new AppNotificationButton(text)
            .AddArgument(TokenArgument, token)
            .AddArgument(ActionArgument, action);
        if (_relayStarted) button.AddArgument(OwnerArgument, _relay.OwnerId);
        return button;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (_shutdown) return;
        if (!args.Arguments.TryGetValue(TokenArgument, out string? token) ||
            !args.Arguments.TryGetValue(ActionArgument, out string? action) ||
            string.IsNullOrWhiteSpace(token))
        {
            _diagnostics?.Write("activation.invalid_arguments");
            _startupForwarded.TrySetResult(false);
            return;
        }
        if (token.Length != 32 || !token.All(Uri.IsHexDigit) || action is not ("approve" or "decline" or "open"))
        {
            _diagnostics?.Write("activation.invalid_arguments");
            _startupForwarded.TrySetResult(false);
            return;
        }

        args.Arguments.TryGetValue(OwnerArgument, out string? owner);
        _diagnostics?.Write("activation.received", token, action, owner is null ? "legacy" : owner == _relay.OwnerId ? "local" : "other_owner");
        if (!string.IsNullOrWhiteSpace(owner) && owner != _relay.OwnerId)
        {
            _ = ForwardInvocationAsync(owner, token, action);
            return;
        }
        _startupForwarded.TrySetResult(false);
        if (!_dispatcherQueue.TryEnqueue(() => _ = HandleInvocationAsync(token, action, "native")))
        {
            _diagnostics?.Write("activation.dispatch_failed", token, action);
            SetStatus(DesktopText.Get("NotificationDispatchFailed"));
        }
    }

    private async Task ForwardInvocationAsync(string owner, string token, string action)
    {
        bool forwarded = await NotificationActivationRelay.TryForwardAsync(owner, token, action);
        _diagnostics?.Write("activation.forwarded", token, action, forwarded ? "accepted" : "unavailable");
        _startupForwarded.TrySetResult(forwarded);
        if (!forwarded && !_shutdown)
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Never reinterpret a failed relay as permission to approve in this process.
                SetStatus(DesktopText.Get("NotificationExpiredOpenApp"));
                RequestAttention();
            });
    }

    private async Task HandleInvocationAsync(string token, string action, string source)
    {
        if (_shutdown) return;
        try
        {
            _diagnostics?.Write("activation.dispatch", token, action, source);
            var (outcome, entry) = await _pending.InvokeAsync(token, action);
            _diagnostics?.Write("activation.result", token, action, outcome.ToString());
            if (_shutdown) return;
            switch (outcome)
            {
                case PendingNotificationRegistry.Outcome.Completed:
                    RemoveToast(token);
                    SetStatus(DesktopText.Get("NotificationAvailable"));
                    ActionCompleted?.Invoke();
                    break;
                case PendingNotificationRegistry.Outcome.Busy:
                    SetStatus(DesktopText.Get("NotificationActionBusy"));
                    // Windows dismisses a clicked toast. Re-show the same decision
                    // with its original lifetime; never queue automatic execution.
                    if (entry is not null && entry.ExpiresAt > DateTimeOffset.UtcNow)
                        _manager.Show(BuildNotification(entry.Kind, token, entry.ExpiresAt));
                    RequestAttention();
                    break;
                case PendingNotificationRegistry.Outcome.Missing:
                case PendingNotificationRegistry.Outcome.Expired:
                case PendingNotificationRegistry.Outcome.Unavailable:
                    RemoveToast(token);
                    SetStatus(DesktopText.Get("NotificationExpiredOpenApp"));
                    RequestAttention();
                    break;
            }
        }
        catch (Exception error)
        {
            _diagnostics?.Write("activation.failed", token, action, error: error);
            SetStatus(DesktopText.Get("NotificationActionRetryInApp"));
            RequestAttention();
        }
    }

    private void RequestAttention()
    {
        if (_shutdown) return;
        _attentionPending = _openApp is null;
        _openApp?.Invoke();
    }

    private async Task ExpireAfterAsync(string token, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (_pending.Expire(token))
        {
            RemoveToast(token);
            SetStatus(DesktopText.Get("NotificationExpired"));
        }
    }

    private void RemoveToast(string? notificationTag)
    {
        if (string.IsNullOrWhiteSpace(notificationTag))
            return;

        void Remove()
        {
            try
            {
                _ = _manager.RemoveByTagAndGroupAsync(notificationTag, NotificationGroup);
            }
            catch (Exception ex)
            {
                SetStatus(DesktopText.Get("NotificationCloseFailed", ex.Message));
            }
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(Remove))
                SetStatus(DesktopText.Get("NotificationCloseDispatchFailed"));
            return;
        }

        Remove();
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_status, status, StringComparison.Ordinal))
            return;

        _status = status;
        void Raise() => StatusChanged?.Invoke();
        if (_dispatcherQueue.HasThreadAccess)
            Raise();
        else
            _dispatcherQueue.TryEnqueue(Raise);
    }

}
