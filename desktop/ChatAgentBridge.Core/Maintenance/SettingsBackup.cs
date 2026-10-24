using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core.Maintenance;

/// <summary>Allowlisted portable preferences. Credentials, CLI arguments and authorizations are never exported.</summary>
public sealed record SettingsBackup
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Provider { get; init; } = AgentProfile.Codex;
    public string Executable { get; init; } = "codex";
    public string Mode { get; init; } = "exec";
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string Workspace { get; init; } = "";
    public string MattermostServer { get; init; } = "";
    public string MattermostChannel { get; init; } = "";
    public bool MattermostBypassProxy { get; init; }
    public string SlackChannel { get; init; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static SettingsBackup Create(BridgeSettings settings) => new()
    {
        Provider = settings.AgentProvider,
        Executable = settings.AgentExecutable,
        Mode = settings.AgentMode,
        Model = settings.AgentModel,
        ReasoningEffort = settings.AgentReasoningEffort,
        Workspace = settings.WorkspaceRoot,
        MattermostServer = SafeServer(settings.MattermostServerUrl),
        MattermostChannel = settings.MattermostChannelId,
        MattermostBypassProxy = settings.MattermostBypassProxy,
        SlackChannel = settings.SlackAllowedChannelIds
    };

    public byte[] Serialize()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
    }

    public static SettingsBackup Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 65536) throw new InvalidDataException("Invalid backup size.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            new[] { "SchemaVersion", "CreatedAt", "Provider", "Executable" }.Any(name => !document.RootElement.TryGetProperty(name, out _)))
            throw new InvalidDataException("The selected file is not a settings backup.");
        var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Duplicate backup fields.");
        var result = JsonSerializer.Deserialize<SettingsBackup>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Empty backup.");
        result.Validate();
        return result;
    }

    /// <summary>Restore requires a separate explicit decision to trust the saved workspace.</summary>
    public BridgeSettings ToSettings(bool trustWorkspace)
    {
        Validate();
        var arguments = Provider switch
        {
            AgentProfile.Codex => Mode == "app-server" ? "app-server" : AgentProfile.DefaultCodexArguments,
            AgentProfile.Gemini => AgentProfile.DefaultGeminiArguments,
            AgentProfile.GitHubCopilot => AgentProfile.DefaultGitHubCopilotArguments,
            AgentProfile.Antigravity => AgentProfile.DefaultAntigravityArguments,
            _ => ""
        };
        return new BridgeSettings
        {
            AgentProvider = Provider,
            AgentExecutable = Executable,
            AgentMode = Mode,
            AgentArguments = arguments,
            AgentModel = Provider == AgentProfile.Custom ? null : Model,
            AgentReasoningEffort = Provider is AgentProfile.Codex or AgentProfile.Claude ? ReasoningEffort : null,
            // Empty roots fall back to WorkspaceRoot in the execution policy, so clear both unless explicitly trusted.
            WorkspaceRoot = trustWorkspace ? Workspace : "",
            TrustedWorkspaceRoots = trustWorkspace ? Workspace : "",
            MattermostServerUrl = SafeServer(MattermostServer),
            MattermostChannelId = MattermostChannel,
            MattermostBypassProxy = MattermostBypassProxy,
            SlackAllowedChannelIds = SlackChannel,
            AutoConnectProvider = "none",
            RequireApproval = true,
            ChatApprovalMode = "always",
            ChatResultMode = "manual",
            AgentPermissionMode = "inherit",
            FileAttachmentApprovalMode = "always"
        };
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || CreatedAt == default ||
            Provider is not (AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Antigravity or AgentProfile.Custom) ||
            Mode is not ("exec" or "app-server") || Mode == "app-server" && Provider != AgentProfile.Codex)
            throw new InvalidDataException("Unsupported backup format.");
        foreach (var value in new[] { Executable, Workspace, MattermostServer, MattermostChannel, SlackChannel })
            if (value is null || value.Length > 4096 || value.Any(c => char.IsControl(c) && c is not '\r' and not '\n'))
                throw new InvalidDataException("Invalid backup field.");
        if (Executable.Length == 0 || Executable.Any(char.IsControl) || Workspace.Any(char.IsControl) ||
            Workspace.Length > 0 && !Path.IsPathFullyQualified(Workspace))
            throw new InvalidDataException("Invalid backup paths.");
        _ = AgentProfile.NormalizeModel(Model);
        _ = AgentProfile.NormalizeReasoningEffort(ReasoningEffort);
        _ = SafeServer(MattermostServer);
        _ = ChatAccessPolicy.NormalizeScopes(SlackChannel);
    }

    private static string SafeServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Invalid server URL.");
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
    }
}
