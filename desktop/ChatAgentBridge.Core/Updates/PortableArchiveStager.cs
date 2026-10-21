using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;

namespace ChatAgentBridge.Core.Updates;

public sealed record StagedPortableRelease(string DirectoryPath, IReadOnlyDictionary<string, string> Files);

/// <summary>Validates a portable ZIP completely before extracting it to a new directory.</summary>
public static class PortableArchiveStager
{
    private const int MaxEntries = 10_000;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "private-data", "privatedata", "settings", "background", "history", "sessions", "audit", "credentials", ".credentials", "token", "tokens", "secrets", "chatagentbridge.testagent", "appx", "msix"
    };
    private static readonly HashSet<string> RequiredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChatAgentBridge.Desktop.exe", "ChatAgentBridge.Desktop.dll", "ChatAgentBridge.Desktop.runtimeconfig.json", "ChatAgentBridge.Updater.exe", "portable-release.json", "portable-files.json", "SHA256SUMS.txt"
    };
    private static readonly HashSet<string> IdentityFiles = new(StringComparer.Ordinal)
    {
        "Identity/" + PortableIdentityBundle.MetadataFileName,
        "Identity/" + PortableIdentityBundle.PackageFileName,
        "Identity/" + PortableIdentityBundle.CertificateFileName
    };

    public static async Task<StagedPortableRelease> ExtractAsync(string zipPath, string destinationDir, PortableRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var archivePath = Path.GetFullPath(zipPath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Portable archive was not found.", archivePath);
        var destination = Path.GetFullPath(destinationDir);
        PortableUpdatePaths.EnsureNoReparsePoints(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Staging destination must not already exist.");
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Staging destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var stageRoot = Path.Combine(parent, "." + Path.GetFileName(destination) + ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageRoot);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries) throw new InvalidDataException("Portable archive entry count is invalid.");
            var records = new List<(ZipArchiveEntry Entry, string Relative, bool IsDirectory)>();
            var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.ExternalAttributes & 0x400) != 0 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Reparse points and symbolic links are forbidden in portable archives.");
                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var name = isDirectory ? entry.FullName[..^1] : entry.FullName;
                if (name.Length == 0 || name.Contains('\\')) throw new InvalidDataException("Portable archive path is invalid.");
                var normalized = ValidateRelativePath(name);
                var prefix = "ChatAgentBridge.Portable/";
                if (normalized.Equals("ChatAgentBridge.Portable", StringComparison.Ordinal) && isDirectory) continue;
                if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Portable archive entries must be under ChatAgentBridge.Portable/.");
                var relative = normalized[prefix.Length..];
                if (relative.Length == 0) { if (!isDirectory) throw new InvalidDataException("Portable archive root is not a directory."); continue; }
                if (!paths.TryAdd(normalized, isDirectory)) throw new InvalidDataException("Portable archive contains case-insensitive path collisions.");
                if (!isDirectory)
                {
                    if (entry.Length < 0 || entry.Length > MaxFileBytes) throw new InvalidDataException("Portable archive file exceeds its size limit.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxExpandedBytes) throw new InvalidDataException("Portable archive expanded size exceeds its limit.");
                }
                records.Add((entry, relative, isDirectory));
            }
            foreach (var pair in paths)
            {
                var parts = pair.Key.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    var ancestor = string.Join('/', parts.Take(i));
                    if (paths.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory) throw new InvalidDataException("Portable archive contains a file/directory path conflict.");
                }
            }
            var files = records.Where(r => !r.IsDirectory).ToDictionary(r => r.Relative, r => r.Entry, StringComparer.OrdinalIgnoreCase);
            foreach (var required in RequiredFiles) if (!files.ContainsKey(required)) throw new InvalidDataException($"Portable archive is missing {required}.");
            ValidatePayloadFileNames(files.Keys);
            ValidatePortableMetadata(await ReadEntryAsync(files["portable-release.json"], 64 * 1024, cancellationToken).ConfigureAwait(false), release);
            ValidateFileList(await ReadEntryAsync(files["portable-files.json"], 1024 * 1024, cancellationToken).ConfigureAwait(false), files.Keys);
            var checksums = ParseChecksums(await ReadEntryAsync(files["SHA256SUMS.txt"], 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            var expectedChecksumNames = files.Keys.Where(k => !k.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!checksums.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedChecksumNames)) throw new InvalidDataException("SHA256SUMS.txt does not cover every archive payload file exactly once.");

            var payloadRoot = Path.Combine(stageRoot, "ChatAgentBridge.Portable");
            Directory.CreateDirectory(payloadRoot);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, relative, isDirectory) in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(payloadRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (isDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long copied = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    copied += count;
                    if (copied > entry.Length || copied > MaxFileBytes) throw new InvalidDataException("Portable archive entry expanded beyond its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (copied != entry.Length) throw new InvalidDataException("Portable archive entry size did not match its header.");
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!relative.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) && !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(checksums[relative]))) throw new InvalidDataException($"Portable payload checksum mismatch: {relative}");
                hashes.Add(relative.Replace('\\', '/'), actual);
            }
            ValidateExecutableArchitecture(Path.Combine(payloadRoot, "ChatAgentBridge.Desktop.exe"), release.AssetName.Contains("win-arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64");
            if (files.Keys.Any(IsIdentityPath)) PortableIdentityBundle.LoadValidated(payloadRoot);
            Directory.Move(payloadRoot, destination);
            return new StagedPortableRelease(destination, hashes);
        }
        catch
        {
            TryDeleteTree(stageRoot);
            throw;
        }
        finally { TryDeleteTree(stageRoot); }
    }

    /// <summary>Validates and returns a Windows-safe slash-separated payload path.</summary>
    public static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) throw new InvalidDataException("Payload path must be a relative slash-separated path.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new InvalidDataException("Payload path contains an unsafe segment.");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')) throw new InvalidDataException("Payload path contains a reserved Windows device name.");
            var fileStem = Path.GetFileNameWithoutExtension(part);
            if (ForbiddenSegments.Contains(part) || ForbiddenSegments.Contains(fileStem) || part.Contains("testagent", StringComparison.OrdinalIgnoreCase) || part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload path contains private or test data.");
            if (part.StartsWith("portableupdate", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".cab-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload path contains private updater state.");
        }
        return string.Join('/', parts);
    }

    private static void ValidatePayloadFileNames(IEnumerable<string> paths)
    {
        var files = paths.ToArray();
        ValidateIdentityFileNames(files);
        foreach (var path in files)
        {
            var leaf = path.Split('/').Last(); var ext = Path.GetExtension(leaf);
            if (!IdentityFiles.Contains(path) && (ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cer", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msix", StringComparison.OrdinalIgnoreCase) || ext.Equals(".appx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Portable payload contains a private key, installer, or debug symbol.");
        }
    }

    internal static bool IsIdentityPath(string path) => path.Equals("Identity", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Identity/", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateIdentityFileNames(IEnumerable<string> paths)
    {
        var identity = paths.Where(IsIdentityPath).ToArray();
        if (identity.Length != 0 && (identity.Length != IdentityFiles.Count || !identity.ToHashSet(StringComparer.Ordinal).SetEquals(IdentityFiles)))
            throw new InvalidDataException("The portable identity payload must contain exactly its public metadata, signed package, and certificate.");
    }

    private static void ValidatePortableMetadata(byte[] data, PortableRelease release)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var expectedArchitecture = release.AssetName.Contains("win-arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 || !root.TryGetProperty("releaseVersion", out var version) || version.GetString() != release.Version || !root.TryGetProperty("architecture", out var arch) || !arch.GetString()!.Equals(expectedArchitecture, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("portable-release.json does not match this release candidate.");
    }

    private static void ValidateFileList(byte[] data, IEnumerable<string> actualFiles)
    {
        using var doc = JsonDocument.Parse(data); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 || !root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("portable-files.json has an unsupported schema.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("portable-files.json contains an invalid path.");
            var path = ValidateRelativePath(item.GetString()!);
            if (!listed.Add(path)) throw new InvalidDataException("portable-files.json contains duplicate paths.");
        }
        if (!listed.SetEquals(actualFiles)) throw new InvalidDataException("portable-files.json does not match the archive contents.");
    }

    private static Dictionary<string, string> ParseChecksums(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(data).TrimStart('\uFEFF'));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line.Length < 68 || line[64] != ' ' || line[65] != '*' || !line[..64].All(Uri.IsHexDigit)) throw new InvalidDataException("SHA256SUMS.txt has an invalid row.");
            var name = ValidateRelativePath(line[66..]);
            if (!result.TryAdd(name, line[..64].ToLowerInvariant())) throw new InvalidDataException("SHA256SUMS.txt contains duplicate paths.");
        }
        return result;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int max, CancellationToken token)
    {
        if (entry.Length > max) throw new InvalidDataException("Portable metadata file exceeds its size limit.");
        await using var stream = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > max) throw new InvalidDataException("Portable metadata file exceeds its size limit."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }

    private static void ValidateExecutableArchitecture(string path, string architecture)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Desktop executable has no valid DOS header.");
        stream.Position = 0x3C; var pe = reader.ReadInt32();
        if (pe < 0 || pe > stream.Length - 6) throw new InvalidDataException("Desktop executable has an invalid PE offset.");
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Desktop executable has no PE signature.");
        var machine = reader.ReadUInt16(); var expected = NormalizeArchitecture(architecture) == "arm64" ? 0xAA64 : 0x8664;
        if (machine != expected) throw new InvalidDataException("Desktop executable architecture does not match the portable release metadata.");
    }

    private static string NormalizeArchitecture(string architecture) => architecture.ToLowerInvariant() switch { "x64" or "amd64" or "win-x64" => "x64", "arm64" or "aarch64" or "win-arm64" => "arm64", _ => throw new InvalidDataException("Unknown portable architecture.") };

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) { File.Delete(entry); continue; }
                if ((attrs & FileAttributes.Directory) != 0) TryDeleteTree(entry); else File.Delete(entry);
            }
            Directory.Delete(path, false);
        }
        catch { }
    }
}
