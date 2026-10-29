using System.Text.Json;

namespace ChatAgentBridge.Core;

public enum PermissionSource { BridgeSetting, ProjectLocalSettings, ProjectSettings, UserSettings, CliDefault, Unknown }

/// <param name="ConfiguredMode">The bridge's normalized <c>AgentPermissionMode</c>.</param>
/// <param name="NativeMode">The provider-native mode that will apply (e.g. Claude <c>auto</c>, Codex <c>danger-full-access</c>), or null when unknown.</param>
/// <param name="Unattended">True when the agent can act without asking a human.</param>
public sealed record EffectivePermissionInfo(string ConfiguredMode, string? NativeMode, PermissionSource Source, bool Unattended);

/// <summary>
/// Resolves what permission behavior an agent run will really have. With <c>inherit</c> the bridge adds no flag,
/// so Claude Code / Codex fall back to their own settings files, which can silently be unattended.
/// </summary>
public static class EffectivePermission
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 };
    private const long MaxConfigBytes = 1024 * 1024;

    public static EffectivePermissionInfo Resolve(BridgeSettings settings, string? userHome = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var mode = ExecutionPolicy.NormalizePermissionMode(settings.AgentPermissionMode);
        string provider;
        try { provider = AgentProfile.ResolveProvider(settings); }
        catch (ArgumentException) { provider = AgentProfile.Custom; }
        var cabFull = mode == "full-access";
        if (mode != "inherit")
        {
            var native = provider switch
            {
                AgentProfile.Claude => mode switch { "read-only" => "plan", "workspace-write" => "acceptEdits", "full-access" => "bypassPermissions", _ => null },
                AgentProfile.Codex => mode switch { "read-only" => "read-only", "workspace-write" => "workspace-write", "full-access" => "danger-full-access", _ => null },
                _ => null
            };
            return new(mode, native, PermissionSource.BridgeSetting, cabFull);
        }

        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (provider == AgentProfile.Claude) return ResolveClaude(settings.WorkspaceRoot, home, env);
        if (provider == AgentProfile.Codex) return ResolveCodex(home, env);
        return new(mode, null, PermissionSource.Unknown, false);
    }

    private static EffectivePermissionInfo ResolveClaude(string workspace, string home, Func<string, string?> env)
    {
        var configDir = env("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom ? custom : string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude");
        var candidates = new List<(string Path, PermissionSource Source)>(3);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            candidates.Add((Path.Combine(workspace, ".claude", "settings.local.json"), PermissionSource.ProjectLocalSettings));
            candidates.Add((Path.Combine(workspace, ".claude", "settings.json"), PermissionSource.ProjectSettings));
        }
        if (configDir is not null) candidates.Add((Path.Combine(configDir, "settings.json"), PermissionSource.UserSettings));
        foreach (var (path, source) in candidates)
        {
            var value = ReadClaudeDefaultMode(path);
            if (value is not null) return new("inherit", value, source, IsClaudeUnattended(value));
        }
        return new("inherit", "default", PermissionSource.CliDefault, false);
    }

    private static bool IsClaudeUnattended(string value) => value is "bypassPermissions" or "auto" or "dontAsk";

    private static string? ReadClaudeDefaultMode(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes) return null;
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, Lenient);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object &&
                permissions.TryGetProperty("defaultMode", out var mode) && mode.ValueKind == JsonValueKind.String &&
                mode.GetString()?.Trim() is { Length: > 0 and <= 64 } text)
                return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { }
        return null;
    }

    private static EffectivePermissionInfo ResolveCodex(string home, Func<string, string?> env)
    {
        var codexHome = env("CODEX_HOME") is { Length: > 0 } custom ? custom : string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".codex");
        var sandbox = codexHome is null ? null : ReadCodexSandboxMode(Path.Combine(codexHome, "config.toml"));
        return sandbox is null
            ? new("inherit", null, PermissionSource.Unknown, false)
            : new("inherit", sandbox, PermissionSource.UserSettings, sandbox == "danger-full-access");
    }

    /// <summary>Reads a top-level <c>sandbox_mode = "..."</c>; keys after the first [table] header belong to a table and are ignored.</summary>
    private static string? ReadCodexSandboxMode(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes) return null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[') return null;
                var equals = line.IndexOf('=');
                if (equals <= 0 || line[..equals].Trim() != "sandbox_mode") continue;
                var value = line[(equals + 1)..].Trim();
                if (value.Length < 2 || value[0] is not ('"' or '\'')) return null;
                var end = value.IndexOf(value[0], 1);
                return end > 1 && value[1..end].Trim() is { Length: <= 64 } mode ? mode : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
}
