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

    /// <summary>
    /// Folder and agent switch commands normally always wait for approval on this PC. The opt-in only applies
    /// with automatic approval or after first-turn consent; callers must also verify exact conversation consent
    /// through SessionStore.RequiresApprovalAsync. Trusted-folder and registered-CLI checks still apply.
    /// </summary>
    public static bool AutoApprovesSwitchCommands(BridgeSettings effective) =>
        effective.ChatApprovalMode is "auto" or "first" && effective.AutoApproveSwitchCommands;

    /// <summary>
    /// Identifies what a first-request approval consented to: where and how the CLI runs, and with which
    /// credentials. Model, sharing and allow-list changes do not change what the CLI may do on this PC,
    /// so they keep an existing consent.
    /// </summary>
    public static string GetFingerprint(SessionKey key, BridgeSettings settings)
    {
        // Credentials never leave this calculation. Only an irreversible digest is persisted.
        var payload = JsonSerializer.Serialize(new
        {
            key,
            Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspaceRoot)),
            settings.AgentExecutable, settings.AgentArguments, settings.AgentProvider, settings.AgentMode,
            settings.AgentPermissionMode, settings.AllowAgentNetwork, settings.TrustedWorkspaceRoots,
            CredentialDigest = Digest(key.Provider switch
            {
                "slack" => settings.SlackBotToken + "\0" + settings.SlackAppToken,
                "discord" => settings.DiscordBotToken,
                _ => settings.MattermostToken
            })
        });
        return Digest("execution-v2\0" + payload);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
