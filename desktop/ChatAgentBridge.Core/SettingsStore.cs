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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly StorageOperationRunner _operations = new();
    // Guards against a disappearing backing store within this instance's lifetime; persistence is not journaled across process restarts.
    private bool _settingsPreviouslyPersisted;

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
        => await _operations.RunAsync("read", LoadCoreAsync, cancellationToken).ConfigureAwait(false);

    private async Task<BridgeSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var result = await ReadSettingsAsync(stream, cancellationToken).ConfigureAwait(false);
            _settingsPreviouslyPersisted = true;
            return result;
        }
        catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) &&
            !_settingsPreviouslyPersisted && IsDataVolumeAvailable())
        {
            // A missing file under an available volume is a first run, even when the app folder
            // has not been created yet. Missing/unavailable volumes and vanished saved files fail closed.
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return new BridgeSettings
            {
                WorkspaceRoot = Path.IsPathFullyQualified(documents) && Directory.Exists(documents) ? documents : "",
                MattermostAllowDirectMessages = true
            };
        }
    }

    private async Task<BridgeSettings> ReadSettingsAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) throw new InvalidDataException("Settings file is empty or invalid.");
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Settings schema version {stored.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            if (stored.AgentMode is not (null or "exec" or "app-server"))
                throw new InvalidDataException("Settings contain an unsupported agent mode.");
            var provider = stored.AgentProvider ?? InferLegacyProvider(stored.AgentExecutable ?? "codex");
            if (!AgentProfile.IsSupportedProvider(provider))
                throw new InvalidDataException("Settings contain an unsupported agent provider.");
            var autoConnect = stored.AutoConnectProvider ?? "none";
            if (autoConnect is not ("none" or "mattermost" or "slack" or "discord"))
                throw new InvalidDataException("Settings contain an unsupported auto-connect provider.");
            // Settings saved before the approval mode existed keep the per-conversation approval they were using.
            var approvalMode = ExecutionPolicy.NormalizeChatApprovalMode(
                string.IsNullOrWhiteSpace(stored.ChatApprovalMode) ? "first" : stored.ChatApprovalMode);
            var settings = new BridgeSettings
            {
                WorkspaceRoot = stored.WorkspaceRoot ?? "",
                AgentExecutable = stored.AgentExecutable ?? "codex",
                AgentArguments = AgentProfile.NormalizeStoredArguments(provider, stored.AgentArguments ?? "exec --json -"),
                AgentMode = stored.AgentMode ?? "exec",
                AgentProvider = provider,
                AgentModel = AgentProfile.NormalizeModel(stored.AgentModel),
                AgentReasoningEffort = AgentProfile.NormalizeReasoningEffort(stored.AgentReasoningEffort),
                AutoConnectProvider = autoConnect,
                MattermostServerUrl = stored.MattermostServerUrl ?? "",
                MattermostChannelId = stored.MattermostChannelId ?? "",
                MattermostAllowDirectMessages = stored.MattermostAllowDirectMessages,
                MattermostToken = Unprotect(stored.MattermostToken),
                MattermostBypassProxy = stored.MattermostBypassProxy,
                PostConnectionNotice = stored.PostConnectionNotice,
                SlackBotToken = Unprotect(stored.SlackBotToken),
                SlackAppToken = Unprotect(stored.SlackAppToken),
                SlackAllowDirectMessages = stored.SlackAllowDirectMessages,
                DiscordBotToken = Unprotect(stored.DiscordBotToken),
                DiscordAllowedUserIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(stored.DiscordAllowedUserIds ?? ""),
                DiscordAllowedGuildIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(stored.DiscordAllowedGuildIds ?? ""),
                DiscordAllowedChannelIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(stored.DiscordAllowedChannelIds ?? ""),
                DiscordAllowedThreadIds = ChatAccessPolicy.NormalizeDiscordSnowflakes(stored.DiscordAllowedThreadIds ?? ""),
                DiscordAllowDirectMessages = stored.DiscordAllowDirectMessages,
                MattermostAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(stored.MattermostAllowedUserIds ?? ""),
                SlackAllowedUserIds = ChatAccessPolicy.NormalizeAllowedUsers(stored.SlackAllowedUserIds ?? ""),
                ShareApprovalsInChat = stored.ShareApprovalsInChat,
                ShareAgentQuestionsInChat = stored.ShareAgentQuestionsInChat,
                RequireApproval = stored.RequireApproval,
                AgentPermissionMode = ExecutionPolicy.NormalizePermissionMode(stored.AgentPermissionMode),
                AllowAgentNetwork = stored.AllowAgentNetwork,
                TrustedWorkspaceRoots = ExecutionPolicy.NormalizeTrustedRoots(stored.TrustedWorkspaceRoots),
                ChatApprovalMode = approvalMode,
                AutoApproveSwitchCommands = stored.AutoApproveSwitchCommands &&
                    approvalMode is "auto" or "first",
                ChatResultMode = ExecutionPolicy.NormalizeChatResultMode(stored.ChatResultMode),
                ChatFollowUpMode = ChatFollowUpPolicy.Normalize(stored.ChatFollowUpMode),
                ChatProgressDetail = Core.ChatProgressDetail.Normalize(stored.ChatProgressDetail),
                SlackAllowedChannelIds = ChatAccessPolicy.NormalizeScopes(stored.SlackAllowedChannelIds ?? ""),
                SlackAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(stored.SlackAllowedThreadIds ?? ""),
                MattermostAllowedThreadIds = ChatAccessPolicy.NormalizeScopes(stored.MattermostAllowedThreadIds ?? ""),
                FileAttachmentApprovalMode = FileAttachmentPolicy.NormalizeMode(stored.FileAttachmentApprovalMode ?? "always"),
                FileAttachmentMaxSizeMb = stored.FileAttachmentMaxSizeMb,
                FileAttachmentMaxCount = stored.FileAttachmentMaxCount,
                FileAttachmentMaxTotalSizeMb = stored.FileAttachmentMaxTotalSizeMb
            };
            FileAttachmentPolicy.Validate(settings);
            if (provider == AgentProfile.Custom && settings.AgentModel is not null)
                throw new InvalidDataException("A custom agent cannot use a provider model override.");
            if (provider == AgentProfile.Custom && settings.AgentReasoningEffort is not null)
                throw new InvalidDataException("A custom agent cannot use a provider reasoning effort override.");
            if (AgentProfile.IsBasicCliProvider(provider) && !AgentProfile.SupportsPermissionMode(provider, settings.AgentPermissionMode))
                throw new InvalidDataException("This CLI's settings use an unsupported permission mode.");
            if (AgentProfile.IsBasicCliProvider(provider) && settings.AgentReasoningEffort is not null)
                throw new InvalidDataException("This CLI profile does not support a reasoning effort override in the bridge.");
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("Settings file contains invalid JSON.", ex); }
        catch (CryptographicException ex) { throw new InvalidDataException("A protected token in settings could not be decrypted for this Windows user.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("Settings contain a malformed protected token or invalid allowed-user list.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Settings contain an invalid provider or model value.", ex); }
    }

    private bool IsDataVolumeAvailable()
    {
        var current = Path.GetDirectoryName(_filePath);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                return (attributes & FileAttributes.Directory) != 0;
            }
            catch (DirectoryNotFoundException) { current = Directory.GetParent(current)?.FullName; }
            catch (FileNotFoundException) { current = Directory.GetParent(current)?.FullName; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
        return false;
    }

    public async Task SaveAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
        => await _operations.RunAsync("write", async token =>
        {
            var gate = WriteGates.GetOrAdd(_filePath, static _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { await SaveCoreAsync(settings, token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Grants access only if the pending request still belongs to the saved connection and scope.</summary>
    public Task<BridgeSettings?> TryAddAllowedUserAsync(BridgeSettings connected, string provider,
        string userId, string channelId, string threadId, string? guildId, CancellationToken cancellationToken = default,
        bool isDirectMessage = false)
        => _operations.RunAsync<BridgeSettings?>("write", async token =>
        {
            var gate = WriteGates.GetOrAdd(_filePath, static _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = await LoadCoreAsync(token).ConfigureAwait(false);
                if (!ChatAccessPolicy.CanApproveAccessRequest(current, connected, provider, channelId, threadId, guildId, isDirectMessage))
                    return null;
                var updated = ChatAccessPolicy.WithAllowedUser(current, provider, userId);
                if (!ReferenceEquals(updated, current)) await SaveCoreAsync(updated, token).ConfigureAwait(false);
                return updated;
            }
            finally { gate.Release(); }
        }, cancellationToken);

    private async Task SaveCoreAsync(BridgeSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.WorkspaceRoot is null || settings.WorkspaceRoot.Length > 0 &&
            (settings.WorkspaceRoot.Length > 2048 || settings.WorkspaceRoot.Any(char.IsControl) ||
             !Path.IsPathFullyQualified(settings.WorkspaceRoot)))
            throw new ArgumentException("Workspace root must be an absolute path of at most 2048 characters without control characters.", nameof(settings));
        if (settings.AgentMode is not ("exec" or "app-server"))
            throw new ArgumentException("Agent mode must be exec or app-server.", nameof(settings));
        var provider = AgentProfile.ResolveProvider(settings);
        var agentArguments = AgentProfile.ValidateManagedArguments(provider, settings.AgentArguments);
        if (settings.AutoConnectProvider is not ("none" or "mattermost" or "slack" or "discord"))
            throw new ArgumentException("Auto-connect provider must be none, mattermost, slack, or discord.", nameof(settings));
        var model = AgentProfile.NormalizeModel(settings.AgentModel);
        var effort = AgentProfile.NormalizeReasoningEffort(settings.AgentReasoningEffort);
        string mattermostAllowedUsers;
        string slackAllowedUsers;
        string slackAllowedChannels, slackAllowedThreads, mattermostAllowedThreads;
        string discordAllowedUsers, discordAllowedGuilds, discordAllowedChannels, discordAllowedThreads;
        string permissionMode, approvalMode, resultMode, followUpMode, trustedRoots, attachmentMode;
        try
        {
            mattermostAllowedUsers = ChatAccessPolicy.NormalizeAllowedUsers(settings.MattermostAllowedUserIds);
            slackAllowedUsers = ChatAccessPolicy.NormalizeAllowedUsers(settings.SlackAllowedUserIds);
            slackAllowedChannels = ChatAccessPolicy.NormalizeScopes(settings.SlackAllowedChannelIds);
            slackAllowedThreads = ChatAccessPolicy.NormalizeScopes(settings.SlackAllowedThreadIds);
            mattermostAllowedThreads = ChatAccessPolicy.NormalizeScopes(settings.MattermostAllowedThreadIds);
            discordAllowedUsers = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedUserIds);
            discordAllowedGuilds = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedGuildIds);
            discordAllowedChannels = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedChannelIds);
            discordAllowedThreads = ChatAccessPolicy.NormalizeDiscordSnowflakes(settings.DiscordAllowedThreadIds);
            permissionMode = ExecutionPolicy.NormalizePermissionMode(settings.AgentPermissionMode);
            approvalMode = ExecutionPolicy.NormalizeChatApprovalMode(settings.ChatApprovalMode);
            resultMode = ExecutionPolicy.NormalizeChatResultMode(settings.ChatResultMode);
            followUpMode = ChatFollowUpPolicy.Normalize(settings.ChatFollowUpMode);
            trustedRoots = ExecutionPolicy.NormalizeTrustedRoots(settings.TrustedWorkspaceRoots);
            attachmentMode = FileAttachmentPolicy.NormalizeMode(settings.FileAttachmentApprovalMode);
        }
        catch (FormatException ex) { throw new ArgumentException("Allowed-user or scope IDs are invalid: " + ex.Message, nameof(settings), ex); }
        var validatedSettings = settings with { FileAttachmentApprovalMode = attachmentMode };
        FileAttachmentPolicy.Validate(validatedSettings);
        if (provider == AgentProfile.Custom && model is not null)
            throw new ArgumentException("A custom agent cannot use a provider model override.", nameof(settings));
        if (provider == AgentProfile.Custom && effort is not null)
            throw new ArgumentException("A custom agent cannot use a provider reasoning effort override.", nameof(settings));
        if (AgentProfile.IsBasicCliProvider(provider) && !AgentProfile.SupportsPermissionMode(provider, permissionMode))
            throw new ArgumentException(AgentProfile.UnsupportedPermissionModeMessage(provider), nameof(settings));
        if (AgentProfile.IsBasicCliProvider(provider) && effort is not null)
            throw new ArgumentException("This CLI profile does not support a reasoning effort override in the bridge.", nameof(settings));
        Directory.CreateDirectory(DirectoryPath);
        var stored = new StoredSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            WorkspaceRoot = settings.WorkspaceRoot,
            AgentExecutable = settings.AgentExecutable,
            AgentArguments = agentArguments,
            AgentMode = settings.AgentMode,
            AgentProvider = provider,
            AgentModel = model,
            AgentReasoningEffort = effort,
            AutoConnectProvider = settings.AutoConnectProvider,
            MattermostServerUrl = settings.MattermostServerUrl,
            MattermostChannelId = settings.MattermostChannelId,
            MattermostAllowDirectMessages = settings.MattermostAllowDirectMessages,
            MattermostToken = Protect(settings.MattermostToken),
            MattermostBypassProxy = settings.MattermostBypassProxy,
            PostConnectionNotice = settings.PostConnectionNotice,
            SlackBotToken = Protect(settings.SlackBotToken),
            SlackAppToken = Protect(settings.SlackAppToken),
            SlackAllowDirectMessages = settings.SlackAllowDirectMessages,
            DiscordBotToken = Protect(settings.DiscordBotToken),
            DiscordAllowedUserIds = discordAllowedUsers,
            DiscordAllowedGuildIds = discordAllowedGuilds,
            DiscordAllowedChannelIds = discordAllowedChannels,
            DiscordAllowedThreadIds = discordAllowedThreads,
            DiscordAllowDirectMessages = settings.DiscordAllowDirectMessages,
            MattermostAllowedUserIds = mattermostAllowedUsers,
            SlackAllowedUserIds = slackAllowedUsers,
            ShareApprovalsInChat = settings.ShareApprovalsInChat,
            ShareAgentQuestionsInChat = settings.ShareAgentQuestionsInChat,
            RequireApproval = settings.RequireApproval,
            AgentPermissionMode = permissionMode,
            AllowAgentNetwork = settings.AllowAgentNetwork,
            TrustedWorkspaceRoots = trustedRoots,
            ChatApprovalMode = approvalMode,
            AutoApproveSwitchCommands = settings.AutoApproveSwitchCommands && approvalMode is "auto" or "first",
            ChatResultMode = resultMode,
            ChatFollowUpMode = followUpMode,
            ChatProgressDetail = Core.ChatProgressDetail.Normalize(settings.ChatProgressDetail),
            SlackAllowedChannelIds = slackAllowedChannels,
            SlackAllowedThreadIds = slackAllowedThreads,
            MattermostAllowedThreadIds = mattermostAllowedThreads,
            FileAttachmentApprovalMode = attachmentMode,
            FileAttachmentMaxSizeMb = settings.FileAttachmentMaxSizeMb,
            FileAttachmentMaxCount = settings.FileAttachmentMaxCount,
            FileAttachmentMaxTotalSizeMb = settings.FileAttachmentMaxTotalSizeMb
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
            _settingsPreviouslyPersisted = true;
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
        public bool MattermostAllowDirectMessages { get; set; } = true;
        public string? MattermostToken { get; set; }
        public bool MattermostBypassProxy { get; set; }
        public bool PostConnectionNotice { get; set; }
        public string? SlackBotToken { get; set; }
        public string? SlackAppToken { get; set; }
        public bool SlackAllowDirectMessages { get; set; } = true;
        public string? DiscordBotToken { get; set; }
        public string? DiscordAllowedUserIds { get; set; }
        public string? DiscordAllowedGuildIds { get; set; }
        public string? DiscordAllowedChannelIds { get; set; }
        public string? DiscordAllowedThreadIds { get; set; }
        public bool DiscordAllowDirectMessages { get; set; } = true;
        public string? MattermostAllowedUserIds { get; set; }
        public string? SlackAllowedUserIds { get; set; }
        public bool ShareApprovalsInChat { get; set; }
        public bool ShareAgentQuestionsInChat { get; set; }
        public bool RequireApproval { get; set; } = true;
        public string? AgentPermissionMode { get; set; }
        public bool AllowAgentNetwork { get; set; }
        public string? TrustedWorkspaceRoots { get; set; }
        public string? ChatApprovalMode { get; set; }
        public bool AutoApproveSwitchCommands { get; set; }
        public string? ChatResultMode { get; set; }
        public string? ChatFollowUpMode { get; set; }
        public string? ChatProgressDetail { get; set; }
        public string? SlackAllowedChannelIds { get; set; }
        public string? SlackAllowedThreadIds { get; set; }
        public string? MattermostAllowedThreadIds { get; set; }
        public string? FileAttachmentApprovalMode { get; set; } = "always";
        public int FileAttachmentMaxSizeMb { get; set; } = 10;
        public int FileAttachmentMaxCount { get; set; } = 3;
        public int FileAttachmentMaxTotalSizeMb { get; set; } = 20;
    }
}
