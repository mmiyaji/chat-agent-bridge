using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

/// <summary>A locally checked supported CLI configuration. It contains no login credentials.</summary>
public sealed record AgentConnectionProfile(
    string Provider,
    string Executable,
    string Arguments,
    string Mode,
    string? Model,
    string? ReasoningEffort,
    string PermissionMode,
    bool AllowNetwork,
    string CliVersion,
    DateTimeOffset CheckedAt)
{
    /// <summary>Applies only agent settings, preserving workspace, chat, and trusted-folder configuration.</summary>
    public BridgeSettings ApplyTo(BridgeSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var profile = AgentConnectionStore.NormalizeAndValidate(this);
        return defaults with
        {
            AgentProvider = profile.Provider,
            AgentExecutable = profile.Executable,
            AgentArguments = profile.Arguments,
            AgentMode = profile.Mode,
            AgentModel = profile.Model,
            AgentReasoningEffort = profile.ReasoningEffort,
            AgentPermissionMode = profile.PermissionMode,
            AllowAgentNetwork = profile.AllowNetwork
        };
    }
}

/// <summary>Stores the locally verified CLI profiles used to switch an individual conversation.</summary>
public sealed class AgentConnectionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;

    public AgentConnectionStore(string? directory = null)
    {
        var directoryPath = directory ?? new SettingsStore().DirectoryPath;
        DirectoryPath = Path.GetFullPath(directoryPath);
        _filePath = Path.Combine(DirectoryPath, "agent-connections.json");
        _gate = Gates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string DirectoryPath { get; }

    public async Task<IReadOnlyList<AgentConnectionProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<AgentConnectionProfile?> FindAsync(string provider, CancellationToken cancellationToken = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(profile => profile.Provider == normalizedProvider);
    }

    /// <summary>Persists a profile after the caller has completed a successful local CLI check.</summary>
    public async Task SaveVerifiedAsync(AgentConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidate(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(existing => existing.Provider != normalized.Provider)
                .Append(normalized)
                .OrderBy(existing => existing.Provider, StringComparer.Ordinal)
                .ToArray();
            Directory.CreateDirectory(DirectoryPath);
            var stored = new StoredConnections { SchemaVersion = CurrentSchemaVersion, Profiles = profiles };
            var tempPath = Path.Combine(DirectoryPath, $"agent-connections.{Guid.NewGuid():N}.tmp");
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
        finally { _gate.Release(); }
    }

    internal static AgentConnectionProfile NormalizeAndValidate(AgentConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var provider = NormalizeProvider(profile.Provider);
        var executable = profile.Executable?.Trim() ?? "";
        if (executable.Length is 0 or > 2048 || executable.Any(char.IsControl))
            throw new ArgumentException("A CLI executable is required and must not contain control characters.", nameof(profile));
        var mode = profile.Mode?.Trim().ToLowerInvariant() ?? "";
        var arguments = profile.Arguments?.Trim() ?? "";
        if (provider == AgentProfile.Codex)
        {
            if (mode == "exec" && arguments.Length == 0) arguments = AgentProfile.DefaultCodexArguments;
            if (mode == "app-server" && arguments.Length == 0) arguments = "app-server";
            if (mode is not ("exec" or "app-server"))
                throw new ArgumentException("Codex mode must be exec or app-server.", nameof(profile));
            var expectedArguments = mode == "exec" ? AgentProfile.DefaultCodexArguments : "app-server";
            if (!string.Equals(arguments, expectedArguments, StringComparison.Ordinal))
                throw new ArgumentException("Codex profile arguments must use the supported canonical CLI arguments.", nameof(profile));
        }
        else if (provider == AgentProfile.Claude)
        {
            if (mode != "exec") throw new ArgumentException("Claude Code supports exec mode only.", nameof(profile));
            if (arguments.Length != 0)
                throw new ArgumentException("Claude Code arguments are generated by the bridge and must be empty in a saved profile.", nameof(profile));
        }
        else
        {
            if (mode != "exec") throw new ArgumentException("This CLI supports exec mode only.", nameof(profile));
            // Validate canonical output settings and reject configured selectors for another session.
            _ = AgentProfile.GetStructuredCliArguments(new BridgeSettings { AgentArguments = arguments }, provider);
        }

        string permissionMode;
        try { permissionMode = ExecutionPolicy.NormalizePermissionMode(profile.PermissionMode); }
        catch (ArgumentException ex) { throw new ArgumentException("The CLI permission mode is invalid.", nameof(profile), ex); }
        if (AgentProfile.IsBasicCliProvider(provider) && !AgentProfile.SupportsPermissionMode(provider, permissionMode))
            throw new ArgumentException(provider == AgentProfile.Antigravity
                ? "Antigravity CLI profiles support the inherit, workspace-write, and full-access permission modes."
                : "Gemini CLI and GitHub Copilot profiles must inherit their CLI permission settings.", nameof(profile));
        if (permissionMode == "ask" && provider != AgentProfile.Codex)
            throw new ArgumentException("Ask permission mode is available only for Codex App Server profiles.", nameof(profile));
        if (permissionMode == "ask" && mode != "app-server")
            throw new ArgumentException("Ask permission mode requires Codex App Server mode.", nameof(profile));

        string? model;
        string? effort;
        try
        {
            model = AgentProfile.NormalizeModel(profile.Model);
            effort = AgentProfile.NormalizeReasoningEffort(profile.ReasoningEffort);
        }
        catch (ArgumentException ex) { throw new ArgumentException("The model or reasoning effort is invalid.", nameof(profile), ex); }
        if (AgentProfile.IsBasicCliProvider(provider) && effort is not null)
            throw new ArgumentException("Reasoning effort overrides are unavailable for this CLI.", nameof(profile));
        var version = profile.CliVersion?.Trim() ?? "";
        if (version.Length is 0 or > 512 || version.Any(char.IsControl))
            throw new ArgumentException("A successfully checked CLI version is required.", nameof(profile));
        if (profile.CheckedAt == default || profile.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("The CLI check timestamp is missing or invalid.", nameof(profile));

        return profile with
        {
            Provider = provider,
            Executable = executable,
            Arguments = arguments,
            Mode = mode,
            Model = model,
            ReasoningEffort = effort,
            PermissionMode = permissionMode,
            CliVersion = version,
            CheckedAt = profile.CheckedAt.ToUniversalTime()
        };
    }

    private async Task<IReadOnlyList<AgentConnectionProfile>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<AgentConnectionProfile>();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredConnections>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) throw new InvalidDataException("Agent connections file is empty or invalid.");
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Agent connections schema version {stored.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            var profiles = (stored.Profiles ?? []).Select(NormalizeAndValidate).ToArray();
            if (profiles.GroupBy(profile => profile.Provider, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidDataException("Agent connections file contains more than one profile for a provider.");
            return profiles;
        }
        catch (JsonException ex) { throw new InvalidDataException("Agent connections file contains invalid JSON or unsupported fields.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Agent connections file contains an invalid CLI profile.", ex); }
    }

    private static string NormalizeProvider(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Antigravity => normalized,
            _ => throw new ArgumentException("Only supported CLI profiles can be registered.", nameof(provider))
        };
    }

    private sealed class StoredConnections
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public AgentConnectionProfile[]? Profiles { get; set; } = [];
    }
}
