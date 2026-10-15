namespace ChatAgentBridge.Core;

/// <summary>Builds shell-free CLI invocations from provider and model settings.</summary>
public static class AgentProfile
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Custom = "custom";
    public const string DefaultCodexArguments = "exec --json -";

    public static string ResolveProvider(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = string.IsNullOrWhiteSpace(settings.AgentProvider) ? Codex : settings.AgentProvider.Trim().ToLowerInvariant();
        if (provider is not (Codex or Claude or Custom))
            throw new ArgumentException("Agent provider must be codex, claude, or custom.", nameof(settings));

        // Directly-created legacy settings often use an arbitrary executable with the old default provider.
        // Treat those as custom so their executable and arguments remain exactly as configured.
        if (provider == Codex && !IsCodexExecutable(settings.AgentExecutable)) return Custom;
        return provider;
    }

    public static AgentInvocation BuildInvocation(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = ResolveProvider(settings);
        var model = NormalizeModel(settings.AgentModel);
        if (provider == Custom && model is not null)
            throw new ArgumentException("A model cannot be applied to a custom agent profile; choose Codex or Claude Code.", nameof(settings));

        if (settings.AgentMode == "app-server")
        {
            if (provider != Codex)
                throw new ArgumentException("Codex App Server mode requires the Codex provider.", nameof(settings));
            return new AgentInvocation(settings.AgentExecutable, Array.Empty<string>(), model, provider);
        }

        if (provider == Claude)
        {
            string? sessionId = null;
            var configured = AgentArgumentParser.Parse(settings.AgentArguments);
            if (configured.Count == 2 && configured[0] == "--resume") sessionId = configured[1];
            return new AgentInvocation(EffectiveExecutable(settings, provider), BuildClaudeArguments(model, sessionId), model, provider);
        }

        var parsed = AgentArgumentParser.Parse(settings.AgentArguments);
        if (provider == Codex && model is not null)
            parsed = SetOption(parsed, "--model", "-m", model);
        return new AgentInvocation(EffectiveExecutable(settings, provider), parsed, model, provider);
    }

    public static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var normalized = model.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException("Model names must be at most 256 characters and contain no control characters.", nameof(model));
        return normalized;
    }

    public static string ResolveEffectiveExecutable(BridgeSettings settings)
        => EffectiveExecutable(settings, ResolveProvider(settings));

    internal static IReadOnlyList<string> BuildClaudeArguments(string? model, string? sessionId = null)
    {
        var arguments = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!Guid.TryParse(sessionId, out var id)) throw new ArgumentException("Claude session id must be a GUID.", nameof(sessionId));
            arguments.Add("--resume");
            arguments.Add(id.ToString("D"));
        }
        if (model is not null) { arguments.Add("--model"); arguments.Add(model); }
        return arguments;
    }

    private static string EffectiveExecutable(BridgeSettings settings, string provider)
    {
        var executable = settings.AgentExecutable.Trim();
        // Provider preset changes default executable while respecting explicit executable overrides.
        if (provider == Claude && IsCodexExecutable(executable)) return "claude";
        return executable;
    }

    private static IReadOnlyList<string> SetOption(IReadOnlyList<string> arguments, string option, string alias, string value)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == option || argument == alias)
            {
                if (index + 1 < arguments.Count) index++;
                continue;
            }
            if (argument.StartsWith(option + "=", StringComparison.Ordinal) || argument.StartsWith(alias + "=", StringComparison.Ordinal)) continue;
            result.Add(argument);
        }
        result.Add(option);
        result.Add(value);
        return result;
    }

    private static bool IsCodexExecutable(string executable)
    {
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        return new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }
            .Contains(leaf, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}

public sealed record AgentInvocation(string Executable, IReadOnlyList<string> Arguments, string? Model, string Provider);
