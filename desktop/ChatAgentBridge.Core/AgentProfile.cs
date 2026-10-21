namespace ChatAgentBridge.Core;

/// <summary>Builds shell-free CLI invocations from provider and model settings.</summary>
public static class AgentProfile
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Gemini = "gemini";
    public const string GitHubCopilot = "github-copilot";
    public const string Custom = "custom";
    public const string DefaultCodexArguments = "exec --json -";
    public const string DefaultGeminiArguments = "--output-format stream-json";
    public const string DefaultGitHubCopilotArguments = "--output-format json --no-ask-user";

    public static string ResolveProvider(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = string.IsNullOrWhiteSpace(settings.AgentProvider) ? Codex : settings.AgentProvider.Trim().ToLowerInvariant();
        if (provider is not (Codex or Claude or Gemini or GitHubCopilot or Custom))
            throw new ArgumentException("Agent provider must be codex, claude, gemini, github-copilot, or custom.", nameof(settings));

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
        var effort = NormalizeReasoningEffort(settings.AgentReasoningEffort);
        var permissionMode = ExecutionPolicy.NormalizePermissionMode(settings.AgentPermissionMode);
        if (IsBasicCliProvider(provider) && permissionMode != "inherit")
            throw new ArgumentException("Gemini CLI and GitHub Copilot CLI use their own permission settings; select inherit.", nameof(settings));
        if (provider == Custom && permissionMode != "inherit")
            throw new ArgumentException("Execution permission modes are supported only for Codex and Claude Code profiles.", nameof(settings));
        if (permissionMode == "ask" && provider == Codex && settings.AgentMode != "app-server")
            throw new ArgumentException("The ask permission mode requires Codex App Server mode, which can display approval requests. Select App Server mode or choose another permission mode.", nameof(settings));
        if (provider == Custom && model is not null)
            throw new ArgumentException("A model override is unavailable for a custom agent profile.", nameof(settings));
        if (provider is Custom or Gemini or GitHubCopilot && effort is not null)
            throw new ArgumentException("A reasoning effort override is unavailable for this agent profile; choose Codex or Claude Code.", nameof(settings));

        if (settings.AgentMode == "app-server")
        {
            if (provider != Codex)
                throw new ArgumentException("Codex App Server mode requires the Codex provider.", nameof(settings));
            return new AgentInvocation(settings.AgentExecutable, Array.Empty<string>(), model, provider, effort);
        }

        if (provider == Claude)
        {
            string? sessionId = null;
            var configured = AgentArgumentParser.Parse(settings.AgentArguments);
            if (configured.Count == 2 && configured[0] == "--resume") sessionId = configured[1];
            return new AgentInvocation(EffectiveExecutable(settings, provider), BuildClaudeArguments(model, sessionId, effort, permissionMode), model, provider, effort);
        }

        if (IsBasicCliProvider(provider))
        {
            var basicArguments = GetStructuredCliArguments(settings, provider);
            if (settings.AuthorizedCliSessionId is { } sessionId)
            {
                if (!Guid.TryParseExact(sessionId, "D", out var id))
                    throw new ArgumentException("The authorized CLI session id must be a UUID.", nameof(settings));
                basicArguments = basicArguments.Concat(new[] { "--resume", id.ToString("D") }).ToArray();
            }
            return new AgentInvocation(EffectiveExecutable(settings, provider), basicArguments, model, provider);
        }

        var parsed = AgentArgumentParser.Parse(settings.AgentArguments);
        if (provider == Codex && model is not null)
            parsed = SetOption(parsed, "--model", "-m", model);
        if (provider == Codex && effort is not null)
            parsed = SetConfig(parsed, "model_reasoning_effort", effort);
        if (provider == Codex && permissionMode != "inherit")
        {
            parsed = RemoveCodexPermissionOverrides(parsed);
            var sandbox = permissionMode switch
            {
                "read-only" => "read-only",
                "workspace-write" => "workspace-write",
                "full-access" => "danger-full-access",
                _ => throw new ArgumentException($"Permission mode '{permissionMode}' is not supported for Codex exec mode.", nameof(settings))
            };
            // These are global config overrides supported by both `codex exec` and `codex exec resume`.
            // The exec-only --sandbox flag is absent from the resume subcommand, and approval flags
            // are not accepted by the installed CLI's exec command.
            parsed = SetConfigValue(parsed, "sandbox_mode", $"\"{sandbox}\"");
            parsed = SetConfigValue(parsed, "approval_policy", "\"never\"");
            if (permissionMode == "workspace-write")
                parsed = SetConfigValue(parsed, "sandbox_workspace_write.network_access", settings.AllowAgentNetwork ? "true" : "false");
        }
        return new AgentInvocation(EffectiveExecutable(settings, provider), parsed, model, provider, effort);
    }

    public static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var normalized = model.Trim();
        if (normalized.Length > 256 || normalized.StartsWith('-') || normalized.Any(char.IsControl))
            throw new ArgumentException("Model names must be at most 256 characters, must not start with a dash, and must contain no control characters.", nameof(model));
        return normalized;
    }

    public static string? NormalizeReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        var normalized = effort.Trim().ToLowerInvariant();
        if (normalized.Length > 32 || normalized[0] is < 'a' or > 'z' || normalized.Any(character =>
                !((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character is '_' or '-')))
            throw new ArgumentException("Reasoning effort must be a token of at most 32 lowercase ASCII letters, digits, underscores, or hyphens.", nameof(effort));
        return normalized;
    }

    public static string ResolveEffectiveExecutable(BridgeSettings settings)
        => EffectiveExecutable(settings, ResolveProvider(settings));

    public static bool IsBasicCliProvider(string provider) => provider is Gemini or GitHubCopilot;

    /// <summary>Canonical provider arguments before the session runner adds its authorized resume id.</summary>
    internal static IReadOnlyList<string> GetStructuredCliArguments(BridgeSettings settings, string provider)
    {
        var arguments = GetConfiguredCliArguments(settings.AgentArguments, provider);
        foreach (var argument in arguments)
        {
            var option = argument.Split('=', 2)[0];
            // CLI parsers can expand short-option bundles, e.g. -spTEXT into -s -p TEXT.
            // Require separate short options and values so a reserved selector cannot hide in a bundle.
            if (argument.Length > 2 && argument[0] == '-' && argument[1] != '-')
                throw new ArgumentException("CLI short options must be separate arguments; do not bundle flags or attach their values.", nameof(settings));
            if (option is "--" or "--resume" or "-r" or "--continue" or "--session-id" or "--new-session" or "--new-session-id" or "--session" or "--connect" or "--worktree" or
                "-C" or "--cwd" or "--directory" or "--working-directory" or "--prompt" or "-p" or "--interactive" or "-i" ||
                argument.StartsWith("-C", StringComparison.Ordinal) ||
                argument.StartsWith("-r", StringComparison.Ordinal) && !argument.StartsWith("--", StringComparison.Ordinal) ||
                argument.StartsWith("-p", StringComparison.Ordinal) && !argument.StartsWith("--", StringComparison.Ordinal) ||
                argument.StartsWith("-i", StringComparison.Ordinal) && !argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("CLI session and prompt selection must be managed by the bridge; remove configured resume, continue, session, and prompt options.", nameof(settings));
        }
        if (provider == GitHubCopilot)
            arguments = EnsureFlag(arguments.Where(value => value is not ("--silent" or "-s") &&
                !value.StartsWith("--silent=", StringComparison.Ordinal)).ToArray(), "--no-ask-user");
        arguments = SetOption(arguments, "--output-format", provider == Gemini ? "-o" : "--output-format", provider == Gemini ? "stream-json" : "json");
        if (NormalizeModel(settings.AgentModel) is { } model)
            arguments = SetOption(arguments, "--model", "-m", model);
        return arguments;
    }

    internal static string? FindWindowsNpmShim(string executable, string provider)
    {
        if (!OperatingSystem.IsWindows() || !IsBasicCliProvider(provider)) return null;
        var command = provider == Gemini ? "gemini" : "copilot";
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        if (Path.GetFileNameWithoutExtension(leaf).Equals(command, StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(leaf) is ".cmd" or ".bat" or ".ps1")
            return Path.IsPathFullyQualified(executable) ? executable : Path.GetFullPath(executable);
        if (Path.HasExtension(leaf) || !leaf.Equals(command, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (File.Exists(Path.Combine(trimmed, command + ".exe"))) return null;
            var candidate = Path.Combine(trimmed, command + ".cmd");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    internal static string WindowsNpmShimGuidance(string shim, string provider)
    {
        var cliName = provider == Gemini ? "Gemini CLI" : "GitHub Copilot CLI";
        var entryPointHint = provider == Gemini
            ? "For a global npm install, its entrypoint is commonly node_modules/@google/gemini-cli/dist/index.js."
            : "Use the JavaScript entrypoint inside node_modules/@github/copilot for your installed version.";
        return $"'{shim}' is a Windows npm command shim. The bridge does not launch command scripts. Set the executable to node.exe and the arguments to the installed {cliName} JavaScript entrypoint. {entryPointHint}";
    }

    internal static bool IsNodeExecutable(string executable)
        => Path.GetFileNameWithoutExtension(executable.Trim().Trim('"')).Equals("node", StringComparison.OrdinalIgnoreCase);

    /// <summary>Arguments used for an identity-only probe of an explicit Node.js CLI configuration.</summary>
    internal static IReadOnlyList<string> GetNodeProbePrefix(BridgeSettings settings, string provider)
    {
        if (settings.AgentArguments == DefaultCodexArguments || string.IsNullOrWhiteSpace(settings.AgentArguments)) return Array.Empty<string>();
        var configured = AgentArgumentParser.Parse(settings.AgentArguments);
        // Keep only a script and a small set of non-executing Node options. Provider task flags
        // after the script are excluded, and code-loading/evaluation options are never probed.
        var safeNodeOptions = new HashSet<string>(StringComparer.Ordinal)
        { "--no-warnings", "--no-deprecation", "--trace-warnings", "--preserve-symlinks", "--enable-source-maps" };
        var scriptIndex = -1;
        for (var index = 0; index < configured.Count; index++)
        {
            if (new[] { ".js", ".mjs", ".cjs" }.Contains(Path.GetExtension(configured[index]), StringComparer.OrdinalIgnoreCase))
            { scriptIndex = index; break; }
            if (!safeNodeOptions.Contains(configured[index]))
                throw new InvalidOperationException("A Node CLI version check requires a JavaScript entrypoint in AgentArguments. Only --no-warnings, --no-deprecation, --trace-warnings, --preserve-symlinks, and --enable-source-maps may precede the entrypoint.");
        }
        if (scriptIndex >= 0) return configured.Take(scriptIndex + 1).ToArray();
        if (configured.Count == 0) return Array.Empty<string>();
        throw new InvalidOperationException("A Node CLI version check requires a JavaScript entrypoint in AgentArguments; arbitrary Node code or task flags are not run during a version check.");
    }

    internal static IReadOnlyList<string> BuildClaudeArguments(string? model, string? sessionId = null, string? effort = null, string permissionMode = "inherit")
    {
        var arguments = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!Guid.TryParse(sessionId, out var id)) throw new ArgumentException("Claude session id must be a GUID.", nameof(sessionId));
            arguments.Add("--resume");
            arguments.Add(id.ToString("D"));
        }
        if (model is not null) { arguments.Add("--model"); arguments.Add(model); }
        if (effort is not null) { arguments.Add("--effort"); arguments.Add(effort); }
        if (permissionMode != "inherit")
        {
            var claudeMode = permissionMode switch
            {
                "read-only" => "plan",
                "workspace-write" => "acceptEdits",
                "full-access" => "bypassPermissions",
                _ => throw new ArgumentException("Claude Code cannot use the ask permission mode in this bridge because its print-mode prompts require host control RPC. Choose read-only, workspace-write, or full-access.", nameof(permissionMode))
            };
            arguments.Add("--permission-mode");
            arguments.Add(claudeMode);
            if (permissionMode == "full-access") arguments.Add("--dangerously-skip-permissions");
        }
        return arguments;
    }

    private static IReadOnlyList<string> RemoveCodexPermissionOverrides(IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--sandbox" or "-s" or "--ask-for-approval" or "-a")
            { if (index + 1 < arguments.Count) index++; continue; }
            if (argument is "--full-auto" or "--yolo" or "--dangerously-bypass-approvals-and-sandbox") continue;
            if (argument.StartsWith("--sandbox=", StringComparison.Ordinal) || argument.StartsWith("-s=", StringComparison.Ordinal) ||
                argument.StartsWith("--ask-for-approval=", StringComparison.Ordinal) || argument.StartsWith("-a=", StringComparison.Ordinal)) continue;
            if (argument == "-c" || argument == "--config")
            {
                if (index + 1 < arguments.Count)
                {
                    var config = arguments[++index];
                    if (!IsPermissionConfig(config)) { result.Add(argument); result.Add(config); }
                }
                continue;
            }
            if (argument.StartsWith("--config=", StringComparison.Ordinal) || argument.StartsWith("-c", StringComparison.Ordinal) && argument.Length > 2)
            {
                var config = argument.StartsWith("--config=", StringComparison.Ordinal) ? argument[9..] : argument[2..];
                if (!IsPermissionConfig(config)) result.Add(argument);
                continue;
            }
            result.Add(argument);
        }
        return result;
    }

    private static bool IsPermissionConfig(string config)
    {
        var separator = config.IndexOf('=');
        var key = (separator < 0 ? config : config[..separator]).Trim();
        return key is "sandbox_mode" or "approval_policy" or "sandbox_workspace_write.network_access";
    }

    private static string EffectiveExecutable(BridgeSettings settings, string provider)
    {
        var executable = settings.AgentExecutable.Trim();
        // Provider preset changes default executable while respecting explicit executable overrides.
        if (provider == Claude && IsCodexExecutable(executable)) return "claude";
        if (provider == Gemini && IsCodexExecutable(executable)) return "gemini";
        if (provider == GitHubCopilot && IsCodexExecutable(executable)) return "copilot";
        return executable;
    }

    private static IReadOnlyList<string> GetConfiguredCliArguments(string? arguments, string provider)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments == DefaultCodexArguments)
            return AgentArgumentParser.Parse(provider == Gemini ? DefaultGeminiArguments : DefaultGitHubCopilotArguments);
        return AgentArgumentParser.Parse(arguments);
    }

    private static IReadOnlyList<string> EnsureFlag(IReadOnlyList<string> arguments, string flag)
        => arguments.Contains(flag, StringComparer.Ordinal) ? arguments : arguments.Append(flag).ToArray();

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

    private static IReadOnlyList<string> SetConfig(IReadOnlyList<string> arguments, string key, string value)
        => SetConfigValue(arguments, key, $"\"{value}\"");

    private static IReadOnlyList<string> SetConfigValue(IReadOnlyList<string> arguments, string key, string tomlValue)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "-c" || argument == "--config")
            {
                if (index + 1 < arguments.Count)
                {
                    var config = arguments[++index];
                    if (!IsTargetConfig(config, key))
                    {
                        result.Add(argument);
                        result.Add(config);
                    }
                }
                continue;
            }
            if ((argument.StartsWith("-c", StringComparison.Ordinal) && !argument.StartsWith("--", StringComparison.Ordinal) && argument.Length > 2) ||
                (argument.StartsWith("--config=", StringComparison.Ordinal)))
            {
                var config = argument.StartsWith("--config=", StringComparison.Ordinal) ? argument[9..] : argument[2..];
                if (!IsTargetConfig(config, key)) result.Add(argument);
                continue;
            }
            result.Add(argument);
        }
        result.Add("-c");
        result.Add($"{key}={tomlValue}");
        return result;
    }

    private static bool IsTargetConfig(string value, string key)
    {
        var separator = value.IndexOf('=');
        return separator >= 0 && string.Equals(value[..separator].Trim(), key, StringComparison.Ordinal);
    }

    private static bool IsCodexExecutable(string executable)
    {
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        return new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }
            .Contains(leaf, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}

public sealed record AgentInvocation(string Executable, IReadOnlyList<string> Arguments, string? Model, string Provider, string? ReasoningEffort = null);
