using System.Diagnostics;
using System.Text.Json;

namespace ChatAgentBridge.Core;

/// <summary>Recovers only incoming-file paths that this app previously registered.</summary>
public static class IncomingAttachmentRecovery
{
    private const int ManifestVersion = 1;
    private const int MaximumManifestBytes = 16 * 1024;
    private const int MaximumManifestsPerPass = 256;
    private static readonly AsyncLocal<Registration?> Current = new();
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".md", ".csv", ".json", ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Removes abandoned registered files. Malformed, active, or unsafe registrations are left untouched.</summary>
    public static int RecoverAbandoned(string recoveryDataDirectory)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var registry = ResolveRegistryDirectory(recoveryDataDirectory);
        var recovered = 0;
        var started = Stopwatch.StartNew();
        var manifests = Directory.EnumerateFiles(registry, "*.json", SearchOption.TopDirectoryOnly)
            .Take(MaximumManifestsPerPass).ToArray();

        foreach (var manifestPath in manifests)
        {
            try
            {
                if (started.Elapsed > TimeSpan.FromSeconds(5)) break;
                EnsurePlainFile(manifestPath);
                var id = Path.GetFileNameWithoutExtension(manifestPath);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var lockPath = Path.Combine(registry, id + ".lock");
                using var leaseLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                AttachmentHandlePolicy.EnsureSafe(leaseLock, registry, lockPath);
                EnsurePlainFile(manifestPath);
                Manifest? data;
                using (var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    AttachmentHandlePolicy.EnsureSafe(manifestStream, registry, manifestPath);
                    if (manifestStream.Length <= 0 || manifestStream.Length > MaximumManifestBytes) continue;
                    var bytes = new byte[(int)manifestStream.Length];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var count = manifestStream.Read(bytes, read, bytes.Length - read);
                        if (count == 0) break;
                        read += count;
                    }
                    if (read != bytes.Length) continue;
                    data = JsonSerializer.Deserialize<Manifest>(bytes);
                }
                if (!IsValidManifest(data, id)) continue;
                if (HasLiveWorker(data!.WorkerProcessId, data.WorkerStartTimeUtcTicks)) continue;
                if (!TryCleanup(data)) continue;
                File.Delete(manifestPath);
                recovered++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidDataException or JsonException or NotSupportedException)
            {
                // Recovery is best-effort and fail-closed. The next startup may retry.
            }
        }
        return recovered;
    }

    /// <summary>Associates a just-started worker with the active incoming lease before attachment paths are sent.</summary>
    public static void TrackProcess(Process? process)
    {
        var registration = Current.Value;
        if (registration is null) return;
        if (process is null) throw new InvalidOperationException("The incoming attachment worker process identity is required.");
        int processId;
        long startTime;
        try
        {
            processId = process.Id;
            startTime = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException("The incoming attachment worker process identity could not be read.", ex);
        }
        registration.RecordWorker(processId, startTime);
    }

    internal static Registration CreateRegistration(string recoveryDataDirectory, string workspace, string stagingDirectory, string[] paths)
    {
        var registry = ResolveRegistryDirectory(recoveryDataDirectory);
        var id = Guid.NewGuid().ToString("N");
        var lockPath = Path.Combine(registry, id + ".lock");
        var lockStream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            EnsurePlainFile(lockPath);
            var files = paths.Select(path => new FileEntry(Path.GetFileName(path), null, null, null)).ToArray();
            var manifest = new Manifest(ManifestVersion, id, Path.GetFullPath(workspace), Path.GetFullPath(stagingDirectory), files, null, null);
            var registration = new Registration(registry, lockPath, Path.Combine(registry, id + ".json"), manifest, lockStream);
            registration.Persist();
            return registration;
        }
        catch
        {
            lockStream.Dispose();
            try { File.Delete(lockPath); } catch (IOException) { }
            throw;
        }
    }

    internal static IDisposable BeginEmptyExecutionScope() => new Scope(null);

    private static bool IsValidManifest(Manifest? manifest, string id)
    {
        if (manifest is null || manifest.Version != ManifestVersion || manifest.Id != id || manifest.Files is null ||
            manifest.Files.Length > IncomingAttachmentStager.MaximumAttachmentCount ||
            !Path.IsPathFullyQualified(manifest.Workspace) || !Path.IsPathFullyQualified(manifest.StagingDirectory)) return false;
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.Workspace));
        var staging = Path.GetFullPath(manifest.StagingDirectory);
        if (!string.Equals(Path.GetDirectoryName(staging), workspace, StringComparison.OrdinalIgnoreCase)) return false;
        var stageName = Path.GetFileName(staging);
        if (!stageName.StartsWith(".cab-incoming-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(stageName[".cab-incoming-".Length..], "N", out _)) return false;
        long totalLength = 0;
        for (var i = 0; i < manifest.Files.Length; i++)
        {
            var file = manifest.Files[i];
            if (file is null || string.IsNullOrWhiteSpace(file.Name) || Path.GetFileName(file.Name) != file.Name) return false;
            var expectedPrefix = $"file-{i + 1}";
            var extension = Path.GetExtension(file.Name);
            if (!file.Name.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                !AllowedExtensions.Contains(extension) ||
                !string.Equals(file.Name, expectedPrefix + extension, StringComparison.OrdinalIgnoreCase)) return false;
            if (file.Identity is { CreationTimeUtcTicks: <= 0 } || file.Length is < 0 or > IncomingAttachmentStager.MaximumAttachmentBytes ||
                (file.Sha256 is not null && (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))) ||
                (file.Sha256 is null) != (file.Length is null)) return false;
            if (file.Length is not null)
            {
                totalLength = checked(totalLength + file.Length.Value);
                if (totalLength > IncomingAttachmentStager.MaximumTotalBytes) return false;
            }
        }
        return (manifest.WorkerProcessId is null) == (manifest.WorkerStartTimeUtcTicks is null) &&
            (manifest.WorkerProcessId is null || manifest.WorkerProcessId > 0) &&
            (manifest.WorkerStartTimeUtcTicks is null || manifest.WorkerStartTimeUtcTicks > 0);
    }

    private static bool HasLiveWorker(int? processId, long? startTicks)
    {
        if (processId is null || startTicks is null) return false;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.StartTime.ToUniversalTime().Ticks == startTicks.Value;
        }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryCleanup(Manifest manifest)
    {
        try
        {
            IncomingAttachmentStager.EnsureSafeStagingDirectory(manifest.Workspace, manifest.StagingDirectory);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return false;
        }

        foreach (var file in manifest.Files)
        {
            if (file.Identity is null) continue;
            var path = Path.Combine(manifest.StagingDirectory, file.Name);
            try
            {
                IncomingAttachmentStager.EnsureSafeStagingDirectory(manifest.Workspace, manifest.StagingDirectory);
                if (!File.Exists(path)) continue;
                AttachmentHandlePolicy.DeleteVerifiedFile(path, manifest.Workspace, file.Identity.ToAttachmentIdentity(),
                    file.Length, file.Sha256);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
            {
                return false;
            }
        }

        try
        {
            IncomingAttachmentStager.EnsureSafeStagingDirectory(manifest.Workspace, manifest.StagingDirectory);
            if (Directory.Exists(manifest.StagingDirectory)) Directory.Delete(manifest.StagingDirectory, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ValidateRegistryDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Incoming attachment recovery requires an absolute data directory.");
        var full = Path.GetFullPath(path);
        var missing = new Stack<string>();
        var current = full;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        if (!Directory.Exists(current)) throw new DirectoryNotFoundException("Incoming attachment recovery parent directory does not exist.");
        EnsureDirectoryTreeSafe(current);
        while (missing.Count > 0)
        {
            var next = missing.Pop();
            EnsurePlainDirectory(current);
            Directory.CreateDirectory(next);
            EnsurePlainDirectory(next);
            current = next;
        }
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static void EnsureDirectoryTreeSafe(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            EnsurePlainDirectory(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static string ResolveRegistryDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new InvalidDataException("Incoming attachment recovery requires an absolute data directory.");
        return ValidateRegistryDirectory(Path.Combine(Path.GetFullPath(dataDirectory), "incoming-attachments"));
    }

    private static void EnsurePlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Incoming attachment recovery data contains a reparse point or non-directory.");
    }

    private static void EnsurePlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Incoming attachment recovery record is not a plain file.");
    }

    internal sealed class Registration
    {
        private readonly string _registry;
        private readonly string _lockPath;
        private readonly string _manifestPath;
        private readonly FileStream _lockStream;
        private readonly object _gate = new();
        private Manifest _manifest;
        private int _removed;

        internal Registration(string registry, string lockPath, string manifestPath, Manifest manifest, FileStream lockStream)
        {
            _registry = registry;
            _lockPath = lockPath;
            _manifestPath = manifestPath;
            _manifest = manifest;
            _lockStream = lockStream;
        }

        internal IDisposable BeginExecutionScope()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _removed) != 0, this);
            return new Scope(this);
        }

        internal void RecordFileCreated(int index, AttachmentHandlePolicy.AttachmentFileIdentity identity)
        {
            lock (_gate)
            {
                var files = _manifest.Files.ToArray();
                files[index] = files[index] with { Identity = FileIdentity.From(identity) };
                _manifest = _manifest with { Files = files };
                Persist();
            }
        }

        internal void RecordFileCompleted(int index, AttachmentHandlePolicy.AttachmentFileIdentity identity, long length, string hash)
        {
            lock (_gate)
            {
                var files = _manifest.Files.ToArray();
                files[index] = files[index] with
                {
                    Identity = FileIdentity.From(identity),
                    Length = length,
                    Sha256 = hash
                };
                _manifest = _manifest with { Files = files };
                Persist();
            }
        }

        internal void RecordWorker(int processId, long startTicks)
        {
            lock (_gate)
            {
                _manifest = _manifest with { WorkerProcessId = processId, WorkerStartTimeUtcTicks = startTicks };
                Persist();
            }
        }

        internal void Persist()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_manifest);
            if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("Incoming attachment recovery record is too large.");
            var temp = Path.Combine(_registry, _manifest.Id + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, _manifestPath, overwrite: true);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }

        internal void DisposeAndCleanup()
        {
            if (Interlocked.Exchange(ref _removed, 1) != 0) return;
            try
            {
                if (TryCleanup(_manifest)) TryDelete(_manifestPath);
            }
            finally
            {
                _lockStream.Dispose();
                TryDelete(_lockPath);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Registration? _previous;
        private int _disposed;
        internal Scope(Registration? registration) { _previous = Current.Value; Current.Value = registration; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Current.Value = _previous;
        }
    }

    internal sealed record Manifest(int Version, string Id, string Workspace, string StagingDirectory, FileEntry[] Files,
        int? WorkerProcessId, long? WorkerStartTimeUtcTicks);
    internal sealed record FileEntry(string Name, FileIdentity? Identity, long? Length, string? Sha256);
    internal sealed record FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow, long CreationTimeUtcTicks)
    {
        internal static FileIdentity From(AttachmentHandlePolicy.AttachmentFileIdentity identity) => new(
            identity.VolumeSerialNumber, identity.FileIndexHigh, identity.FileIndexLow, identity.CreationTimeUtcTicks);
        internal AttachmentHandlePolicy.AttachmentFileIdentity ToAttachmentIdentity() => new(
            VolumeSerialNumber, FileIndexHigh, FileIndexLow, CreationTimeUtcTicks);
    }
}
