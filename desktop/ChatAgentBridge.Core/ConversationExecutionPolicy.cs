using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Resolves conversation overrides and ties first-turn consent to the actual execution configuration.</summary>
public static class ConversationExecutionPolicy
{
    public static BridgeSettings GetEffectiveSettings(BridgeSettings defaults, SessionBinding? binding) => defaults with
    {
        ChatApprovalMode = ExecutionPolicy.NormalizeChatApprovalMode(binding?.ChatApprovalMode ?? defaults.ChatApprovalMode),
        ChatResultMode = ExecutionPolicy.NormalizeChatResultMode(binding?.ChatResultMode ?? defaults.ChatResultMode),
        AgentPermissionMode = ExecutionPolicy.NormalizePermissionMode(binding?.AgentPermissionMode ?? defaults.AgentPermissionMode),
        AgentModel = binding?.Model ?? defaults.AgentModel,
        AgentReasoningEffort = binding?.ReasoningEffort ??
            (binding?.Model is null || string.Equals(binding.Model, defaults.AgentModel, StringComparison.Ordinal)
                ? defaults.AgentReasoningEffort : null)
    };

    public static bool RequiresApproval(SessionKey key, BridgeSettings effective, SessionBinding? binding) =>
        effective.ChatApprovalMode switch
        {
            "auto" => false,
            "always" => true,
            "first" => !string.Equals(binding?.FirstApprovalFingerprint, GetFingerprint(key, effective), StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentException("Unknown chat approval mode.")
        };

    public static string GetFingerprint(SessionKey key, BridgeSettings settings)
    {
        // Credentials never leave this calculation. Only an irreversible digest is persisted.
        var payload = JsonSerializer.Serialize(new
        {
            key,
            Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot)),
            settings.AgentExecutable, settings.AgentArguments, settings.AgentProvider, settings.AgentMode,
            settings.AgentModel, settings.AgentReasoningEffort, settings.AgentPermissionMode,
            settings.AllowAgentNetwork, settings.TrustedWorkspaceRoots, settings.ChatApprovalMode,
            settings.ChatResultMode, settings.ShareApprovalsInChat,
            settings.MattermostAllowedUserIds, settings.SlackAllowedUserIds,
            settings.MattermostChannelId, settings.MattermostAllowedThreadIds,
            settings.SlackAllowedChannelIds, settings.SlackAllowedThreadIds,
            CredentialDigest = Digest(key.Provider == "slack"
                ? settings.SlackBotToken + "\0" + settings.SlackAppToken : settings.MattermostToken)
        });
        var baseFingerprint = Digest(payload);
        // Keep the historical digest byte-for-byte for the default-off setting so persisted
        // approvals remain valid when upgrading. Opting in creates a distinct consent scope.
        var fingerprint = settings.ShareAgentQuestionsInChat
            ? Digest(baseFingerprint + "\0share-agent-questions")
            : baseFingerprint;
        return ChatFollowUpPolicy.Normalize(settings.ChatFollowUpMode) == ChatFollowUpPolicy.Steer
            ? Digest(fingerprint + "\0chat-follow-up-steer")
            : fingerprint;
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
