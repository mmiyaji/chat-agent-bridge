using System.Security.Cryptography;
using System.Text.Json;
using ChatAgentBridge.Core.Updates;

public sealed class PortableUpdateCacheCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompletedWorkRemovesPayloadAndRetainsLogsAndJson()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "success");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":true,\"RolledBack\":false}");
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        await File.WriteAllTextAsync(Path.Combine(work, "update.log"), "keep this log");
        await File.WriteAllTextAsync(Path.Combine(work, "diagnostics.json"), "{\"keep\":true}");
        Directory.CreateDirectory(Path.Combine(work, "backup"));
        await File.WriteAllTextAsync(Path.Combine(work, "backup", "old.bin"), "backup");
        Directory.CreateDirectory(Path.Combine(work, "logs"));
        await File.WriteAllTextAsync(Path.Combine(work, "logs", "updater.log"), "keep");

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(1, result.CleanedWorkDirectories);
        Assert.False(File.Exists(Path.Combine(work, "update.zip")));
        Assert.False(Directory.Exists(Path.Combine(work, "backup")));
        Assert.Equal("keep this log", await File.ReadAllTextAsync(Path.Combine(work, "update.log")));
        Assert.True(File.Exists(Path.Combine(work, "diagnostics.json")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(work, "logs", "updater.log")));
        Assert.True(Directory.Exists(work));
    }

    [Fact]
    public async Task RestoredBackupIsRetainedForSevenDaysThenReclaimed()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "restored");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":false,\"RolledBack\":true}");
        Directory.CreateDirectory(Path.Combine(work, "backup"));
        await File.WriteAllTextAsync(Path.Combine(work, "backup", "old.bin"), "backup");
        File.SetLastWriteTimeUtc(Path.Combine(work, "update-result.json"), Now.AddDays(-8).UtcDateTime);

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(1, result.CleanedWorkDirectories);
        Assert.False(Directory.Exists(Path.Combine(work, "backup")));
    }

    [Fact]
    public async Task RecentRestoredBackupIsProtected()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "recent-restore");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":false,\"RolledBack\":true}");
        File.SetLastWriteTimeUtc(Path.Combine(work, "update-result.json"), Now.AddDays(-2).UtcDateTime);
        Directory.CreateDirectory(Path.Combine(work, "backup"));
        await File.WriteAllTextAsync(Path.Combine(work, "backup", "old.bin"), "backup");

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(0, result.CleanedWorkDirectories);
        Assert.True(File.Exists(Path.Combine(work, "backup", "old.bin")));
    }

    [Fact]
    public async Task PendingUpdateIsPreservedRegardlessOfAge()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "pending");
        await fixture.WritePlanAsync(work, "0.2.0");
        File.SetLastWriteTimeUtc(Path.Combine(work, "portable-update-stage.json"), Now.AddDays(-30).UtcDateTime);
        Directory.CreateDirectory(Path.Combine(work, "stage"));
        await File.WriteAllTextAsync(Path.Combine(work, "stage", "payload.bin"), "staged");
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        Directory.SetLastWriteTimeUtc(work, Now.AddDays(-30).UtcDateTime);

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(0, result.CleanedWorkDirectories);
        Assert.True(File.Exists(Path.Combine(work, "update.zip")));
        Assert.True(File.Exists(Path.Combine(work, "stage", "payload.bin")));
    }

    [Fact]
    public async Task MismatchedOwnershipAndActiveCleanupLockAreSkipped()
    {
        using var fixture = new CacheFixture();
        var unsafeWork = fixture.CreateWork("0.1.0", "unsafe");
        await fixture.WritePlanAsync(unsafeWork, installDirectory: Path.Combine(fixture.Root, "other-install"));
        await File.WriteAllTextAsync(Path.Combine(unsafeWork, "update-result.json"), "{\"Success\":true,\"RolledBack\":false}");
        await File.WriteAllTextAsync(Path.Combine(unsafeWork, "update.zip"), "archive");

        var lockedWork = fixture.CreateWork("0.2.0", "locked");
        await fixture.WritePlanAsync(lockedWork);
        await File.WriteAllTextAsync(Path.Combine(lockedWork, "update-result.json"), "{\"Success\":true,\"RolledBack\":false}");
        await File.WriteAllTextAsync(Path.Combine(lockedWork, "update.zip"), "archive");
        using var heldLock = new FileStream(Path.Combine(lockedWork, ".cleanup.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(0, result.CleanedWorkDirectories);
        Assert.True(File.Exists(Path.Combine(unsafeWork, "update.zip")));
        Assert.True(File.Exists(Path.Combine(lockedWork, "update.zip")));
    }

    [Fact]
    public async Task ReparsePointInsideWorkTreePreventsCleanup()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "reparse");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":true,\"RolledBack\":false}");
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(Path.Combine(work, "stage"), target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);

        Assert.Equal(0, result.CleanedWorkDirectories);
        Assert.True(File.Exists(Path.Combine(work, "update.zip")));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task FailedOrInterruptedRecoveryNeverExpires()
    {
        using var fixture = new CacheFixture();
        foreach (var completedFailure in new[] { false, true })
        {
            var work = fixture.CreateWork("0.1.0", "recovery");
            await fixture.WritePlanAsync(work);
            Directory.CreateDirectory(Path.Combine(work, "backup"));
            await File.WriteAllTextAsync(Path.Combine(work, "backup", "only-copy.bin"), "keep");
            if (completedFailure)
            {
                await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":false,\"RolledBack\":false}");
                File.SetLastWriteTimeUtc(Path.Combine(work, "update-result.json"), Now.AddYears(-1).UtcDateTime);
            }
            var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
            Assert.Equal(0, result.CleanedWorkDirectories);
            Assert.True(File.Exists(Path.Combine(work, "backup", "only-copy.bin")));
        }
    }

    [Fact]
    public async Task KeepsLatestPendingAndReclaimsSupersededPayloadButNotRecords()
    {
        using var fixture = new CacheFixture();
        var old = fixture.CreateWork("0.1.0", "old");
        var latest = fixture.CreateWork("0.2.0", "latest");
        await fixture.WritePlanAsync(old, "0.1.0");
        await fixture.WritePlanAsync(latest, "0.2.0");
        await File.WriteAllTextAsync(Path.Combine(old, "update.zip"), "old");
        await File.WriteAllTextAsync(Path.Combine(latest, "update.zip"), "latest");
        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.Equal(1, result.CleanedWorkDirectories);
        Assert.False(File.Exists(Path.Combine(old, "update.zip")));
        Assert.True(File.Exists(Path.Combine(old, "portable-update-stage.json")));
        Assert.True(File.Exists(Path.Combine(latest, "update.zip")));
    }

    [Fact]
    public async Task CurrentWorkAndRecentPartialDownloadAreProtected()
    {
        using var fixture = new CacheFixture();
        var current = fixture.CreateWork("0.1.0", "current");
        await fixture.WritePlanAsync(current);
        await File.WriteAllTextAsync(Path.Combine(current, "update-result.json"), "{\"Success\":true}");
        await File.WriteAllTextAsync(Path.Combine(current, "update.zip"), "current");
        var partial = fixture.CreateWork("0.1.0", "partial");
        await File.WriteAllTextAsync(Path.Combine(partial, "update.zip"), "partial");
        File.SetLastWriteTimeUtc(Path.Combine(partial, "update.zip"), Now.AddHours(-1).UtcDateTime);
        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now, current);
        Assert.Equal(0, result.CleanedWorkDirectories);
        Assert.True(File.Exists(Path.Combine(current, "update.zip")));
        Assert.True(File.Exists(Path.Combine(partial, "update.zip")));
    }

    [Fact]
    public async Task OldAnonymousLegacyPayloadIsRemovedAndNestedLogsAreKept()
    {
        using var fixture = new CacheFixture();
        var work = Path.Combine(fixture.CacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "stage", "logs"));
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        await File.WriteAllTextAsync(Path.Combine(work, "stage", "payload.dll"), "payload");
        await File.WriteAllTextAsync(Path.Combine(work, "stage", "events.jsonl"), "events");
        await File.WriteAllTextAsync(Path.Combine(work, "stage", "logs", "opaque.bin"), "log");
        File.SetLastWriteTimeUtc(Path.Combine(work, "update.zip"), Now.AddDays(-2).UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(work, "stage"), Now.AddDays(-2).UtcDateTime);
        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.Equal(1, result.CleanedWorkDirectories);
        Assert.False(File.Exists(Path.Combine(work, "update.zip")));
        Assert.False(File.Exists(Path.Combine(work, "stage", "payload.dll")));
        Assert.True(File.Exists(Path.Combine(work, "stage", "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(work, "stage", "logs", "opaque.bin")));
    }

    [Fact]
    public async Task CleanupIsIdempotentAndDoesNotTouchDataOrLogs()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "completed");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":true}");
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        await File.WriteAllTextAsync(Path.Combine(fixture.Data, "history.jsonl"), "user-data");
        await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        var again = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.Equal(0, again.CleanedWorkDirectories);
        Assert.Equal("user-data", await File.ReadAllTextAsync(Path.Combine(fixture.Data, "history.jsonl")));
        Assert.True(File.Exists(Path.Combine(work, "update-plan.json")));
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "cleanup-history.jsonl")));
    }

    [Fact]
    public async Task SuccessfulHelperCleanupPreservesRecordsAndRejectsForeignDiscardRoot()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "helper");
        await fixture.WritePlanAsync(work);
        Directory.CreateDirectory(Path.Combine(work, "stage"));
        Directory.CreateDirectory(Path.Combine(work, "backup"));
        await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":true}");
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "archive");
        await File.WriteAllTextAsync(Path.Combine(work, "backup", "old.dll"), "old");
        await File.WriteAllTextAsync(Path.Combine(work, "backup", "audit.jsonl"), "audit");
        var files = new[] { "ChatAgentBridge.Desktop.exe", "ChatAgentBridge.Updater.exe", "portable-release.json", "portable-files.json" }
            .ToDictionary(name => name, _ => new string('a', 64));
        var plan = new PortableUpdatePlan(1, fixture.Install, fixture.Data, Path.Combine(work, "stage"), work,
            1, 1, files, "0.1.0", "x64", Guid.NewGuid().ToString("N"));
        using (PortableUpdateCacheCleanup.AcquireWorkLease(work))
            PortableUpdateCacheCleanup.CleanSuccessfulPayload(plan, fixture.CacheRoot);
        Assert.False(File.Exists(Path.Combine(work, "update.zip")));
        Assert.False(File.Exists(Path.Combine(work, "backup", "old.dll")));
        Assert.True(File.Exists(Path.Combine(work, "backup", "audit.jsonl")));
        Assert.True(File.Exists(Path.Combine(work, "update-result.json")));
        Assert.Throws<InvalidDataException>(() => PortableUpdateCacheCleanup.CleanSuccessfulPayload(plan));
        // Discard API validates actual default ownership rather than accepting any root.
        Assert.Throws<InvalidDataException>(() => PortableUpdateCacheCleanup.DiscardOwnedPayload(fixture.Install, fixture.Data, work));
    }

    [Fact]
    public async Task InterruptedDownloadFragmentExpiresAfterOneDay()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "partial");
        var partial = Path.Combine(work, ".update.zip.download-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(partial, "unfinished");
        File.SetLastWriteTimeUtc(partial, Now.AddDays(-2).UtcDateTime);
        var result = await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.Equal(1, result.CleanedWorkDirectories);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task PendingUpdateKeepsArchiveButReclaimsStaleValidationCopy()
    {
        using var fixture = new CacheFixture();
        var work = fixture.CreateWork("0.1.0", "pending");
        await fixture.WritePlanAsync(work);
        await File.WriteAllTextAsync(Path.Combine(work, "update.zip"), "pending");
        var scratch = Path.Combine(work, ".validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        await File.WriteAllTextAsync(Path.Combine(scratch, "duplicate.dll"), "duplicate");
        await File.WriteAllTextAsync(Path.Combine(scratch, "audit.log"), "log");
        Directory.SetLastWriteTimeUtc(scratch, Now.AddDays(-2).UtcDateTime);
        await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.False(File.Exists(Path.Combine(scratch, "duplicate.dll")));
        Assert.True(File.Exists(Path.Combine(scratch, "audit.log")));
        Assert.True(File.Exists(Path.Combine(work, "update.zip")));
    }

    [Fact]
    public async Task RetainsOnlyLatestRestoredBackupAndKeepsEveryResultLog()
    {
        using var fixture = new CacheFixture();
        var old = fixture.CreateWork("0.1.0", "old-restored");
        var latest = fixture.CreateWork("0.2.0", "new-restored");
        foreach (var work in new[] { old, latest })
        {
            await fixture.WritePlanAsync(work);
            await File.WriteAllTextAsync(Path.Combine(work, "update-result.json"), "{\"Success\":false,\"RolledBack\":true}");
            Directory.CreateDirectory(Path.Combine(work, "backup"));
            await File.WriteAllTextAsync(Path.Combine(work, "backup", "old.dll"), "backup");
        }
        File.SetLastWriteTimeUtc(Path.Combine(old, "update-result.json"), Now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(latest, "update-result.json"), Now.AddDays(-1).UtcDateTime);
        await PortableUpdateCacheCleanup.RunAsync(fixture.CacheRoot, Now);
        Assert.False(File.Exists(Path.Combine(old, "backup", "old.dll")));
        Assert.True(File.Exists(Path.Combine(latest, "backup", "old.dll")));
        Assert.True(File.Exists(Path.Combine(old, "update-result.json")));
        Assert.True(File.Exists(Path.Combine(latest, "update-result.json")));
    }

    [Fact]
    public async Task CacheSizeIncludesRetainedLogsWithoutModifyingAnything()
    {
        using var fixture = new CacheFixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.CacheRoot, "events.jsonl"), new byte[10]);
        var work = fixture.CreateWork("0.1.0", "size");
        await File.WriteAllBytesAsync(Path.Combine(work, "update.zip"), new byte[25]);
        Assert.Equal(35L, await PortableUpdateCacheCleanup.GetCacheBytesAsync(fixture.CacheRoot));
        Assert.True(File.Exists(Path.Combine(work, "update.zip")));
        Assert.Equal(0L, await PortableUpdateCacheCleanup.GetCacheBytesAsync(Path.Combine(fixture.Root, "missing")));
    }

    private sealed class CacheFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cab-update-cleanup-" + Guid.NewGuid().ToString("N"));
        public string CacheRoot => Path.Combine(Root, "cache");
        public string Install { get; }
        public string Data { get; }

        public CacheFixture()
        {
            Install = Path.Combine(Root, "install");
            Data = Path.Combine(Root, "data");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(CacheRoot);
        }

        public string CreateWork(string version, string label)
        {
            var owner = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                Path.GetFullPath(Install).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\n" +
                Path.GetFullPath(Data).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            var ownerRoot = Path.Combine(CacheRoot, owner);
            Directory.CreateDirectory(ownerRoot);
            var work = Path.Combine(ownerRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }

        public async Task WritePlanAsync(string work, string releaseVersion = "0.1.0", string? installDirectory = null)
        {
            var plan = new
            {
                InstallDirectory = installDirectory ?? Install,
                DataDirectory = Data,
                WorkDirectory = work,
                Release = new { Version = releaseVersion }
            };
            var json = JsonSerializer.Serialize(plan);
            await File.WriteAllTextAsync(Path.Combine(work, "update-plan.json"), json);
            await File.WriteAllTextAsync(Path.Combine(work, "portable-update-stage.json"), json);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
