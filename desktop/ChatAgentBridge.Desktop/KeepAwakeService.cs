using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CommunityToolkit.Mvvm.ComponentModel;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop;

/// <summary>Owns a process-wide SystemRequired request and mirrors explicit app work intent.</summary>
public sealed partial class KeepAwakeService : ObservableObject, IDisposable
{
    private readonly KeepAwakePreferencesStore _store;
    private readonly NativeKeepAwakeRequest? _native;
    private readonly KeepAwakeRequestController? _requestController;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private KeepAwakePreferences _preferences = new();
    private string _storageError = "", _nativeError = "";
    private bool _receiverIntended, _requestActive, _manualActive, _onAcPower, _powerKnown, _closed;
    [ObservableProperty] public partial string Status { get; set; } = "loading";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Mode { get; set; } = KeepAwakeMode.Off;
    [ObservableProperty] public partial bool AcOnly { get; set; } = true;
    [ObservableProperty] public partial bool IsInitialized { get; set; }
    [ObservableProperty] public partial bool IsSaving { get; set; }
    public KeepAwakePreferences Preferences => _preferences;

    public KeepAwakeService(KeepAwakePreferencesStore store, nint hwnd)
    {
        _store = store;
        try
        {
            _native = new(hwnd, DesktopText.Get("KeepAwakeNativeReason"));
            _requestController = new(_native);
            _native.PowerSourceChanged += OnPowerSourceChanged;
            _native.Resume += OnResume;
            if (_native.CurrentAcPower is bool onAc) { _onAcPower = onAc; _powerKnown = true; }
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            _nativeError = ex.Message;
        }
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        try { _preferences = await Task.Run(() => _store.LoadAsync(token), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (_closed || token.IsCancellationRequested) return;
            Error = ex.Message;
            _storageError = Error;
            Status = "error";
            _preferences = new();
        }
        if (_closed || token.IsCancellationRequested) return;
        Mode = _preferences.Mode;
        AcOnly = _preferences.AcOnly;
        OnPropertyChanged(nameof(Preferences));
        IsInitialized = true;
        Reevaluate();
    }

    public async Task SaveAsync(string mode, bool acOnly, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!IsInitialized) throw new InvalidOperationException("Keep-awake preferences are not loaded.");
        var next = new KeepAwakePreferences { Mode = KeepAwakeMode.Normalize(mode), AcOnly = acOnly };
        IsSaving = true;
        var gateEntered = false;
        try
        {
            await _saveGate.WaitAsync(token);
            gateEntered = true;
            await Task.Run(() => _store.SaveAsync(next, token), token);
            if (_closed) return;
            _preferences = next;
            _storageError = "";
            Error = "";
            OnPropertyChanged(nameof(Preferences));
            Reevaluate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Error = ex.Message;
            _storageError = Error;
            Status = "error";
            throw;
        }
        finally { if (gateEntered) _saveGate.Release(); if (!_closed) IsSaving = false; }
    }

    public void UpdateSignals(bool receiverIntended, bool requestActive, bool manualActive)
    {
        if (_closed) return;
        _receiverIntended = receiverIntended; _requestActive = requestActive; _manualActive = manualActive;
        Reevaluate();
    }

    public void RestorePersistedAfterSaveFailure()
    {
        Mode = _preferences.Mode;
        AcOnly = _preferences.AcOnly;
        Reevaluate();
    }

    public async Task FlushPendingSaveAsync(CancellationToken token = default)
    {
        await _saveGate.WaitAsync(token);
        _saveGate.Release();
    }

    private void OnPowerSourceChanged(bool? onAc)
    {
        _onAcPower = onAc == true;
        _powerKnown = onAc.HasValue;
        // Recreate requests invalidated by a power transition even if the
        // selected policy still requires the system to remain awake.
        _requestController?.Resume();
        Reevaluate();
    }
    private void OnResume() { _requestController?.Resume(); Reevaluate(); }
    private void Reevaluate()
    {
        if (_closed || !IsInitialized) return;
        var desired = KeepAwakePolicy.ShouldStayAwake(_preferences, _receiverIntended, _requestActive, _manualActive, _onAcPower);
        try
        {
            _requestController?.Update(desired);
            if (_native is not null) _nativeError = "";
            var nativeUnavailable = _preferences.Mode != KeepAwakeMode.Off && (_nativeError.Length > 0 || _native is null);
            if (_storageError.Length > 0 || nativeUnavailable)
            {
                Error = _storageError.Length > 0 ? _storageError : _nativeError.Length > 0 ? _nativeError : "Native power requests are unavailable.";
                Status = "error";
                return;
            }
            Error = "";
            Status = desired ? "active" : _preferences.Mode != KeepAwakeMode.Off && _preferences.AcOnly && !_powerKnown ? "waiting" : "inactive";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _native?.AbandonAfterFailure();
            _requestController?.MarkInactiveAfterFailure();
            _nativeError = ex.Message;
            Error = ex.Message;
            Status = "error";
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        IsInitialized = false;
        if (_native is not null)
        {
            _native.PowerSourceChanged -= OnPowerSourceChanged;
            _native.Resume -= OnResume;
            _requestController?.Dispose();
        }
        // An in-flight preference write may still be releasing this gate.
    }
}

/// <summary>HWND-owned subclass observes power source/resume; callbacks only reapply policy.</summary>
internal sealed class NativeKeepAwakeRequest : IKeepAwakeRequest
{
    private const uint WmPowerBroadcast = 0x0218, PbtApmResumeSuspend = 0x0007, PbtApmResumeAutomatic = 0x0012, PbtPowerSettingChange = 0x8013;
    private const uint PowerRequestContextSimpleString = 1, PowerRequestSystemRequired = 1;
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");
    private static readonly UIntPtr SubclassId = new(0x4341424B);
    private readonly nint _hwnd;
    private readonly SubclassProc _proc;
    private readonly string _reason;
    private SafeFileHandle? _handle;
    private nint _powerNotification;
    private bool _isActive, _disposed;
    public event Action<bool?>? PowerSourceChanged;
    public event Action? Resume;
    public bool? CurrentAcPower { get; }

    public NativeKeepAwakeRequest(nint hwnd, string reason)
    {
        _hwnd = hwnd;
        _reason = reason;
        _proc = WindowProc;
        if (!SetWindowSubclass(hwnd, _proc, SubclassId, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe system power events.");
        var powerSetting = AcDcPowerSource;
        _powerNotification = RegisterPowerSettingNotification(hwnd, ref powerSetting, 0);
        if (_powerNotification == 0)
        {
            var error = Marshal.GetLastWin32Error();
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            throw new Win32Exception(error, "Could not observe AC power changes.");
        }
        if (GetSystemPowerStatus(out var power)) CurrentAcPower = power.AcLineStatus switch { 0 => false, 1 => true, _ => null };
    }

    public void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (active)
        {
            if (_isActive) return;
            _handle ??= CreateRequest();
            if (_handle.IsInvalid || _handle.IsClosed) throw new InvalidOperationException("The power request handle is unavailable.");
            if (!PowerSetRequest(_handle, PowerRequestSystemRequired)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the system power request.");
        }
        else
        {
            if (!_isActive) return;
            if (_handle is null || !PowerClearRequest(_handle, PowerRequestSystemRequired)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not clear the system power request.");
        }
        _isActive = active;
    }

    public void ResetAfterResume() { if (_disposed) return; _handle?.Dispose(); _handle = null; _isActive = false; }

    public void AbandonAfterFailure() { _handle?.Dispose(); _handle = null; _isActive = false; }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, UIntPtr id, nuint data)
    {
        try
        {
            if (message == WmPowerBroadcast)
            {
                var eventCode = unchecked((uint)wParam);
                if (eventCode is PbtApmResumeSuspend or PbtApmResumeAutomatic) Resume?.Invoke();
                else if (eventCode == PbtPowerSettingChange && lParam != 0 && _powerNotification != 0)
                {
                    var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == AcDcPowerSource && setting.DataLength >= sizeof(uint))
                        PowerSourceChanged?.Invoke(Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>()) switch { 0 => true, 1 => false, _ => null });
                }
            }
        }
        catch { /* Never let managed exceptions cross a native window-procedure boundary. */ }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isActive && _handle is { IsClosed: false, IsInvalid: false } handle) PowerClearRequest(handle, PowerRequestSystemRequired);
        _isActive = false;
        if (_powerNotification != 0) { UnregisterPowerSettingNotification(_powerNotification); _powerNotification = 0; }
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _handle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PowerRequestContext { public uint Version, Flags; public PowerRequestReason Reason; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PowerRequestReason
    {
        [FieldOffset(0)] public nint Detailed;
        [FieldOffset(0)] public nint Simple;
        [FieldOffset(8)] public uint ResourceId;
        [FieldOffset(12)] public uint ReasonStringCount;
        [FieldOffset(16)] public nint ReasonStrings;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, UIntPtr id, nuint data);

    private SafeFileHandle CreateRequest()
    {
        return CreateRequest(_reason);
    }

    private static SafeFileHandle CreateRequest(string reasonTextValue)
    {
        var simpleReason = Marshal.StringToHGlobalUni(reasonTextValue);
        SafeFileHandle handle;
        var error = 0;
        try
        {
            var reason = new PowerRequestContext { Version = 0, Flags = PowerRequestContextSimpleString,
                Reason = new PowerRequestReason { Simple = simpleReason } };
            handle = PowerCreateRequest(ref reason);
            if (handle.IsInvalid) error = Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(simpleReason); }
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(error, "Windows could not create a power request."); }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref PowerRequestContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle handle, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle handle, uint type);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, UIntPtr id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(nint handle);
    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
