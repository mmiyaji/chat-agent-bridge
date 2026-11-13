using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public static class FileAttachmentPolicy
{
    public static string NormalizeMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value) ? "always" : value.Trim().ToLowerInvariant();
        return mode is "disabled" or "always" or "first" or "auto"
            ? mode
            : throw new ArgumentException("Unsupported file attachment approval mode.", nameof(value));
    }

    /// <summary>"run": only files new in this run. "conversation": files created since the conversation began.
    /// "workspace": any eligible workspace file the agent names; sending those always needs approval on the PC.</summary>
    public static string NormalizeScope(string? value)
    {
        var scope = string.IsNullOrWhiteSpace(value) ? "run" : value.Trim().ToLowerInvariant();
        return scope is "run" or "conversation" or "workspace"
            ? scope
            : throw new ArgumentException("Unsupported file attachment scope.", nameof(value));
    }

    public static void Validate(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = NormalizeMode(settings.FileAttachmentApprovalMode);
        _ = NormalizeScope(settings.FileAttachmentScope);
        if (settings.FileAttachmentMaxSizeMb is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings.FileAttachmentMaxSizeMb), "Per-file attachment limit must be between 1 and 100 MiB.");
        if (settings.FileAttachmentMaxCount is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(settings.FileAttachmentMaxCount), "Attachment count limit must be between 1 and 10.");
        if (settings.FileAttachmentMaxTotalSizeMb is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings.FileAttachmentMaxTotalSizeMb), "Total attachment limit must be between 1 and 100 MiB.");
    }

    public static string GetConsentFingerprint(SessionKey key, string sessionId, BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A native session ID is required.", nameof(sessionId));
        Validate(settings);
        var payload = JsonSerializer.Serialize(new
        {
            Conversation = ConversationExecutionPolicy.GetFingerprint(key, settings),
            SessionId = sessionId,
            Mode = NormalizeMode(settings.FileAttachmentApprovalMode),
            Scope = NormalizeScope(settings.FileAttachmentScope),
            settings.FileAttachmentMaxSizeMb,
            settings.FileAttachmentMaxCount,
            settings.FileAttachmentMaxTotalSizeMb
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
