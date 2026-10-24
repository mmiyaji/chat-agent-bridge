using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatAgentBridge.Core;

/// <summary>Checks file identity on Windows. Other platforms fail closed because hardlink and final-path checks are unavailable.</summary>
internal static class AttachmentHandlePolicy
{
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileAttributeSystem = 0x00000004;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;

    public static void EnsureSafe(FileStream stream, string workspace, string expectedPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Safe attachment handle verification is supported only on Windows.");
        var information = GetInformation(stream.SafeFileHandle);

        var finalPath = GetFinalPath(stream);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var normalizedFinal = Path.GetFullPath(finalPath);
        var normalizedExpected = Path.GetFullPath(expectedPath);
        if (IsNetworkPath(normalizedFinal) || !IsWithin(normalizedFinal, normalizedRoot) ||
            !string.Equals(normalizedFinal, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Attachment handle resolved outside the requested workspace file.");
    }

    public static AttachmentFileIdentity GetIdentity(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe attachment handle verification is supported only on Windows.");
        var info = GetInformation(stream.SafeFileHandle);
        var creationFileTime = ((long)(uint)info.CreationTime.dwHighDateTime << 32) | (uint)info.CreationTime.dwLowDateTime;
        return new AttachmentFileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow,
            DateTime.FromFileTimeUtc(creationFileTime).Ticks);
    }

    public static void DeleteVerifiedFile(string path, string workspace, AttachmentFileIdentity expectedIdentity,
        long? expectedLength, string? expectedSha256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe attachment handle verification is supported only on Windows.");
        using var handle = CreateFile(path, GenericRead | GenericWrite | DeleteAccess, ShareRead, IntPtr.Zero,
            OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Incoming attachment could not be opened for safe cleanup.", new Win32Exception(Marshal.GetLastWin32Error()));
        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        EnsureSafe(stream, workspace, path);
        if (GetIdentity(stream) != expectedIdentity) return;
        if (stream.Length > IncomingAttachmentStager.MaximumAttachmentBytes) return;
        if (expectedLength is not null && stream.Length != expectedLength.Value) return;
        if (expectedSha256 is not null)
        {
            stream.Position = 0;
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal)) return;
        }
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfo, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new IOException("Incoming attachment could not be safely deleted.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static ByHandleFileInformation GetInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new InvalidDataException("Attachment handle could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (information.NumberOfLinks != 1 ||
            (information.FileAttributes & (FileAttributeDevice | FileAttributeDirectory | FileAttributeHidden |
                FileAttributeSystem | FileAttributeReparsePoint)) != 0)
            throw new InvalidDataException("Attachment file is linked, hidden, a system file, or is not a regular file.");
        return information;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInformation information, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

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

    internal readonly record struct AttachmentFileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow, long CreationTimeUtcTicks);
}
