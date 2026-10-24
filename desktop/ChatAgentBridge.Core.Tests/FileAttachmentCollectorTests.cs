using System.Security.Cryptography;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentCollectorTests
{
    [Fact]
    public async Task DiscoversOnlyNewSafeFilesInsideTrustedWorkspace()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Path("existing.txt"), "old");
        Directory.CreateDirectory(fixture.Path(".git"));
        Directory.CreateDirectory(fixture.Path(".claude"));
        Directory.CreateDirectory(fixture.Path("node_modules"));
        Directory.CreateDirectory(fixture.Path("artifacts"));
        Directory.CreateDirectory(fixture.Path("build"));
        Directory.CreateDirectory(fixture.Path("dist"));
        await File.WriteAllTextAsync(fixture.Path(".git/config"), "secret");
        await File.WriteAllTextAsync(fixture.Path(".claude/history.json"), "secret");
        await File.WriteAllTextAsync(fixture.Path("node_modules/package.json"), "package");
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);

        await File.WriteAllTextAsync(fixture.Path("new.txt"), "fresh");
        Directory.CreateDirectory(fixture.Path(".cab-incoming-test"));
        await File.WriteAllTextAsync(fixture.Path(".cab-incoming-test/file-1.txt"), "received input must not be uploaded");
        await File.WriteAllTextAsync(fixture.Path(".env.production"), "secret");
        await File.WriteAllTextAsync(fixture.Path("settings.local.json"), "secret");
        await File.WriteAllTextAsync(fixture.Path("credentials-backup.txt"), "secret");
        await File.WriteAllTextAsync(fixture.Path("private.pem"), "secret");
        await File.WriteAllTextAsync(fixture.Path("tool.exe"), "binary");
        await File.WriteAllTextAsync(fixture.Path(".unknown"), "hidden config");
        await File.WriteAllTextAsync(fixture.Path("id_ed25519"), "private key");
        await File.WriteAllTextAsync(fixture.Path("server.ppk"), "private key");
        await File.WriteAllTextAsync(fixture.Path("artifacts/report.txt"), "artifact");
        await File.WriteAllTextAsync(fixture.Path("build/result.txt"), "build output");
        await File.WriteAllTextAsync(fixture.Path("dist/package.txt"), "dist output");
        await File.WriteAllTextAsync(Path.Combine(fixture.Data, "app.txt"), "private app data");

        var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);

        Assert.True(scan.IsComplete);
        Assert.Equal(4, scan.Candidates.Count);
        var candidate = Assert.Single(scan.Candidates, item => item.RelativePath == "new.txt");
        Assert.Equal(Convert.ToHexString(SHA256.HashData("fresh"u8)).ToLowerInvariant(), candidate.Sha256);
        Assert.Contains(scan.Candidates, item => item.RelativePath == Path.Combine("artifacts", "report.txt"));
        Assert.Contains(scan.Candidates, item => item.RelativePath == Path.Combine("build", "result.txt"));
        Assert.Contains(scan.Candidates, item => item.RelativePath == Path.Combine("dist", "package.txt"));
    }

    [Fact]
    public async Task RejectsOutsidePathsAndReparseFiles()
    {
        using var fixture = new Fixture();
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        var outside = Path.Combine(fixture.Parent, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var outsideCandidate = new FileAttachmentCandidate(outside, "outside.txt", 7, File.GetLastWriteTimeUtc(outside), HashFile(outside));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync([outsideCandidate], fixture.Settings, fixture.Data, default));

        var target = Path.Combine(fixture.Parent, "linked-target.txt");
        await File.WriteAllTextAsync(target, "secret");
        try
        {
            var link = fixture.Path("linked.txt");
            File.CreateSymbolicLink(link, target);
            await File.WriteAllTextAsync(fixture.Path("safe.txt"), "safe");
            var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);
            Assert.DoesNotContain(scan.Candidates, item => item.RelativePath == "linked.txt");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { }
    }

    [Fact]
    public async Task RejectsWindowsHardlinksDuringDiscoveryAndPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        var outside = Path.Combine(fixture.Parent, "outside-secret.txt");
        var alias = fixture.Path("report.txt");
        await File.WriteAllTextAsync(outside, "outside secret");
        Assert.True(CreateHardLink(alias, outside, IntPtr.Zero));
        var forged = new FileAttachmentCandidate(alias, "report.txt", new FileInfo(alias).Length, File.GetLastWriteTimeUtc(alias), HashFile(alias));

        var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);
        Assert.False(scan.IsComplete);
        Assert.DoesNotContain(scan.Candidates, item => item.RelativePath == "report.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync([forged], fixture.Settings, fixture.Data, default));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public async Task OpenedHandleRejectsExcludedAttributes(FileAttributes attribute)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = fixture.Path("report.txt");
        await File.WriteAllTextAsync(path, "synthetic output");
        var original = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, original | attribute);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<InvalidDataException>(() => AttachmentHandlePolicy.EnsureSafe(stream, fixture.Workspace, path));
        }
        finally
        {
            File.SetAttributes(path, original);
        }
    }

    [Fact]
    public async Task RejectsAlternateStreamAndUnsafePathNames()
    {
        using var fixture = new Fixture();
        var alternateStream = fixture.Path("report.txt:metadata");
        var forged = new FileAttachmentCandidate(alternateStream, Path.GetRelativePath(fixture.Workspace, alternateStream), 0,
            DateTime.UtcNow, new string('a', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync([forged], fixture.Settings, fixture.Data, default));
    }

    [Fact]
    public async Task DiscoveryKeepsSensitiveNameRulesCaseInsensitive()
    {
        using var fixture = new Fixture();
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        foreach (var name in new[]
        {
            "SECRET-notes.txt", "settingsBackup.txt", "notes_SESSION.txt", "private.KEY", "tool.PS1", ".hidden.txt"
        })
        {
            await File.WriteAllTextAsync(fixture.Path(name), "excluded");
        }
        await File.WriteAllTextAsync(fixture.Path("safe-report.TXT"), "safe");

        var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);

        Assert.True(scan.IsComplete);
        Assert.Equal("safe-report.TXT", Assert.Single(scan.Candidates).RelativePath);
    }

    [Fact]
    public async Task KeepsExistingFilesOutAndPreservesAnIncompleteSnapshotFlag()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Path("already-there.txt"), "old");
        var complete = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        await File.WriteAllTextAsync(fixture.Path("already-there.txt"), "changed");
        var safeFile = fixture.Path("new.txt");
        await File.WriteAllTextAsync(safeFile, "new");
        var scan = await fixture.Collector.DiscoverAsync(complete with { IsComplete = false }, fixture.Settings, default);
        Assert.False(scan.IsComplete);
        Assert.DoesNotContain(scan.Candidates, item => item.RelativePath == "already-there.txt");
        Assert.Contains(scan.Candidates, item => item.RelativePath == "new.txt");
    }

    [Fact]
    public async Task PrepareEnforcesCountAndAggregateLimits()
    {
        using var fixture = new Fixture();
        fixture.SetSettings(fixture.Settings with { FileAttachmentMaxCount = 1 });
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        await File.WriteAllTextAsync(fixture.Path("one.txt"), "1");
        await File.WriteAllTextAsync(fixture.Path("two.txt"), "2");
        var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync(scan.Candidates, fixture.Settings, fixture.Data, default));

        fixture.SetSettings(fixture.Settings with { FileAttachmentMaxCount = 3, FileAttachmentMaxSizeMb = 1, FileAttachmentMaxTotalSizeMb = 1 });
        var aggregateBefore = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        var oversizedPath = fixture.Path("oversized.bin");
        await File.WriteAllBytesAsync(oversizedPath, new byte[1024 * 1024 + 1]);
        var oversizedScan = await fixture.Collector.DiscoverAsync(aggregateBefore, fixture.Settings, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync(oversizedScan.Candidates, fixture.Settings, fixture.Data, default));
        File.Delete(oversizedPath);

        var aggregateSnapshot = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        var bytes = new byte[600 * 1024];
        await File.WriteAllBytesAsync(fixture.Path("three.bin"), bytes);
        await File.WriteAllBytesAsync(fixture.Path("four.bin"), bytes);
        var totalScan = await fixture.Collector.DiscoverAsync(aggregateSnapshot, fixture.Settings, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync(totalScan.Candidates, fixture.Settings, fixture.Data, default));
    }

    [Fact]
    public async Task RejectsMutationAndReturnsStableCopiedBytes()
    {
        using var fixture = new Fixture();
        var before = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        var path = fixture.Path("mutable.txt");
        await File.WriteAllTextAsync(path, "first");
        var scan = await fixture.Collector.DiscoverAsync(before, fixture.Settings, default);
        var candidate = Assert.Single(scan.Candidates);
        await File.WriteAllTextAsync(path, "mutated");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Collector.PrepareAsync([candidate], fixture.Settings, fixture.Data, default));

        var secondBefore = await fixture.Collector.CaptureBeforeAsync(fixture.Workspace, fixture.Data, default);
        await File.WriteAllTextAsync(fixture.Path("copy.txt"), "copied");
        var secondScan = await fixture.Collector.DiscoverAsync(secondBefore, fixture.Settings, default);
        var prepared = Assert.Single(await fixture.Collector.PrepareAsync(secondScan.Candidates, fixture.Settings, fixture.Data, default));
        await File.WriteAllTextAsync(fixture.Path("copy.txt"), "changed after copy");
        using var read = prepared.OpenRead();
        using var reader = new StreamReader(read);
        Assert.Equal("copied", await reader.ReadToEndAsync());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-attachment-" + Guid.NewGuid().ToString("N"));
            Workspace = System.IO.Path.Combine(Parent, "workspace");
            Data = System.IO.Path.Combine(Parent, "app-data");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Data);
            Settings = new BridgeSettings { WorkspaceRoot = Workspace };
        }

        public string Parent { get; }
        public string Workspace { get; }
        public string Data { get; }
        public BridgeSettings Settings { get; private set; }
        public AttachmentFileCollector Collector { get; } = new();
        public string Path(string relative) => System.IO.Path.Combine(Workspace, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void SetSettings(BridgeSettings settings) => Settings = settings;
        public void Dispose() { if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true); }
    }
}
