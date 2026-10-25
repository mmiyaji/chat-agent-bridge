using System.Security.Cryptography;
using System.Text;

namespace ChatAgentBridge.Core.Deployment;

/// <summary>
/// Names the single live instance allowed per data folder. Two windows on one folder would both
/// answer the same chat requests and overwrite each other's saved state; separate folders
/// (profiles, isolated test runs) keep running side by side.
/// </summary>
public static class AppInstanceKey
{
    public static string For(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        // Windows paths are case-insensitive, and a trailing separator names the same folder.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return "ChatAgentBridge.Data." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..32];
    }
}
