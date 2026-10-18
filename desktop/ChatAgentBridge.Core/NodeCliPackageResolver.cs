using System.Text.Json;

namespace ChatAgentBridge.Core;

/// <summary>Resolves known npm CLI command shims to their Node entrypoints without executing shim text.</summary>
public static class NodeCliPackageResolver
{
    public sealed record Resolution(string Executable, string EntryPoint, string PackageName);

    public static Resolution? Resolve(string configuredExecutable, string provider)
    {
        if (!OperatingSystem.IsWindows() || !AgentProfile.IsBasicCliProvider(provider)) return null;
        var shim = FindShim(configuredExecutable, provider);
        if (shim is null) return null;

        var (packageName, command) = provider == AgentProfile.Gemini
            ? ("@google/gemini-cli", "gemini")
            : ("@github/copilot", "copilot");
        var binDirectory = Path.GetDirectoryName(shim)!;
        var packageDirectory = packageName.Split('/').Aggregate(
            Path.Combine(binDirectory, "node_modules"), Path.Combine);
        var packageJsonPath = Path.Combine(packageDirectory, "package.json");
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

    private static string? FindShim(string executable, string provider)
    {
        var command = provider == AgentProfile.Gemini ? "gemini" : "copilot";
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
        var label = provider == AgentProfile.Gemini ? "Gemini CLI" : "GitHub Copilot CLI";
        return new InvalidOperationException($"'{shim}' is a Windows npm command shim, but the expected {label} package '{packageName}' and JavaScript entrypoint could not be resolved beside it. Install the official npm package globally or set the executable to node.exe and arguments to the installed CLI entrypoint. The bridge will not launch .cmd files.");
    }
}
