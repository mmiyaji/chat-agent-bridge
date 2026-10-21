// Interop for the isolated test shortcut only. No registration or process identity changes.
// https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DesktopToasts/CPP/DesktopToastsSample.cpp
#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NotificationNativeProbe.ShortcutDiagnostic;

public sealed class ShortcutEvidence
{
    public string Target { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public string IconPath { get; set; }
    public int IconIndex { get; set; }
    public string AppUserModelId { get; set; }
    public Guid ToastActivatorClsid { get; set; }
    public ushort AppIdVariantType { get; set; }
    public ushort ActivatorVariantType { get; set; }
}

public static class ShortcutFile
{
    private static readonly Guid PropertyFormat = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    public static void Create(string file, string target, string arguments, string appId, Guid activator, string iconPath)
    {
        object instance = new ShellLink();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target));
            link.SetDescription("Isolated Notification Native Probe diagnostic");
            if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
            var store = (IPropertyStore)instance;
            var appKey = new PropertyKey(PropertyFormat, 5);
            var activatorKey = new PropertyKey(PropertyFormat, 26);
            var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref appKey, ref value); }
            finally { PropVariantClear(ref value); }
            value = new PropVariant { Type = 72, Pointer = Marshal.AllocCoTaskMem(16) };
            try
            {
                Marshal.StructureToPtr(activator, value.Pointer, false);
                store.SetValue(ref activatorKey, ref value);
            }
            finally { PropVariantClear(ref value); }
            store.Commit();
            ((IPersistFile)instance).Save(file, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    public static ShortcutEvidence Read(string file)
    {
        object instance = new ShellLink();
        try
        {
            ((IPersistFile)instance).Load(file, 0);
            var link = (IShellLinkW)instance;
            var path = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var workingDirectory = new StringBuilder(32768);
            var iconPath = new StringBuilder(32768);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            link.GetIconLocation(iconPath, iconPath.Capacity, out int iconIndex);
            var evidence = new ShortcutEvidence { Target = path.ToString(), Arguments = arguments.ToString(), WorkingDirectory = workingDirectory.ToString(), IconPath = iconPath.ToString(), IconIndex = iconIndex };
            var store = (IPropertyStore)instance;
            var key = new PropertyKey(PropertyFormat, 5);
            store.GetValue(ref key, out PropVariant value);
            try
            {
                evidence.AppIdVariantType = value.Type;
                if (value.Type != 31) throw new InvalidOperationException("AUMID is not VT_LPWSTR.");
                evidence.AppUserModelId = Marshal.PtrToStringUni(value.Pointer);
            }
            finally { PropVariantClear(ref value); }
            key = new PropertyKey(PropertyFormat, 26);
            store.GetValue(ref key, out value);
            try
            {
                evidence.ActivatorVariantType = value.Type;
                if (value.Type != 72) throw new InvalidOperationException("Activator is not VT_CLSID.");
                evidence.ToastActivatorClsid = Marshal.PtrToStructure<Guid>(value.Pointer);
            }
            finally { PropVariantClear(ref value); }
            return evidence;
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(Guid format, uint id) { Format = format; Id = id; }
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
