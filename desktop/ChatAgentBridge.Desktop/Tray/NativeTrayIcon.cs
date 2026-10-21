using System.Runtime.InteropServices;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Tray;

/// <summary>Owns the native notification area icon associated with a window.</summary>
public sealed class NativeTrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmUser = 0x0400;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetFocus = 0x00000003;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint NinSelect = WmUser;
    private const uint NinKeySelect = WmUser + 1;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;
    private const uint MfString = 0x00000000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;

    private const uint OpenMenuCommand = 1;
    private const uint ExitMenuCommand = 2;
    private const uint IconId = 1;
    private const string TrayTooltip = "Chat Agent Bridge";

    private static long _nextSubclassId;

    private readonly nint _hwnd;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly uint _callbackMessage = WmApp + 0x2B1;
    private readonly uint _taskbarCreatedMessage;
    private readonly nuint _subclassId;
    private readonly SubclassProc _subclassProc;
    private nint _icon;
    private bool _subclassInstalled;
    private bool _disposed;

    public NativeTrayIcon(nint hwnd, Action open, Action exit)
    {
        _hwnd = hwnd;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _subclassId = unchecked((nuint)Interlocked.Increment(ref _nextSubclassId));
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _subclassProc = WindowSubclassProc;

        if (_hwnd == 0 || _taskbarCreatedMessage == 0)
        {
            NotifyAvailabilityChanged();
            return;
        }

        try
        {
            _subclassInstalled = SetWindowSubclass(_hwnd, _subclassProc, _subclassId, 0);
            if (!_subclassInstalled)
            {
                NotifyAvailabilityChanged();
                return;
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            _icon = LoadImageW(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            if (_icon == 0)
            {
                NotifyAvailabilityChanged();
                return;
            }

            RefreshRegistration();
        }
        catch
        {
            NotifyAvailabilityChanged();
        }
    }

    /// <summary>True while the icon is registered with the notification area.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True after Windows has begun ending the session.</summary>
    public bool SessionEnding { get; private set; }

    /// <summary>Raised when registration changes or fails, so the owner can restore its window.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>Attempts to add the icon again after a shell restart or registration failure.</summary>
    public void RefreshRegistration()
    {
        if (_disposed || !_subclassInstalled || _icon == 0)
        {
            IsAvailable = false;
            NotifyAvailabilityChanged();
            return;
        }

        if (IsAvailable)
            return;

        try
        {
            var data = CreateNotifyIconData();
            var added = Shell_NotifyIconW(NimAdd, ref data);
            if (added)
            {
                data.TimeoutOrVersion = NotifyIconVersion4;
                added = Shell_NotifyIconW(NimSetVersion, ref data);
                if (!added)
                    Shell_NotifyIconW(NimDelete, ref data);
            }

            IsAvailable = added;
            NotifyAvailabilityChanged();
        }
        catch
        {
            IsAvailable = false;
            NotifyAvailabilityChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (IsAvailable)
        {
            try
            {
                var data = CreateNotifyIconData();
                Shell_NotifyIconW(NimDelete, ref data);
            }
            catch { }
        }

        IsAvailable = false;
        if (_subclassInstalled)
        {
            try { RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId); }
            catch { }
            _subclassInstalled = false;
        }

        if (_icon != 0)
        {
            try { DestroyIcon(_icon); }
            catch { }
            _icon = 0;
        }
    }

    private NativeNotifyIconData CreateNotifyIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeNotifyIconData>(),
        Window = _hwnd,
        Id = IconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = _callbackMessage,
        Icon = _icon,
        Tip = TrayTooltip
    };

    private nint WindowSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        try
        {
            if (message == WmQueryEndSession)
                SessionEnding = true;
            else if (message == WmEndSession)
                SessionEnding = wParam != 0;
            else if (message == _taskbarCreatedMessage)
            {
                IsAvailable = false;
                RefreshRegistration();
            }
            else if (message == _callbackMessage)
            {
                HandleTrayCallback(wParam, lParam);
            }
        }
        catch
        {
            // Never allow a managed exception to escape the native window procedure.
            IsAvailable = false;
            NotifyAvailabilityChanged();
        }

        try { return DefSubclassProc(hwnd, message, wParam, lParam); }
        catch { return 0; }
    }

    private void HandleTrayCallback(nuint wParam, nint lParam)
    {
        // NOTIFYICON_VERSION_4 stores the event in LOWORD(lParam) and the
        // icon ID in HIWORD(lParam); wParam carries the screen anchor point.
        var notificationData = unchecked((uint)(nuint)lParam);
        var notification = notificationData & 0xFFFF;
        var callbackIconId = (notificationData >> 16) & 0xFFFF;
        if (callbackIconId != IconId)
            return;

        if (notification is NinSelect or NinKeySelect)
        {
            InvokeSafely(_open);
        }
        else if (notification == WmContextMenu)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        nint menu = 0;
        try
        {
            menu = CreatePopupMenu();
            if (menu == 0)
                return;

            var openText = DesktopText.Get("TrayMenuOpen");
            var exitText = DesktopText.Get("TrayMenuExit");
            if (!AppendMenuW(menu, MfString, OpenMenuCommand, openText) ||
                !AppendMenuW(menu, MfString, ExitMenuCommand, exitText))
                return;

            if (!GetCursorPos(out var point))
                return;

            SetForegroundWindow(_hwnd);
            var selected = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand, point.X, point.Y, 0, _hwnd, 0);
            if (selected == OpenMenuCommand)
                InvokeSafely(_open);
            else if (selected == ExitMenuCommand)
                InvokeSafely(_exit);
        }
        catch
        {
            // Menu failures should not unwind through the unmanaged callback.
        }
        finally
        {
            if (menu != 0)
            {
                try { DestroyMenu(menu); }
                catch { }
            }

            try
            {
                SendMessageW(_hwnd, WmNull, 0, 0);
                var data = CreateNotifyIconData();
                Shell_NotifyIconW(NimSetFocus, ref data);
            }
            catch { }
        }
    }

    private void InvokeSafely(Action action)
    {
        try { action(); }
        catch
        {
            IsAvailable = false;
            NotifyAvailabilityChanged();
        }
    }

    private void NotifyAvailabilityChanged()
    {
        try { AvailabilityChanged?.Invoke(); }
        catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NativeNotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int desiredWidth, int desiredHeight, uint loadFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint itemId, string text);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nuint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
