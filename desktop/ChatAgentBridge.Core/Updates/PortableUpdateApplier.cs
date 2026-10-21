using System.Text.Json;
using ChatAgentBridge.Core.Deployment;

namespace ChatAgentBridge.Core.Updates;

public interface IPortableApplicationStarter
{
    Task<bool> StartAndConfirmAsync(string executable, string dataDirectory, string healthEventName, CancellationToken cancellationToken);
    Task StartRestoredAsync(string executable, string dataDirectory, CancellationToken cancellationToken);
}

public sealed record PortableUpdateResult(bool Success, bool RolledBack, string Version, string? Error);

/// <summary>Runs outside the desktop process. Only validated payload files are replaced.</summary>
public sealed class PortableUpdateApplier(IPortableApplicationStarter starter)
{
    private static readonly JsonSerializerOptions MetadataJson = new() { PropertyNameCaseInsensitive = true };

    public static async Task ValidateStagingAsync(PortableUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        plan.Validate();
        PortableArchiveStager.ValidateIdentityFileNames(plan.Files.Keys);
        var installedIdentity = PortableUpdatePaths.Resolve(plan.InstallDirectory, "Identity/" + PortableIdentityBundle.MetadataFileName);
        if (File.Exists(installedIdentity) && !plan.Files.ContainsKey("Identity/" + PortableIdentityBundle.MetadataFileName))
            throw new InvalidDataException("This update would remove the portable notification identity bundle.");
        foreach (var (relative, hash) in plan.Files)
        {
            var source = PortableUpdatePaths.Resolve(plan.StagingDirectory, relative);
            if (!File.Exists(source) || !hash.Equals(await PortableUpdatePaths.HashAsync(source, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged update verification failed.");
        }
        var metadata = JsonSerializer.Deserialize<ReleaseMetadata>(
            await File.ReadAllTextAsync(Path.Combine(plan.StagingDirectory, "portable-release.json"), cancellationToken), MetadataJson);
        if (metadata is not { SchemaVersion: 1 } || metadata.ReleaseVersion != plan.ReleaseVersion || metadata.Architecture != plan.Architecture)
            throw new InvalidDataException("Staged update release metadata does not match.");
        var listed = await ReadManagedFilesAsync(plan.StagingDirectory, cancellationToken);
        if (listed is null || !listed.SetEquals(plan.Files.Keys))
            throw new InvalidDataException("Staged update file list does not match.");
        if (plan.Files.Keys.Any(PortableArchiveStager.IsIdentityPath)) PortableIdentityBundle.LoadValidated(plan.StagingDirectory);
    }

    public async Task<PortableUpdateResult> ApplyAsync(PortableUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        await ValidateStagingAsync(plan, cancellationToken);
        if (!File.Exists(Path.Combine(plan.InstallDirectory, "ChatAgentBridge.Desktop.exe")))
            throw new InvalidDataException("The portable installation was not found.");
        if (Directory.Exists(plan.BackupDirectory)) throw new IOException("An update backup already exists.");

        FileStream? lease = await AcquireExclusiveAsync(plan.InstallDirectory, cancellationToken);
        var backedUp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mutated = false;
        try
        {
            var oldFiles = await ReadManagedFilesAsync(plan.InstallDirectory, cancellationToken) ?? new(StringComparer.OrdinalIgnoreCase);
            var touched = new HashSet<string>(oldFiles, StringComparer.OrdinalIgnoreCase);
            touched.UnionWith(plan.Files.Keys);
            // Validate every old path before backing up or deleting anything.
            foreach (var relative in touched)
            {
                var target = PortableUpdatePaths.Resolve(plan.InstallDirectory, relative);
                if (PortableUpdatePaths.Overlaps(target, plan.DataDirectory))
                    throw new InvalidDataException("Installed file manifest overlaps settings.");
                if (plan.Files.ContainsKey(relative) && Directory.Exists(target))
                    throw new InvalidDataException("An update file conflicts with an existing folder.");
            }
            Directory.CreateDirectory(plan.BackupDirectory);
            foreach (var relative in touched.Order(StringComparer.OrdinalIgnoreCase))
            {
                var target = PortableUpdatePaths.Resolve(plan.InstallDirectory, relative);
                if (!File.Exists(target)) continue;
                var backup = PortableUpdatePaths.Resolve(plan.BackupDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: false);
                backedUp.Add(relative, await PortableUpdatePaths.HashAsync(backup, cancellationToken));
            }
            await File.WriteAllTextAsync(Path.Combine(plan.WorkDirectory, "backup-files.json"),
                JsonSerializer.Serialize(backedUp), cancellationToken);
            await WritePhaseAsync(plan, "backed_up", cancellationToken);

            // Once mutation starts, finish replacement or rollback even if the caller cancels.
            cancellationToken.ThrowIfCancellationRequested();
            mutated = true;
            foreach (var relative in oldFiles)
                if (!plan.Files.ContainsKey(relative))
                {
                    var obsolete = PortableUpdatePaths.Resolve(plan.InstallDirectory, relative);
                    if (File.Exists(obsolete)) File.Delete(obsolete);
                }
            RemoveEmptyParents(plan.InstallDirectory, oldFiles);
            foreach (var (relative, hash) in plan.Files)
            {
                var source = PortableUpdatePaths.Resolve(plan.StagingDirectory, relative);
                var target = PortableUpdatePaths.Resolve(plan.InstallDirectory, relative);
                await InstallFileAsync(source, target, hash, plan.Nonce);
            }
            await WritePhaseAsync(plan, "installed", CancellationToken.None);
            lease.Dispose();
            lease = null;
            if (!await starter.StartAndConfirmAsync(Path.Combine(plan.InstallDirectory, "ChatAgentBridge.Desktop.exe"),
                    plan.DataDirectory, plan.HealthEventName, CancellationToken.None))
                throw new IOException("The updated application did not confirm startup.");
            var success = new PortableUpdateResult(true, false, plan.ReleaseVersion, null);
            await WriteResultAsync(plan, success);
            return success;
        }
        catch (Exception error)
        {
            if (!mutated)
            {
                lease?.Dispose();
                lease = null;
                await starter.StartRestoredAsync(Path.Combine(plan.InstallDirectory, "ChatAgentBridge.Desktop.exe"), plan.DataDirectory, CancellationToken.None);
                var untouched = new PortableUpdateResult(false, false, plan.ReleaseVersion, error.GetType().Name);
                await WriteResultAsync(plan, untouched);
                return untouched;
            }
            try
            {
                lease ??= await AcquireExclusiveAsync(plan.InstallDirectory, CancellationToken.None);
                foreach (var relative in plan.Files.Keys)
                {
                    var installed = PortableUpdatePaths.Resolve(plan.InstallDirectory, relative);
                    if (File.Exists(installed)) File.Delete(installed);
                }
                RemoveEmptyParents(plan.InstallDirectory, plan.Files.Keys);
                foreach (var (relative, hash) in backedUp)
                    await InstallFileAsync(PortableUpdatePaths.Resolve(plan.BackupDirectory, relative),
                        PortableUpdatePaths.Resolve(plan.InstallDirectory, relative), hash, plan.Nonce);
                await WritePhaseAsync(plan, "rolled_back", CancellationToken.None);
                lease.Dispose();
                lease = null;
                await starter.StartRestoredAsync(Path.Combine(plan.InstallDirectory, "ChatAgentBridge.Desktop.exe"), plan.DataDirectory, CancellationToken.None);
                var restored = new PortableUpdateResult(false, true, plan.ReleaseVersion, error.GetType().Name);
                await WriteResultAsync(plan, restored);
                return restored;
            }
            catch (Exception rollbackError)
            {
                var failed = new PortableUpdateResult(false, false, plan.ReleaseVersion,
                    $"{error.GetType().Name}; recovery: {rollbackError.GetType().Name}");
                await WriteResultAsync(plan, failed);
                return failed;
            }
        }
        finally { lease?.Dispose(); }
    }

    private static async Task InstallFileAsync(string source, string target, string hash, string nonce)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        PortableUpdatePaths.EnsureNoReparsePoints(target);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".cab-new-" + nonce);
        PortableUpdatePaths.EnsureNoReparsePoints(temporary);
        try
        {
            File.Copy(source, temporary, overwrite: true);
            if (!hash.Equals(await PortableUpdatePaths.HashAsync(temporary), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update file changed during installation.");
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<HashSet<string>?> ReadManagedFilesAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "portable-files.json");
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Installed file manifest is too large.");
        var manifest = JsonSerializer.Deserialize<FileManifest>(await File.ReadAllTextAsync(path, cancellationToken), MetadataJson);
        if (manifest is not { SchemaVersion: 1, Files: not null } || manifest.Files.Count > 10000)
            throw new InvalidDataException("Invalid installed file manifest.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in manifest.Files)
        {
            if (name != PortableArchiveStager.ValidateRelativePath(name) || !files.Add(name))
                throw new InvalidDataException("Invalid installed file manifest path.");
        }
        return files;
    }

    private static void RemoveEmptyParents(string root, IEnumerable<string> relativeFiles)
    {
        foreach (var relative in relativeFiles)
        {
            for (var directory = Path.GetDirectoryName(PortableUpdatePaths.Resolve(root, relative));
                 directory is not null && !directory.Equals(root, StringComparison.OrdinalIgnoreCase) && PortableUpdatePaths.IsWithin(root, directory);
                 directory = Path.GetDirectoryName(directory))
            {
                PortableUpdatePaths.EnsureNoReparsePoints(directory);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                else break;
            }
        }
    }

    private static async Task<FileStream> AcquireExclusiveAsync(string directory, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return PortableApplicationLease.Open(directory, exclusive: true); }
            catch (IOException) when (DateTimeOffset.UtcNow < until) { await Task.Delay(200, cancellationToken); }
        }
    }

    private static async Task WritePhaseAsync(PortableUpdatePlan plan, string phase, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(plan.WorkDirectory, "update-phase.json"),
                JsonSerializer.Serialize(new { phase, version = plan.ReleaseVersion }), cancellationToken);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static async Task WriteResultAsync(PortableUpdatePlan plan, PortableUpdateResult result)
    {
        // Reporting must never undo an installation whose application already confirmed startup.
        try { await File.WriteAllTextAsync(plan.ResultFile, JsonSerializer.Serialize(result)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private sealed record ReleaseMetadata(int SchemaVersion, string ReleaseVersion, string Architecture);
    private sealed record FileManifest(int SchemaVersion, IReadOnlyList<string> Files);
}
