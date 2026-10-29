using System.Runtime.InteropServices;
using System.Text;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>
/// A parameterless AppNotificationManager.Register() gives an unpackaged app the executable name and a generic
/// icon in its toasts, and re-registers those on every launch. Packaged builds take both from the manifest.
/// </summary>
internal static class UnpackagedNotificationIdentity
{
    internal const string DisplayName = "Chat Agent Bridge";
    private const string IconRelativePath = @"Assets\Square44x44Logo.scale-200.png";
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    internal static bool IsPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    /// <summary>The icon to register for an unpackaged run, or null when the app has package identity or the file is missing.</summary>
    internal static Uri? FindIconUri()
    {
        try
        {
            if (IsPackaged() || string.IsNullOrEmpty(Environment.ProcessPath)) return null;
            var iconPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, IconRelativePath);
            return File.Exists(iconPath) ? new Uri(iconPath) : null;
        }
        catch (Exception error) when (error is IOException or UriFormatException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
