using System.Security.Cryptography;
using System.Text;

namespace ChatAgentBridge.Core.Sessions;

/// <summary>Builds session identities scoped to the human who owns a chat session.</summary>
public static class ChatSessionScope
{
    /// <summary>
    /// Creates a user-scoped key. Mattermost server URLs are reduced to their canonical base URL
    /// and hashed so credentials and server URL details are never persisted in the key.
    /// </summary>
    public static SessionKey Create(string provider, string channelId, string threadId, string userId, string? serverUrl = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A non-empty chat user id is required to scope a session.", nameof(userId));

        var normalizedProvider = provider?.Trim().ToLowerInvariant();
        string origin;
        if (string.Equals(normalizedProvider, "mattermost", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new ArgumentException("A Mattermost server URL is required to scope a session.", nameof(serverUrl));
            origin = HashServerUrl(serverUrl);
        }
        else if (string.Equals(normalizedProvider, "slack", StringComparison.Ordinal))
        {
            origin = "slack";
        }
        else if (string.Equals(normalizedProvider, "discord", StringComparison.Ordinal))
        {
            origin = "discord";
        }
        else
        {
            throw new ArgumentException("User-scoped sessions are supported for Slack, Mattermost, and Discord.", nameof(provider));
        }

        return new SessionKey(normalizedProvider!, channelId, threadId, userId, origin);
    }

    private static string HashServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Mattermost server URL must be an absolute HTTP(S) base URL without credentials, query, or fragment.", nameof(serverUrl));

        var path = uri.AbsolutePath;
        if (!path.EndsWith("/", StringComparison.Ordinal)) path += "/";
        // Scheme and host are case-insensitive; the path is intentionally left case-sensitive.
        var canonical = $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{path}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
