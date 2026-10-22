using System.Security.Cryptography;
using System.Text.Json;

namespace ChatAgentBridge.Core.Updates;

public sealed record RestoredPortableUpdate(string WorkDirectory, PortableRelease Release, StagedPortableRelease Staged);

/// <summary>Persists a downloaded portable update without treating its state file as trusted input.</summary>
public sealed class PortableUpdateStageStore
{
    public const string StateFileName = "portable-update-stage.json";
    public const string ArchiveFileName = "update.zip";
    private const int MaxStateBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string GetWorkRoot(string installDirectory, string dataDirectory)
    {
        var identity = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + "\n" + Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatAgentBridge", "Updates", key);
    }

    public static async Task SaveAsync(string workDirectory, string installDirectory, string dataDirectory,
        string architecture, PortableRelease release, StagedPortableRelease staged, CancellationToken cancellationToken = default)
    {
        var root = GetWorkRoot(installDirectory, dataDirectory);
        ValidateOwnedWorkDirectory(root, workDirectory);
        PortableUpdatePaths.EnsureNoReparsePoints(workDirectory);
        var archivePath = Path.Combine(workDirectory, ArchiveFileName);
        if (!File.Exists(archivePath) || !string.Equals(await PortableUpdatePaths.HashAsync(archivePath, cancellationToken), release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded update archive checksum does not match.");
        var record = new StageRecord(1, Path.GetFullPath(installDirectory), Path.GetFullPath(dataDirectory), Path.GetFullPath(workDirectory),
            architecture, release, release.Sha256, staged.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        var statePath = Path.Combine(workDirectory, StateFileName);
        PortableUpdatePaths.EnsureNoReparsePoints(statePath);
        var temporary = Path.Combine(workDirectory, ".stage-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, statePath, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    public static async Task<RestoredPortableUpdate?> LoadAsync(string workDirectory, string installDirectory, string dataDirectory,
        string currentVersion, string architecture, bool includePrereleases, PortableRelease? expectedRelease,
        CancellationToken cancellationToken = default)
    {
        var root = GetWorkRoot(installDirectory, dataDirectory);
        ValidateOwnedWorkDirectory(root, workDirectory);
        PortableUpdatePaths.EnsureNoReparsePoints(workDirectory);
        var statePath = Path.Combine(workDirectory, StateFileName);
        PortableUpdatePaths.EnsureNoReparsePoints(statePath);
        var info = new FileInfo(statePath);
        if (!info.Exists || info.Length > MaxStateBytes) throw new InvalidDataException("Portable update state is missing or too large.");
        var record = JsonSerializer.Deserialize<StageRecord>(await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Portable update state is empty.");
        if (record.SchemaVersion != 1 || string.IsNullOrWhiteSpace(record.InstallDirectory) || string.IsNullOrWhiteSpace(record.DataDirectory) ||
            string.IsNullOrWhiteSpace(record.WorkDirectory) || string.IsNullOrWhiteSpace(record.Architecture) || record.Release is null ||
            record.Files is null || record.Files.Count is < 4 or > 10000 ||
            !SamePath(record.InstallDirectory, installDirectory) || !SamePath(record.DataDirectory, dataDirectory) ||
            !SamePath(record.WorkDirectory, workDirectory) || record.Architecture != architecture ||
            record.ArchiveSha256 is null || record.ArchiveSha256.Length != 64 || !record.ArchiveSha256.All(Uri.IsHexDigit) ||
            !string.Equals(record.ArchiveSha256, record.Release.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !PortableReleaseClient.IsValidCachedCandidate(record.Release, currentVersion, architecture, includePrereleases) ||
            !SameRelease(record.Release, expectedRelease))
            throw new InvalidDataException("Portable update state does not match the current release candidate.");

        var archivePath = Path.Combine(workDirectory, ArchiveFileName);
        PortableUpdatePaths.EnsureNoReparsePoints(archivePath);
        if (!File.Exists(archivePath) || !string.Equals(await PortableUpdatePaths.HashAsync(archivePath, cancellationToken).ConfigureAwait(false), record.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Portable update archive failed verification.");

        var stageDirectory = Path.Combine(workDirectory, "stage");
        PortableUpdatePaths.EnsureNoReparsePoints(stageDirectory);
        var stagedFiles = ValidateFileMap(record.Files);
        foreach (var (relative, hash) in stagedFiles)
        {
            var file = PortableUpdatePaths.Resolve(stageDirectory, relative);
            if (!File.Exists(file) || !string.Equals(await PortableUpdatePaths.HashAsync(file, cancellationToken).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A staged update file failed verification.");
        }

        // Re-run ZIP path, metadata, architecture, and embedded checksum validation against a disposable child.
        var validationDirectory = Path.Combine(workDirectory, ".validate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var verified = await PortableArchiveStager.ExtractAsync(archivePath, validationDirectory, record.Release, cancellationToken).ConfigureAwait(false);
            if (!SameFiles(stagedFiles, verified.Files)) throw new InvalidDataException("Staged files do not match the validated release archive.");
        }
        finally { DeleteOwnedChild(workDirectory, validationDirectory); }
        return new RestoredPortableUpdate(workDirectory, record.Release, new StagedPortableRelease(stageDirectory, stagedFiles));
    }

    public static void DeleteOwnedWorkDirectory(string installDirectory, string dataDirectory, string workDirectory)
    {
        var root = GetWorkRoot(installDirectory, dataDirectory);
        ValidateOwnedWorkDirectory(root, workDirectory);
        DeleteOwnedChild(root, workDirectory);
    }

    public static bool IsCompleted(string workDirectory)
    {
        var result = Path.Combine(workDirectory, "update-result.json");
        PortableUpdatePaths.EnsureNoReparsePoints(result);
        if (!File.Exists(result) || new FileInfo(result).Length > 64 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            return (document.RootElement.TryGetProperty("Success", out var success) ||
                    document.RootElement.TryGetProperty("success", out success)) && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static Dictionary<string, string> ValidateFileMap(Dictionary<string, string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in files)
        {
            if (path != PortableArchiveStager.ValidateRelativePath(path) || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !result.TryAdd(path, hash))
                throw new InvalidDataException("Portable update file manifest is invalid.");
        }
        return result;
    }

    private static bool SameFiles(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var hash) && string.Equals(pair.Value, hash, StringComparison.OrdinalIgnoreCase));

    private static bool SameRelease(PortableRelease left, PortableRelease? right) => right is not null &&
        left.Version == right.Version && left.Tag == right.Tag && left.AssetName == right.AssetName && left.DownloadUri == right.DownloadUri &&
        left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase) && left.Size == right.Size && left.ReleasePage == right.ReleasePage;

    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void ValidateOwnedWorkDirectory(string root, string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) || !Path.IsPathFullyQualified(workDirectory)) throw new InvalidDataException("Portable update work path is invalid.");
        var fullRoot = Path.GetFullPath(root);
        var fullWork = Path.GetFullPath(workDirectory);
        if (!PortableUpdatePaths.IsWithin(fullRoot, fullWork) || SamePath(fullRoot, fullWork) ||
            !SamePath(Path.GetDirectoryName(fullWork)!, fullRoot) ||
            !Guid.TryParseExact(Path.GetFileName(fullWork), "N", out _))
            throw new InvalidDataException("Portable update work path is outside its owned work root.");
        PortableUpdatePaths.EnsureNoReparsePoints(fullRoot);
        PortableUpdatePaths.EnsureNoReparsePoints(fullWork);
    }

    private static void DeleteOwnedChild(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent);
        var fullChild = Path.GetFullPath(child);
        if (!PortableUpdatePaths.IsWithin(fullParent, fullChild) || SamePath(fullParent, fullChild) ||
            !SamePath(Path.GetDirectoryName(fullChild)!, fullParent)) throw new InvalidDataException("Refusing to remove a path outside the update work root.");
        PortableUpdatePaths.EnsureNoReparsePoints(fullParent);
        PortableUpdatePaths.EnsureNoReparsePoints(fullChild);
        if (Directory.Exists(fullChild)) Directory.Delete(fullChild, recursive: true);
        else if (File.Exists(fullChild)) File.Delete(fullChild);
    }

    private sealed record StageRecord(int SchemaVersion, string InstallDirectory, string DataDirectory, string WorkDirectory,
        string Architecture, PortableRelease Release, string ArchiveSha256, Dictionary<string, string> Files);
}
