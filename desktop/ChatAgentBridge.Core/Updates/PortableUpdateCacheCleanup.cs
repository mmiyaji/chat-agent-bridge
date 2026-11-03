using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatAgentBridge.Core.Updates;

public sealed record PortableUpdateCacheCleanupResult(int CleanedWorkDirectories, long RemovedBytes, int SkippedWorkDirectories);

/// <summary>Reclaims update payloads only. Logs and recovery records are never garbage collected here.</summary>
public static class PortableUpdateCacheCleanup
{
    public const string LockFileName = ".cleanup.lock";
    private static readonly HashSet<string> PayloadNames = new(StringComparer.OrdinalIgnoreCase)
        { "stage", "backup", "update.zip", "ChatAgentBridge.Updater.exe" };
    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".jsonl", ".ndjson", ".log", ".txt", ".csv", ".etl", ".dmp", ".trace" };
    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "Data", "logs", "diagnostics" };

    public static FileStream AcquireWorkLease(string workDirectory)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(workDirectory);
        var path = Path.Combine(workDirectory, LockFileName);
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static Task<PortableUpdateCacheCleanupResult> RunAsync(string cacheRoot, DateTimeOffset now,
        string? protectedWorkDirectory = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
            Run(cacheRoot, now, protectedWorkDirectory, cancellationToken), cancellationToken);

    public static Task<long?> GetCacheBytesAsync(string cacheRoot, CancellationToken cancellationToken = default) => Task.Run<long?>(() =>
    {
        if (!Directory.Exists(cacheRoot)) return 0;
        PortableUpdatePaths.EnsureNoReparsePoints(cacheRoot);
        var pending = new Stack<string>(); pending.Push(cacheRoot);
        long bytes = 0;
        var entries = 0;
        try
        {
            while (pending.TryPop(out var path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > 150000) return null;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                else bytes += new FileInfo(path).Length;
            }
        }
        catch (Exception e) when (IsLocalError(e)) { return null; }
        return bytes;
    }, cancellationToken);

    private static PortableUpdateCacheCleanupResult Run(string root, DateTimeOffset now, string? protectedWork,
        CancellationToken token)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return new(0, 0, 0);
        PortableUpdatePaths.EnsureNoReparsePoints(root);
        var candidates = new List<Candidate>();
        var cleaned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long removed = 0;
        var skipped = 0;
        // A bound keeps startup housekeeping finite even with malformed or very old caches.
        var inspected = 0;
        foreach (var owner in Directory.EnumerateDirectories(root).Take(1024))
        {
            token.ThrowIfCancellationRequested();
            if (inspected >= 4096) break;
            if (Guid.TryParseExact(Path.GetFileName(owner), "N", out _)) Inspect(owner, null);
            else if (IsHash(Path.GetFileName(owner)))
            {
                try
                {
                    PortableUpdatePaths.EnsureNoReparsePoints(owner);
                    foreach (var work in Directory.EnumerateDirectories(owner).Take(1024))
                        if (Guid.TryParseExact(Path.GetFileName(work), "N", out _)) Inspect(work, Path.GetFileName(owner));
                }
                catch (Exception e) when (IsLocalError(e)) { skipped++; }
            }
        }

        void Inspect(string work, string? owner)
        {
            if (++inspected > 4096) return;
            if (protectedWork is not null && SamePath(work, protectedWork)) { skipped++; return; }
            try
            {
                using var lease = AcquireWorkLease(work);
                var candidate = ReadCandidate(work, owner, now);
                if (candidate is not null)
                {
                    // A killed validation can leave a second extracted copy beside a
                    // perfectly valid pending update. Retire only its stale scratch files.
                    if (candidate.Kind == Kind.Pending)
                    {
                        var reclaimed = RemoveStaleScratch(work, now, token);
                        if (reclaimed > 0) { cleaned.Add(work); removed += reclaimed; }
                    }
                    candidates.Add(candidate);
                }
                else skipped++;
            }
            catch (Exception e) when (IsLocalError(e)) { skipped++; }
        }

        var protectedPending = candidates.Where(c => c.Kind == Kind.Pending)
            .GroupBy(c => c.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Version).ThenByDescending(c => c.Timestamp).First().Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedRecovery = candidates.Where(c => c.Kind == Kind.Restored && now - c.Timestamp < TimeSpan.FromDays(7))
            .GroupBy(c => c.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Timestamp).First().Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligible = candidates.Where(c => !protectedPending.Contains(c.Path) && !retainedRecovery.Contains(c.Path))
            .OrderBy(c => c.Timestamp).ToArray();
        // Reclaim all obsolete payloads rather than retaining garbage up to a budget.
        // Pending updates and the only recoverable backup are always protected.
        foreach (var candidate in eligible)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var lease = AcquireWorkLease(candidate.Path);
                EnsureSafeTree(candidate.Path, token);
                var current = ReadCandidate(candidate.Path, candidate.Legacy ? null : candidate.Owner, now);
                if (current is null || current.Kind != candidate.Kind || current.Timestamp != candidate.Timestamp)
                { skipped++; continue; }
                var bytes = RemovePayload(candidate.Path, token);
                if (bytes > 0) { cleaned.Add(candidate.Path); removed += bytes; }
            }
            catch (Exception e) when (IsLocalError(e)) { skipped++; }
        }
        var result = new PortableUpdateCacheCleanupResult(cleaned.Count, removed,
            skipped + protectedPending.Count + retainedRecovery.Count);
        if (cleaned.Count > 0)
        {
            try
            {
                var log = Path.Combine(root, "cleanup-history.jsonl");
                PortableUpdatePaths.EnsureNoReparsePoints(log);
                File.AppendAllText(log, JsonSerializer.Serialize(new { TimestampUtc = now, result.CleanedWorkDirectories,
                    result.RemovedBytes, result.SkippedWorkDirectories }) + "\n");
            }
            catch (Exception e) when (IsLocalError(e)) { /* Housekeeping must not prevent app startup. */ }
        }
        return result;
    }

    private static Candidate? ReadCandidate(string work, string? owner, DateTimeOffset now)
    {
        var planPath = Path.Combine(work, PortableUpdatePlan.FileName);
        var stagePath = Path.Combine(work, PortableUpdateStageStore.StateFileName);
        var hasPlan = File.Exists(planPath);
        var hasStage = File.Exists(stagePath);
        var hasBackup = Directory.Exists(Path.Combine(work, "backup"));
        string? install = null, data = null;
        ReleaseVersion version = default;
        if (hasPlan || hasStage)
        {
            // Check both records when present, including copies left by older updaters.
            foreach (var path in new[] { planPath, stagePath }.Where(File.Exists))
            {
                using var json = ReadJson(path);
                var row = json.RootElement;
                var rowInstall = Text(row, "InstallDirectory");
                var rowData = Text(row, "DataDirectory");
                if (!ValidAbsolute(rowInstall) || !ValidAbsolute(rowData) || !SamePath(Text(row, "WorkDirectory"), work) ||
                    PortableUpdatePaths.Overlaps(work, rowInstall!) || PortableUpdatePaths.Overlaps(work, rowData!)) return null;
                if (install is not null && (!SamePath(install, rowInstall) || !SamePath(data, rowData))) return null;
                install = rowInstall; data = rowData;
                if (owner is not null && OwnerKey(install!, data!) != owner) return null;
                if (TryProperty(row, "Release", out var release)) ReleaseVersion.TryParse(Text(release, "Version"), out version);
            }
        }
        else
        {
            // Anonymous legacy downloads are recognized only by the exact historic layout.
            if (hasBackup || Directory.EnumerateFileSystemEntries(work).Any(p =>
                !IsPayloadName(Path.GetFileName(p)) && !ProtectedDirectories.Contains(Path.GetFileName(p)) && Path.GetFileName(p) != LockFileName &&
                !LogExtensions.Contains(Path.GetExtension(p)))) return null;
            if (!Directory.EnumerateFileSystemEntries(work).Any(p => IsPayloadName(Path.GetFileName(p)))) return null;
        }
        var ownerKey = owner ?? (install is not null ? OwnerKey(install, data!) : work);
        var resultPath = Path.Combine(work, "update-result.json");
        if (File.Exists(resultPath))
        {
            if (!hasPlan) return null;
            using var json = ReadJson(resultPath);
            var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(resultPath));
            if (Boolean(json.RootElement, "Success")) return new(work, ownerKey, owner is null, Kind.Completed, timestamp, version);
            if (Boolean(json.RootElement, "RolledBack")) return new(work, ownerKey, owner is null, Kind.Restored, timestamp, version);
            if (hasBackup) return null; // The only recoverable copy must never expire.
            return now - timestamp >= TimeSpan.FromDays(1)
                ? new(work, ownerKey, owner is null, Kind.Abandoned, timestamp, version) : null;
        }
        if (hasBackup) return null; // Interrupted apply / unknown recovery state.
        if (hasStage && version != default)
            return new(work, ownerKey, owner is null, Kind.Pending, new DateTimeOffset(File.GetLastWriteTimeUtc(stagePath)), version);
        var latest = Directory.EnumerateFileSystemEntries(work).Where(p => Path.GetFileName(p) != LockFileName)
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(work)).Max();
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc));
        return now - stamp >= TimeSpan.FromDays(1) ? new(work, ownerKey, owner is null, Kind.Abandoned, stamp, version) : null;
    }

    // The stage reader already owns the work lease while calling this method.
    internal static void CleanStaleScratchPayload(string installDirectory, string dataDirectory,
        string workDirectory, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWork(installDirectory, dataDirectory, workDirectory);
        RemoveStaleScratch(workDirectory, now, cancellationToken);
    }

    private static long RemoveStaleScratch(string work, DateTimeOffset now, CancellationToken token)
    {
        var stale = Directory.EnumerateFileSystemEntries(work)
            .Where(p => IsScratchName(Path.GetFileName(p)) &&
                now - new DateTimeOffset(File.GetLastWriteTimeUtc(p)) >= TimeSpan.FromDays(1)).ToArray();
        foreach (var scratch in stale) EnsureSafeTree(scratch, token);
        if (stale.Length == 0) return 0;
        var paths = stale.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RemovePayload(work, token, paths.Contains);
    }

    private static void ValidateOwnedWork(string installDirectory, string dataDirectory, string workDirectory)
    {
        var root = PortableUpdateStageStore.GetWorkRoot(installDirectory, dataDirectory);
        if (!SamePath(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), root) ||
            !Guid.TryParseExact(Path.GetFileName(workDirectory), "N", out _))
            throw new InvalidDataException("Update work directory is outside its owner root.");
        PortableUpdatePaths.EnsureNoReparsePoints(workDirectory);
    }

    public static void DiscardOwnedPayload(string installDirectory, string dataDirectory, string workDirectory)
    {
        ValidateOwnedWork(installDirectory, dataDirectory, workDirectory);
        if (!Directory.Exists(workDirectory)) return;
        using var lease = AcquireWorkLease(workDirectory);
        EnsureSafeTree(workDirectory, CancellationToken.None);
        // Keep the original record for diagnosis but never offer a discarded stage again.
        var state = Path.Combine(workDirectory, PortableUpdateStageStore.StateFileName);
        if (File.Exists(state)) File.Move(state, Path.Combine(workDirectory, "retired-stage-" + Guid.NewGuid().ToString("N") + ".json"));
        RemovePayload(workDirectory, CancellationToken.None);
    }

    // Called while the updater holds the work lease and after startup health was confirmed.
    public static void CleanSuccessfulPayload(PortableUpdatePlan plan, string? cacheRoot = null)
    {
        plan.Validate();
        var expectedOwner = PortableUpdateStageStore.GetWorkRoot(plan.InstallDirectory, plan.DataDirectory);
        cacheRoot ??= Path.GetDirectoryName(expectedOwner)!;
        var parent = Path.GetDirectoryName(plan.WorkDirectory);
        var ownerRoot = Path.Combine(cacheRoot, OwnerKey(plan.InstallDirectory, plan.DataDirectory));
        if (!Guid.TryParseExact(Path.GetFileName(plan.WorkDirectory), "N", out _) ||
            (!SamePath(parent, ownerRoot) && !SamePath(parent, cacheRoot)))
            throw new InvalidDataException("Update payload cleanup is outside the owned cache root.");
        if (!PortableUpdateStageStore.IsCompleted(plan.WorkDirectory)) return;
        EnsureSafeTree(plan.WorkDirectory, CancellationToken.None);
        RemovePayload(plan.WorkDirectory, CancellationToken.None);
    }

    private static long RemovePayload(string work, CancellationToken token, Func<string, bool>? include = null)
    {
        long bytes = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(work)
                     .Where(p => IsPayloadName(Path.GetFileName(p)) && (include is null || include(p)))
                     .OrderBy(p => Path.GetFileName(p).Equals("ChatAgentBridge.Updater.exe", StringComparison.OrdinalIgnoreCase)))
            Delete(child);
        return bytes;
        void Delete(string path)
        {
            token.ThrowIfCancellationRequested();
            PortableUpdatePaths.EnsureNoReparsePoints(path);
            if (Directory.Exists(path))
            {
                if (ProtectedDirectories.Contains(Path.GetFileName(path))) return;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Delete(entry);
                if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            else if (!LogExtensions.Contains(Path.GetExtension(path)))
            {
                // Windows cannot remove the updater's own running executable. It is
                // small and will be reclaimed by the desktop on the next startup.
                if (SamePath(path, Environment.ProcessPath)) return;
                var size = new FileInfo(path).Length;
                File.Delete(path);
                bytes += size;
            }
        }
    }

    private static void EnsureSafeTree(string root, CancellationToken token)
    {
        // Validate the entire tree before deleting the first file; never traverse a junction.
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 30000) throw new InvalidDataException("Update cache has too many entries.");
            PortableUpdatePaths.EnsureNoReparsePoints(path);
            if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
    }

    private static JsonDocument ReadJson(string path)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Update record too large.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
    private static bool TryProperty(JsonElement row, string name, out JsonElement value)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var property in row.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static string? Text(JsonElement row, string name) => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Boolean(JsonElement row, string name) => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool ValidAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Path.GetPathRoot(path) != Path.TrimEndingDirectorySeparator(path);
    private static bool SamePath(string? a, string? b) => a is not null && b is not null && Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool IsScratchName(string name) =>
        (name.StartsWith(".validate-", StringComparison.Ordinal) && Guid.TryParseExact(name[10..], "N", out _)) ||
        (name.StartsWith(".update.zip.download-", StringComparison.Ordinal) && Guid.TryParseExact(name[21..], "N", out _));
    private static bool IsPayloadName(string name) => PayloadNames.Contains(name) || IsScratchName(name);
    private static bool IsHash(string name) => name.Length == 64 && name.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
    private static string OwnerKey(string install, string data) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)) + "\n" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)))));
    private static bool IsLocalError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or NotSupportedException;
    private enum Kind { Completed, Pending, Restored, Abandoned }
    private sealed record Candidate(string Path, string Owner, bool Legacy, Kind Kind, DateTimeOffset Timestamp, ReleaseVersion Version);
}
