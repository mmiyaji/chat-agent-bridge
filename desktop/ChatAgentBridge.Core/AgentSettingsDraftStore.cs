using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

/// <summary>An editable agent configuration that has not yet passed a local CLI check.</summary>
public sealed record AgentSettingsDraft(
    string Provider,
    string Executable,
    string Arguments,
    string Mode,
    string? Model,
    string? ReasoningEffort,
    string PermissionMode,
    bool AllowNetwork)
{
    /// <summary>Creates a provider draft from the agent fields in settings.</summary>
    public static AgentSettingsDraft FromSettings(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return AgentSettingsDraftStore.NormalizeAndValidate(new AgentSettingsDraft(
            AgentProfile.ResolveProvider(settings), settings.AgentExecutable, settings.AgentArguments,
            settings.AgentMode, settings.AgentModel, settings.AgentReasoningEffort,
            settings.AgentPermissionMode, settings.AllowAgentNetwork));
    }

    /// <summary>Applies only agent fields, preserving shared workspace, trust, and chat settings.</summary>
    public BridgeSettings ApplyTo(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var draft = AgentSettingsDraftStore.NormalizeAndValidate(this);
        return settings with
        {
            AgentProvider = draft.Provider,
            AgentExecutable = draft.Executable,
            AgentArguments = draft.Arguments,
            AgentMode = draft.Mode,
            AgentModel = draft.Model,
            AgentReasoningEffort = draft.ReasoningEffort,
            AgentPermissionMode = draft.PermissionMode,
            AllowAgentNetwork = draft.AllowNetwork
        };
    }
}

/// <summary>Stores editable per-provider agent drafts separately from CLI-verified connections.</summary>
public sealed class AgentSettingsDraftStore
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

    public AgentSettingsDraftStore(string? directory = null)
    {
        var directoryPath = directory ?? new SettingsStore().DirectoryPath;
        DirectoryPath = Path.GetFullPath(directoryPath);
        _filePath = Path.Combine(DirectoryPath, "agent-settings-drafts.json");
        _gate = Gates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string DirectoryPath { get; }

    public async Task<IReadOnlyList<AgentSettingsDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Persists an editable draft without treating it as a verified CLI connection.</summary>
    public async Task SaveAsync(AgentSettingsDraft draft, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidate(draft);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var drafts = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(existing => existing.Provider != normalized.Provider)
                .Append(normalized)
                .OrderBy(existing => existing.Provider, StringComparer.Ordinal)
                .ToArray();
            Directory.CreateDirectory(DirectoryPath);
            var stored = new StoredDrafts { SchemaVersion = CurrentSchemaVersion, Drafts = drafts };
            var tempPath = Path.Combine(DirectoryPath, $"agent-settings-drafts.{Guid.NewGuid():N}.tmp");
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

    internal static AgentSettingsDraft NormalizeAndValidate(AgentSettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var provider = NormalizeProvider(draft.Provider);
        var executable = NormalizeEditableText(draft.Executable, 2048, nameof(draft.Executable));
        var arguments = NormalizeEditableText(draft.Arguments, 8192, nameof(draft.Arguments), allowArgumentWhitespace: true);
        var mode = NormalizeEditableText(draft.Mode, 32, nameof(draft.Mode)).ToLowerInvariant();
        var permissionMode = NormalizeEditableText(draft.PermissionMode, 32, nameof(draft.PermissionMode)).ToLowerInvariant();
        string? model;
        string? effort;
        try
        {
            model = AgentProfile.NormalizeModel(draft.Model);
            effort = AgentProfile.NormalizeReasoningEffort(draft.ReasoningEffort);
        }
        catch (ArgumentException ex) { throw new ArgumentException("The model or reasoning effort is invalid.", nameof(draft), ex); }

        return draft with
        {
            Provider = provider,
            Executable = executable,
            Arguments = arguments,
            Mode = mode,
            Model = model,
            ReasoningEffort = effort,
            PermissionMode = permissionMode
        };
    }

    private async Task<IReadOnlyList<AgentSettingsDraft>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<AgentSettingsDraft>();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredDrafts>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) throw new InvalidDataException("Agent settings drafts file is empty or invalid.");
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Agent settings drafts schema version {stored.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            if (stored.Drafts is null) throw new InvalidDataException("Agent settings drafts file has no draft list.");
            if (stored.Drafts.Any(draft => draft is null))
                throw new InvalidDataException("Agent settings drafts file contains a null draft.");
            var drafts = stored.Drafts.Select(NormalizeAndValidate).ToArray();
            if (drafts.GroupBy(draft => draft.Provider, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidDataException("Agent settings drafts file contains more than one draft for a provider.");
            return drafts;
        }
        catch (JsonException ex) { throw new InvalidDataException("Agent settings drafts file contains invalid JSON or unsupported fields.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Agent settings drafts file contains an invalid draft.", ex); }
    }

    private static string NormalizeProvider(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Custom => normalized,
            _ => throw new ArgumentException("Only supported agent providers can have settings drafts.", nameof(provider))
        };
    }

    private static string NormalizeEditableText(string? value, int maxLength, string parameterName, bool allowArgumentWhitespace = false)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > maxLength || normalized.Any(character => char.IsControl(character) &&
            !(allowArgumentWhitespace && character is '\r' or '\n' or '\t')))
            throw new ArgumentException("Draft text is too long or contains control characters.", parameterName);
        return normalized;
    }

    private sealed class StoredDrafts
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public AgentSettingsDraft[]? Drafts { get; set; } = [];
    }
}
