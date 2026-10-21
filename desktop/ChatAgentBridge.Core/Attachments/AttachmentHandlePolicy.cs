using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatAgentBridge.Core;

/// <summary>Checks file identity on Windows. Other platforms fail closed because hardlink and final-path checks are unavailable.</summary>
internal static class AttachmentHandlePolicy
{
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static void EnsureSafe(FileStream stream, string workspace, string expectedPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Safe attachment handle verification is supported only on Windows.");
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            throw new InvalidDataException("Attachment handle could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (information.NumberOfLinks != 1 ||
            (information.FileAttributes & (FileAttributeDevice | FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
            throw new InvalidDataException("Attachment file is linked or is not a regular file.");

        var finalPath = GetFinalPath(stream);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var normalizedFinal = Path.GetFullPath(finalPath);
        var normalizedExpected = Path.GetFullPath(expectedPath);
        if (IsNetworkPath(normalizedFinal) || !IsWithin(normalizedFinal, normalizedRoot) ||
            !string.Equals(normalizedFinal, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Attachment handle resolved outside the requested workspace file.");
    }

    private static string GetFinalPath(FileStream stream)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new InvalidDataException("Attachment handle path could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new InvalidDataException("Attachment handle path could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        var path = buffer.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + path[8..];
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private static bool IsNetworkPath(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    private static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
