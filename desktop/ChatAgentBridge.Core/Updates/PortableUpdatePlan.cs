using System.Security.Cryptography;
using System.Text.Json;

namespace ChatAgentBridge.Core.Updates;

public sealed record PortableUpdatePlan(
    int SchemaVersion, string InstallDirectory, string DataDirectory, string StagingDirectory,
    string WorkDirectory, int ParentProcessId, long ParentStartTimeUtcTicks,
    Dictionary<string, string> Files, string ReleaseVersion, string Architecture, string Nonce, string Language = "en")
{
    public const string FileName = "update-plan.json";
    public string ReadyFile => Path.Combine(WorkDirectory, "helper-ready-" + Nonce);
    public string ResultFile => Path.Combine(WorkDirectory, "update-result.json");
    public string BackupDirectory => Path.Combine(WorkDirectory, "backup");
    public string HealthEventName => @"Local\ChatAgentBridge.Update." + Nonce;

    public static async Task<PortableUpdatePlan> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Update plan is too large.");
        var plan = JsonSerializer.Deserialize<PortableUpdatePlan>(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException("Update plan is empty.");
        plan.Validate();
        if (!Path.GetFullPath(path).Equals(Path.Combine(plan.WorkDirectory, FileName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected update plan location.");
        return plan;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || ParentProcessId <= 0 || ParentStartTimeUtcTicks <= 0 ||
            Nonce is null || Nonce.Length != 32 || !Nonce.All(Uri.IsHexDigit) ||
            Architecture is not ("x64" or "arm64") || Files is null || Files.Count is < 4 or > 10000)
            throw new InvalidDataException("Invalid update plan.");
        foreach (var directory in new[] { InstallDirectory, DataDirectory, StagingDirectory, WorkDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) != Path.TrimEndingDirectorySeparator(directory) ||
                Path.GetPathRoot(directory) == Path.TrimEndingDirectorySeparator(directory))
                throw new InvalidDataException("Update directories must be absolute non-root paths.");
            PortableUpdatePaths.EnsureNoReparsePoints(directory);
        }
        if (!PortableUpdatePaths.IsWithin(WorkDirectory, StagingDirectory) ||
            Path.TrimEndingDirectorySeparator(WorkDirectory).Equals(Path.TrimEndingDirectorySeparator(StagingDirectory), StringComparison.OrdinalIgnoreCase) ||
            PortableUpdatePaths.Overlaps(WorkDirectory, InstallDirectory) ||
            PortableUpdatePaths.Overlaps(WorkDirectory, DataDirectory) ||
            PortableUpdatePaths.IsWithin(DataDirectory, InstallDirectory))
            throw new InvalidDataException("Unsafe update directory relationship.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in Files)
        {
            if (relative != PortableArchiveStager.ValidateRelativePath(relative))
                throw new InvalidDataException("Update paths must use their canonical form.");
            if (!names.Add(relative) || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid update file checksum.");
            var target = PortableUpdatePaths.Resolve(InstallDirectory, relative);
            if (PortableUpdatePaths.Overlaps(target, DataDirectory))
                throw new InvalidDataException("Update payload overlaps the settings directory.");
        }
        if (!names.Contains("ChatAgentBridge.Desktop.exe") || !names.Contains("portable-release.json") ||
            !names.Contains("portable-files.json") || !names.Contains("ChatAgentBridge.Updater.exe"))
            throw new InvalidDataException("Update payload is incomplete.");
    }
}

public static class PortableUpdatePaths
{
    public static bool IsWithin(string root, string path) =>
        Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static bool Overlaps(string left, string right) => IsWithin(left, right) || IsWithin(right, left);

    public static string Resolve(string root, string relative)
    {
        PortableArchiveStager.ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, path)) throw new InvalidDataException("Update file escapes its directory.");
        EnsureNoReparsePoints(path);
        return path;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Updates cannot follow symbolic links or junctions.");
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

/// <summary>Shared app lifetime lease; the updater needs exclusive access before replacing any file.</summary>
public static class PortableApplicationLease
{
    public const string FileName = ".cab-instance.lock";
    public static FileStream Open(string installDirectory, bool exclusive = false)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(installDirectory);
        var path = Path.Combine(installDirectory, FileName);
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        if (!File.Exists(path))
        {
            using var create = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
    }
}
