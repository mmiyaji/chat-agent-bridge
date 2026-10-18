using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace ChatAgentBridge.Core;

public sealed class SettingsStore
{
    private const int CurrentSchemaVersion = 1;
    private const string ProtectedPrefix = "dpapi:";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public SettingsStore(string? directory = null)
    {
        var configuredDirectory = directory ?? Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR");
        DirectoryPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatAgentBridge.WinUI")
            : configuredDirectory);
        _filePath = Path.Combine(DirectoryPath, "settings.json");
    }

    public string DirectoryPath { get; }

    public async Task<BridgeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new BridgeSettings();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) throw new InvalidDataException("Settings file is empty or invalid.");
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Settings schema version {stored.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            if (stored.AgentMode is not (null or "exec" or "app-server"))
                throw new InvalidDataException("Settings contain an unsupported agent mode.");
            var provider = stored.AgentProvider ?? InferLegacyProvider(stored.AgentExecutable ?? "codex");
            if (provider is not (AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Custom))
                throw new InvalidDataException("Settings contain an unsupported agent provider.");
            var autoConnect = stored.AutoConnectProvider ?? "none";
            if (autoConnect is not ("none" or "mattermost" or "slack"))
                throw new InvalidDataException("Settings contain an unsupported auto-connect provider.");
            var settings = new BridgeSettings
            {
                WorkspaceRoot = stored.WorkspaceRoot ?? "",
                AgentExecutable = stored.AgentExecutable ?? "codex",
                AgentArguments = stored.AgentArguments ?? "exec --json -",
                AgentMode = stored.AgentMode ?? "exec",
                AgentProvider = provider,
                AgentModel = AgentProfile.NormalizeModel(stored.AgentModel),
                AgentReasoningEffort = AgentProfile.NormalizeReasoningEffort(stored.AgentReasoningEffort),
                AutoConnectProvider = autoConnect,
                MattermostServerUrl = stored.MattermostServerUrl ?? "",
                MattermostChannelId = stored.MattermostChannelId ?? "",
                MattermostToken = Unprotect(stored.MattermostToken),
                SlackBotToken = Unprotect(stored.SlackBotToken),
                SlackAppToken = Unprotect(stored.SlackAppToken),
                MattermostAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(stored.MattermostAllowedUserIds ?? ""),
                SlackAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(stored.SlackAllowedUserIds ?? ""),
                ShareApprovalsInChat = stored.ShareApprovalsInChat,
                RequireApproval = stored.RequireApproval,
                AgentPermissionMode = ExecutionPolicy.NormalizePermissionMode(stored.AgentPermissionMode),
                AllowAgentNetwork = stored.AllowAgentNetwork,
                TrustedWorkspaceRoots = ExecutionPolicy.NormalizeTrustedRoots(stored.TrustedWorkspaceRoots),
                ChatApprovalMode = ExecutionPolicy.NormalizeChatApprovalMode(stored.ChatApprovalMode),
                ChatResultMode = ExecutionPolicy.NormalizeChatResultMode(stored.ChatResultMode),
                SlackAllowedChannelIds = ChatAccessPolicy.NormalizeScopes(stored.SlackAllowedChannelIds ?? ""),
                SlackAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(stored.SlackAllowedThreadIds ?? ""),
                MattermostAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(stored.MattermostAllowedThreadIds ?? "")
            };
            if (provider == AgentProfile.Custom && settings.AgentModel is not null)
                throw new InvalidDataException("A custom agent cannot use a provider model override.");
            if (provider == AgentProfile.Custom && settings.AgentReasoningEffort is not null)
                throw new InvalidDataException("A custom agent cannot use a provider reasoning effort override.");
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("Settings file contains invalid JSON.", ex); }
        catch (CryptographicException ex) { throw new InvalidDataException("A protected token in settings could not be decrypted for this Windows user.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("Settings contain a malformed protected token or invalid allowed-user list.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Settings contain an invalid provider or model value.", ex); }
    }

    public async Task SaveAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AgentMode is not ("exec" or "app-server"))
            throw new ArgumentException("Agent mode must be exec or app-server.", nameof(settings));
        var provider = AgentProfile.ResolveProvider(settings);
        if (settings.AutoConnectProvider is not ("none" or "mattermost" or "slack"))
            throw new ArgumentException("Auto-connect provider must be none, mattermost, or slack.", nameof(settings));
        var model = AgentProfile.NormalizeModel(settings.AgentModel);
        var effort = AgentProfile.NormalizeReasoningEffort(settings.AgentReasoningEffort);
        string mattermostAllowedUsers;
        string slackAllowedUsers;
        string slackAllowedChannels, slackAllowedThreads, mattermostAllowedThreads;
        string permissionMode, approvalMode, resultMode, trustedRoots;
        try
        {
            mattermostAllowedUsers = ChatAccessPolicy.NormalizeAllowedUsers(settings.MattermostAllowedUserIds);
            slackAllowedUsers = ChatAccessPolicy.NormalizeAllowedUsers(settings.SlackAllowedUserIds);
            slackAllowedChannels = ChatAccessPolicy.NormalizeScopes(settings.SlackAllowedChannelIds);
            slackAllowedThreads = ChatAccessPolicy.NormalizeScopes(settings.SlackAllowedThreadIds);
            mattermostAllowedThreads = ChatAccessPolicy.NormalizeScopes(settings.MattermostAllowedThreadIds);
            permissionMode = ExecutionPolicy.NormalizePermissionMode(settings.AgentPermissionMode);
            approvalMode = ExecutionPolicy.NormalizeChatApprovalMode(settings.ChatApprovalMode);
            resultMode = ExecutionPolicy.NormalizeChatResultMode(settings.ChatResultMode);
            trustedRoots = ExecutionPolicy.NormalizeTrustedRoots(settings.TrustedWorkspaceRoots);
        }
        catch (FormatException ex) { throw new ArgumentException("Allowed-user or scope IDs are invalid: " + ex.Message, nameof(settings), ex); }
        if (provider == AgentProfile.Custom && model is not null)
            throw new ArgumentException("A custom agent cannot use a provider model override.", nameof(settings));
        if (provider == AgentProfile.Custom && effort is not null)
            throw new ArgumentException("A custom agent cannot use a provider reasoning effort override.", nameof(settings));
        Directory.CreateDirectory(DirectoryPath);
        var stored = new StoredSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            WorkspaceRoot = settings.WorkspaceRoot,
            AgentExecutable = settings.AgentExecutable,
            AgentArguments = settings.AgentArguments,
            AgentMode = settings.AgentMode,
            AgentProvider = provider,
            AgentModel = model,
            AgentReasoningEffort = effort,
            AutoConnectProvider = settings.AutoConnectProvider,
            MattermostServerUrl = settings.MattermostServerUrl,
            MattermostChannelId = settings.MattermostChannelId,
            MattermostToken = Protect(settings.MattermostToken),
            SlackBotToken = Protect(settings.SlackBotToken),
            SlackAppToken = Protect(settings.SlackAppToken),
            MattermostAllowedUserIds = mattermostAllowedUsers,
            SlackAllowedUserIds = slackAllowedUsers,
            ShareApprovalsInChat = settings.ShareApprovalsInChat,
            RequireApproval = settings.RequireApproval,
            AgentPermissionMode = permissionMode,
            AllowAgentNetwork = settings.AllowAgentNetwork,
            TrustedWorkspaceRoots = trustedRoots,
            ChatApprovalMode = approvalMode,
            ChatResultMode = resultMode,
            SlackAllowedChannelIds = slackAllowedChannels,
            SlackAllowedThreadIds = slackAllowedThreads,
            MattermostAllowedThreadIds = mattermostAllowedThreads
        };
        var tempPath = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static string Protect(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Saving chat tokens requires Windows DPAPI; plaintext token storage is disabled on this platform.");
        return ProtectedPrefix + Convert.ToBase64String(ProtectWindows(Encoding.UTF8.GetBytes(token)));
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Settings contain an unprotected token; refusing to load it.");
        }
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("This settings file contains a Windows-protected token and must be loaded on Windows.");
        var bytes = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
        return Encoding.UTF8.GetString(UnprotectWindows(bytes));
    }

    private static string InferLegacyProvider(string executable)
    {
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        return new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }
            .Contains(leaf, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            ? AgentProfile.Codex : AgentProfile.Custom;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] value) => ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] value) => ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);

    private sealed class StoredSettings
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string? WorkspaceRoot { get; set; }
        public string? AgentExecutable { get; set; }
        public string? AgentArguments { get; set; }
        public string? AgentMode { get; set; }
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
        public string? AgentReasoningEffort { get; set; }
        public string? AutoConnectProvider { get; set; }
        public string? MattermostServerUrl { get; set; }
        public string? MattermostChannelId { get; set; }
        public string? MattermostToken { get; set; }
        public string? SlackBotToken { get; set; }
        public string? SlackAppToken { get; set; }
        public string? MattermostAllowedUserIds { get; set; }
        public string? SlackAllowedUserIds { get; set; }
        public bool ShareApprovalsInChat { get; set; }
        public bool RequireApproval { get; set; } = true;
        public string? AgentPermissionMode { get; set; }
        public bool AllowAgentNetwork { get; set; }
        public string? TrustedWorkspaceRoots { get; set; }
        public string? ChatApprovalMode { get; set; }
        public string? ChatResultMode { get; set; }
        public string? SlackAllowedChannelIds { get; set; }
        public string? SlackAllowedThreadIds { get; set; }
        public string? MattermostAllowedThreadIds { get; set; }
    }
}
