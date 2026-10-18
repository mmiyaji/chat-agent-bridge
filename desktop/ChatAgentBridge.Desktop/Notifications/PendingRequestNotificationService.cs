using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Collections.Concurrent;

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
    private const string NotificationGroup = "pending-decisions";

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppNotificationManager _manager;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _registered;
    private bool _available;
    private bool _shutdown;
    private string _status = "Windows 通知はまだ初期化されていません。";

    public PendingRequestNotificationService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _manager = AppNotificationManager.Default;
    }

    /// <summary>A short, user-visible explanation of whether notifications are available.</summary>
    public string Status => _status;

    public bool IsAvailable => _available && _registered && !_shutdown;

    public event Action? StatusChanged;

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
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;

            string setting = _manager.Setting.ToString();
            if (!string.Equals(setting, "Enabled", StringComparison.Ordinal))
            {
                _available = false;
                SetStatus($"Windows 通知は無効です（{setting}）。Windows の通知設定を確認してください。");
                return false;
            }

            _available = true;
            SetStatus("Windows 通知を利用できます。");
            return true;
        }
        catch (Exception ex)
        {
            _manager.NotificationInvoked -= OnNotificationInvoked;
            _registered = false;
            _available = false;
            SetStatus($"Windows 通知を初期化できませんでした: {ex.Message}");
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
        token = string.Empty;
        if (!IsAvailable || approve is null || decline is null || open is null)
            return false;

        string candidate = Guid.NewGuid().ToString("N");
        var entry = new PendingEntry(candidate, DateTimeOffset.UtcNow + PendingLifetime, approve, decline, open);

        try
        {
            AppNotification notification = BuildNotification(kind, candidate);
            _pending[candidate] = entry;
            _manager.Show(notification);
            if (notification.Id == 0)
            {
                _pending.TryRemove(candidate, out _);
                RemoveToast(candidate);
                SetStatus("Windows 通知を表示できませんでした。Windows の通知設定を確認してください。");
                return false;
            }
            entry.NotificationTag = candidate;
            token = candidate;
            _ = ExpireAfterAsync(candidate, PendingLifetime);
            SetStatus("承認待ちの Windows 通知を表示しました。有効期限は 5 分です。");
            return true;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(candidate, out _);
            SetStatus($"Windows 通知を表示できませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes a pending callback and its toast after the request is resolved.</summary>
    public bool Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_pending.TryRemove(token, out PendingEntry? entry))
            return false;

        RemoveToast(entry.NotificationTag);
        return true;
    }

    /// <summary>Invalidates all in-memory callbacks and removes their displayed toasts.</summary>
    public void ClearAll()
    {
        foreach (KeyValuePair<string, PendingEntry> pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out PendingEntry? entry))
                RemoveToast(entry.NotificationTag);
        }
    }

    /// <summary>Clears pending work and unregisters notification activation on window close.</summary>
    public void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        _available = false;
        ClearAll();
        if (_registered)
        {
            try
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
                _manager.Unregister();
            }
            catch (Exception ex)
            {
                SetStatus($"Windows 通知の終了処理に失敗しました: {ex.Message}");
            }
            finally
            {
                _registered = false;
            }
        }
        SetStatus("Windows 通知を停止しました。");
    }

    private static AppNotification BuildNotification(PendingNotificationKind kind, string token)
    {
        (string title, string body) = kind switch
        {
            PendingNotificationKind.ChatRequest => ("チャット依頼", "応答待ちのチャット依頼があります。"),
            PendingNotificationKind.AiApproval => ("AI 操作の承認", "承認すると保存済みの PC 設定で実行します。"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        AppNotification notification = new AppNotificationBuilder()
            .AddArgument(TokenArgument, token)
            .AddArgument(ActionArgument, "open")
            .AddText(title)
            .AddText(body)
            .AddButton(CreateButton("承認して実行", token, "approve"))
            .AddButton(CreateButton("拒否", token, "decline"))
            .SetDuration(AppNotificationDuration.Long)
            .BuildNotification();

        notification.Expiration = DateTimeOffset.UtcNow.Add(PendingLifetime);
        notification.ExpiresOnReboot = false;
        notification.Tag = token;
        notification.Group = NotificationGroup;
        return notification;
    }

    private static AppNotificationButton CreateButton(string text, string token, string action) =>
        new AppNotificationButton(text)
            .AddArgument(TokenArgument, token)
            .AddArgument(ActionArgument, action);

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue(TokenArgument, out string? token) ||
            !args.Arguments.TryGetValue(ActionArgument, out string? action) ||
            string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => _ = HandleInvocationAsync(token, action)))
            SetStatus("Windows 通知の操作を UI スレッドへ渡せませんでした。");
    }

    private async Task HandleInvocationAsync(string token, string action)
    {
        if (!_pending.TryGetValue(token, out PendingEntry? entry))
            return;

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Resolve(token);
            SetStatus("通知の有効期限が切れています。アプリを開いて最新の状態を確認してください。");
            return;
        }

        Func<Task>? callback = action switch
        {
            "approve" => entry.Approve,
            "decline" => entry.Decline,
            "open" => entry.Open,
            _ => null
        };
        if (callback is null || !_pending.TryRemove(token, out PendingEntry? consumed))
            return;

        RemoveToast(consumed.NotificationTag);
        try
        {
            await callback();
        }
        catch (Exception ex)
        {
            SetStatus($"Windows 通知の操作を完了できませんでした: {ex.Message}");
        }
    }

    private async Task ExpireAfterAsync(string token, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (_pending.TryRemove(token, out PendingEntry? entry))
        {
            RemoveToast(entry.NotificationTag);
            SetStatus("Windows 通知の有効期限が切れました。");
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
                SetStatus($"Windows 通知を閉じられませんでした: {ex.Message}");
            }
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(Remove))
                SetStatus("Windows 通知を閉じる処理を UI スレッドへ渡せませんでした。");
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

    private sealed class PendingEntry(
        string token,
        DateTimeOffset expiresAt,
        Func<Task> approve,
        Func<Task> decline,
        Func<Task> open)
    {
        public string Token { get; } = token;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public Func<Task> Approve { get; } = approve;
        public Func<Task> Decline { get; } = decline;
        public Func<Task> Open { get; } = open;
        public string? NotificationTag { get; set; }
    }
}
