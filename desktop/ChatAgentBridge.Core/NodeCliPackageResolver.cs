using System.Text.Json;

namespace ChatAgentBridge.Core;

/// <summary>Resolves known npm and Qwen standalone CLI shims without executing shim text.</summary>
public static class NodeCliPackageResolver
{
    public sealed record Resolution(string Executable, string EntryPoint, string PackageName);

    private static readonly CliDiscoveryCache<Resolution> Cache = new();

    /// <summary>Drops cached discovery results, for example after the process environment was reloaded.</summary>
    public static void ClearCache() => Cache.Clear();

    public static Resolution? Resolve(string configuredExecutable, string provider)
    {
        if (!OperatingSystem.IsWindows() || !AgentProfile.UsesNpmShim(provider)) return null;
        var environment = string.Join('\n', provider, Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("PATH") ?? "");
        // Failures throw and are never cached; a null result (not a shim) is cheap to recompute.
        return Cache.GetOrResolve(configuredExecutable, environment,
            resolution => File.Exists(resolution.Executable) && File.Exists(resolution.EntryPoint),
            () => ResolveUncached(configuredExecutable, provider));
    }

    private static Resolution? ResolveUncached(string configuredExecutable, string provider)
    {
        var explicitPath = configuredExecutable.Trim().Trim('"');
        if (provider == AgentProfile.Qwen && Path.IsPathFullyQualified(explicitPath) &&
            Path.GetFileName(explicitPath).Equals("node.exe", StringComparison.OrdinalIgnoreCase))
        {
            var explicitNode = Path.GetFullPath(explicitPath);
            var nodeDirectory = Path.GetDirectoryName(explicitNode)!;
            if (!Path.GetFileName(nodeDirectory).Equals("node", StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetDirectoryName(nodeDirectory);
            if (root is null) return null;
            var standalone = ResolveQwenStandalone(Path.Combine(root, "bin", "qwen.cmd"));
            return standalone is not null && standalone.Executable.Equals(explicitNode, StringComparison.OrdinalIgnoreCase)
                ? standalone : null;
        }
        var shim = FindShim(configuredExecutable, provider);
        if (shim is null) return null;

        var (packageName, command) = provider == AgentProfile.Gemini
            ? ("@google/gemini-cli", "gemini")
            : provider == AgentProfile.Qwen ? ("@qwen-code/qwen-code", "qwen") : ("@github/copilot", "copilot");
        var binDirectory = Path.GetDirectoryName(shim)!;
        var packageDirectory = packageName.Split('/').Aggregate(
            Path.Combine(binDirectory, "node_modules"), Path.Combine);
        var packageJsonPath = Path.Combine(packageDirectory, "package.json");
        // Only try the official standalone layout when no npm package occupies this location.
        if (provider == AgentProfile.Qwen && !Directory.Exists(packageDirectory))
        {
            var standalone = ResolveQwenStandalone(shim);
            if (standalone is not null) return standalone;
        }
        if (!File.Exists(packageJsonPath)) throw MissingPackage(shim, provider, packageName);

        string entryPoint;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() != packageName)
                throw MissingPackage(shim, provider, packageName);
            if (!root.TryGetProperty("bin", out var bin)) throw MissingPackage(shim, provider, packageName);
            string? relativeEntry;
            if (bin.ValueKind == JsonValueKind.String)
                relativeEntry = bin.GetString();
            else if (bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty(command, out var commandEntry))
                relativeEntry = commandEntry.GetString();
            else relativeEntry = null;
            if (string.IsNullOrWhiteSpace(relativeEntry)) throw MissingPackage(shim, provider, packageName);
            entryPoint = Path.GetFullPath(Path.Combine(packageDirectory, relativeEntry));
            var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory)) + Path.DirectorySeparatorChar;
            if (!entryPoint.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPoint) ||
                !new[] { ".js", ".mjs", ".cjs" }.Contains(Path.GetExtension(entryPoint), StringComparer.OrdinalIgnoreCase))
                throw MissingPackage(shim, provider, packageName);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not read the installed {packageName} package metadata beside '{shim}'. Set the executable to node.exe and its arguments to the package JavaScript entrypoint.", ex);
        }

        var node = FindNode(binDirectory);
        if (node is null)
            throw new InvalidOperationException($"'{shim}' is a Windows npm command shim for {packageName}, and node.exe was not found. Install Node.js or set the executable to its full path.");
        return new Resolution(node, entryPoint, packageName);
    }

    private static Resolution? ResolveQwenStandalone(string shim)
    {
        var bin = Path.GetDirectoryName(shim)!;
        if (!Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;
        var parent = Path.GetDirectoryName(bin)!;
        // Installer wrapper: <base>/bin/qwen.cmd. Archive command: <base>/qwen-code/bin/qwen.cmd.
        var root = File.Exists(Path.Combine(parent, "manifest.json"))
            ? parent : Path.Combine(parent, "qwen-code");
        var files = new[] { shim, Path.Combine(root, "manifest.json"), Path.Combine(root, "package.json"),
            Path.Combine(root, "lib", "package.json"), Path.Combine(root, "lib", "cli-entry.js"),
            Path.Combine(root, "node", "node.exe"), Path.Combine(root, "bin", "qwen.cmd") };
        if (files.Any(path => !File.Exists(path))) return null;
        if (HasPackageReparsePoint(files[0], Path.GetDirectoryName(files[0])!) ||
            files.Skip(1).Any(path => HasPackageReparsePoint(path, root))) return null;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(files[1]));
            using var package = JsonDocument.Parse(File.ReadAllText(files[2]));
            using var library = JsonDocument.Parse(File.ReadAllText(files[3]));
            static bool HasString(JsonElement element, string key, string value) =>
                element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == value;
            if (!HasString(manifest.RootElement, "name", "@qwen-code/qwen-code") ||
                !HasString(manifest.RootElement, "runtime", "node") ||
                !(HasString(manifest.RootElement, "target", "win-x64") || HasString(manifest.RootElement, "target", "win-arm64")) ||
                !HasString(package.RootElement, "name", "@qwen-code/qwen-code") ||
                !HasString(library.RootElement, "name", "@qwen-code/qwen-code") ||
                !library.RootElement.TryGetProperty("bin", out var commands) || commands.ValueKind != JsonValueKind.Object ||
                !HasString(commands, "qwen", "cli-entry.js")) return null;
            return new Resolution(files[5], files[4], "@qwen-code/qwen-code");
        }
        catch (JsonException) { return null; }
    }

    // Parent directories may be junctions (for example an install beneath a redirected
    // user profile). Links inside the standalone package are rejected so package files
    // cannot escape the validated install tree.
    private static bool HasPackageReparsePoint(string path, string packageRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        for (var current = fullPath; current is not null && !current.Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static string? FindShim(string executable, string provider)
    {
        var command = provider switch { AgentProfile.Gemini => "gemini", AgentProfile.Qwen => "qwen", _ => "copilot" };
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        var extension = Path.GetExtension(leaf);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.GetFileNameWithoutExtension(leaf).Equals(command, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(executable);
        }
        if (extension.Length != 0) return null;
        if (!leaf.Equals(command, StringComparison.OrdinalIgnoreCase)) return null;
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidate = Path.GetFullPath(executable) + ".cmd";
            return File.Exists(candidate) ? candidate : null;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (File.Exists(Path.Combine(trimmed, command + ".exe"))) return null;
            var candidate = Path.Combine(trimmed, command + ".cmd");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string? FindNode(string shimDirectory)
    {
        var sibling = Path.Combine(shimDirectory, "node.exe");
        if (File.Exists(sibling)) return Path.GetFullPath(sibling);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "node.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static InvalidOperationException MissingPackage(string shim, string provider, string packageName)
    {
        var label = provider switch { AgentProfile.Gemini => "Gemini CLI", AgentProfile.Qwen => "Qwen Code", _ => "GitHub Copilot CLI" };
        return new InvalidOperationException($"'{shim}' is a Windows npm command shim, but the expected {label} package '{packageName}' and JavaScript entrypoint could not be resolved beside it. Install the official npm package globally or set the executable to node.exe and arguments to the installed CLI entrypoint. The bridge will not launch .cmd files.");
    }
}
