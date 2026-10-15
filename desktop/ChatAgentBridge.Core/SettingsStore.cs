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
            return new BridgeSettings
            {
                WorkspaceRoot = stored.WorkspaceRoot ?? "",
                AgentExecutable = stored.AgentExecutable ?? "codex",
                AgentArguments = stored.AgentArguments ?? "exec --json -",
                AgentMode = stored.AgentMode ?? "exec",
                MattermostServerUrl = stored.MattermostServerUrl ?? "",
                MattermostChannelId = stored.MattermostChannelId ?? "",
                MattermostToken = Unprotect(stored.MattermostToken),
                SlackBotToken = Unprotect(stored.SlackBotToken),
                SlackAppToken = Unprotect(stored.SlackAppToken),
                RequireApproval = stored.RequireApproval
            };
        }
        catch (JsonException ex) { throw new InvalidDataException("Settings file contains invalid JSON.", ex); }
        catch (CryptographicException ex) { throw new InvalidDataException("A protected token in settings could not be decrypted for this Windows user.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("A protected token in settings is malformed.", ex); }
    }

    public async Task SaveAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AgentMode is not ("exec" or "app-server"))
            throw new ArgumentException("Agent mode must be exec or app-server.", nameof(settings));
        Directory.CreateDirectory(DirectoryPath);
        var stored = new StoredSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            WorkspaceRoot = settings.WorkspaceRoot,
            AgentExecutable = settings.AgentExecutable,
            AgentArguments = settings.AgentArguments,
            AgentMode = settings.AgentMode,
            MattermostServerUrl = settings.MattermostServerUrl,
            MattermostChannelId = settings.MattermostChannelId,
            MattermostToken = Protect(settings.MattermostToken),
            SlackBotToken = Protect(settings.SlackBotToken),
            SlackAppToken = Protect(settings.SlackAppToken),
            RequireApproval = settings.RequireApproval
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
            if (File.Exists(_filePath)) File.Move(tempPath, _filePath, overwrite: true);
            else File.Move(tempPath, _filePath);
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
        public string? MattermostServerUrl { get; set; }
        public string? MattermostChannelId { get; set; }
        public string? MattermostToken { get; set; }
        public string? SlackBotToken { get; set; }
        public string? SlackAppToken { get; set; }
        public bool RequireApproval { get; set; } = true;
    }
}
