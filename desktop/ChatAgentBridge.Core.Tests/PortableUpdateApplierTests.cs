using System.Diagnostics;
using System.Text.Json;
using ChatAgentBridge.Core.Updates;

public sealed class PortableUpdateApplierTests
{
    [Fact]
    public async Task AppliesUpdatePreservingDataAndUnmanagedFilesAndBacksUpOldPayload()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedFile("obsolete.dll", "obsolete");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json", "obsolete.dll");
        fixture.WriteData("settings.json", "private-settings");
        fixture.WriteUnmanagedFile("local-notes.txt", "keep-local");
        var plan = await fixture.CreatePlanAsync();
        var starter = new StubStarter((exe, data) =>
        {
            Assert.Equal("new-app", File.ReadAllText(exe));
            Assert.Equal("private-settings", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Install, "obsolete.dll")));
            return true;
        });

        var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);

        Assert.True(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal("private-settings", await File.ReadAllTextAsync(Path.Combine(fixture.Data, "settings.json")));
        Assert.Equal("keep-local", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "local-notes.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Install, "obsolete.dll")));
        Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(plan.BackupDirectory, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal("obsolete", await File.ReadAllTextAsync(Path.Combine(plan.BackupDirectory, "obsolete.dll")));
        Assert.Equal(1, starter.ConfirmedStarts);
        Assert.Equal(0, starter.RestoredStarts);
    }

    [Fact]
    public async Task ResultFileWriteFailureAfterHealthyStartupKeepsUpdatedApplicationRunning()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json");
        var plan = await fixture.CreatePlanAsync();
        var starter = new StubStarter((exe, _) =>
        {
            Assert.Equal("new-app", File.ReadAllText(exe));
            Directory.CreateDirectory(plan.ResultFile);
            return true;
        });

        var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);

        Assert.True(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal(1, starter.ConfirmedStarts);
        Assert.Equal(0, starter.RestoredStarts);
    }

    [Fact]
    public async Task PhaseLogWriteFailureDoesNotAbortHealthyUpdate()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json");
        var plan = await fixture.CreatePlanAsync();
        Directory.CreateDirectory(Path.Combine(plan.WorkDirectory, "update-phase.json"));
        var starter = new StubStarter((exe, _) =>
        {
            Assert.Equal("new-app", File.ReadAllText(exe));
            return true;
        });

        var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);

        Assert.True(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal(1, starter.ConfirmedStarts);
        Assert.Equal(0, starter.RestoredStarts);
    }

    [Fact]
    public async Task FailedHealthCheckRollsBackOldFilesRemovesAddedFilesAndRestartsOldApp()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedFile("old.dll", "old-managed");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json", "old.dll");
        var plan = await fixture.CreatePlanAsync(includeAddedFile: true);
        var starter = new StubStarter((exe, _) =>
        {
            Assert.Equal("new-app", File.ReadAllText(exe));
            Assert.True(File.Exists(Path.Combine(fixture.Install, "added", "new.dll")));
            Assert.False(File.Exists(Path.Combine(fixture.Install, "old.dll")));
            return false;
        }, restored: (exe, _) =>
        {
            Assert.Equal("old-app", File.ReadAllText(exe));
            Assert.Equal("old-managed", File.ReadAllText(Path.Combine(fixture.Install, "old.dll")));
        });

        var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal("old-managed", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "old.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Install, "added", "new.dll")));
        Assert.Equal(1, starter.ConfirmedStarts);
        Assert.Equal(1, starter.RestoredStarts);
    }

    [Fact]
    public async Task RejectsTamperedStagingBeforeMutatingOrRestartingInstallation()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json");
        var plan = await fixture.CreatePlanAsync();
        await File.WriteAllTextAsync(Path.Combine(plan.StagingDirectory, "ChatAgentBridge.Desktop.exe"), "tampered");
        var starter = new StubStarter();

        await Assert.ThrowsAsync<InvalidDataException>(() => new PortableUpdateApplier(starter).ApplyAsync(plan));

        Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.Equal(0, starter.ConfirmedStarts);
        Assert.Equal(0, starter.RestoredStarts);
    }

    [Fact]
    public async Task RejectsOldManifestThatTargetsDataAndRestartsUnchangedApplication()
    {
        using var fixture = new UpdateFixture(dataDirectoryName: "UserBox");
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteData("settings.json", "private-settings");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json", "userbox/settings.json");
        var plan = await fixture.CreatePlanAsync();
        var starter = new StubStarter(restored: (exe, _) => Assert.Equal("old-app", File.ReadAllText(exe)));

        var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);

        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal("private-settings", await File.ReadAllTextAsync(Path.Combine(fixture.Data, "settings.json")));
        Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.Equal(1, starter.RestoredStarts);
    }

    [Fact]
    public async Task CancellationBeforeMutationLeavesInstallationUntouched()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteOldManagedFile("ChatAgentBridge.Desktop.exe", "old-app");
        fixture.WriteOldManagedManifest("ChatAgentBridge.Desktop.exe", "portable-files.json");
        var plan = await fixture.CreatePlanAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var starter = new StubStarter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PortableUpdateApplier(starter).ApplyAsync(plan, cancellation.Token));

        Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.Equal(0, starter.ConfirmedStarts);
        Assert.Equal(0, starter.RestoredStarts);
    }

    [Fact]
    public void SharedApplicationLeaseBlocksExclusiveUpdaterLease()
    {
        using var fixture = new UpdateFixture();
        using var appLease = PortableApplicationLease.Open(fixture.Install);

        Assert.Throws<IOException>(() => PortableApplicationLease.Open(fixture.Install, exclusive: true));
    }

    [Fact]
    public void RejectsPlanPathsThatOverlapDataIgnoringPathCase()
    {
        using var fixture = new UpdateFixture(dataDirectoryName: "UserBox");
        var plan = fixture.CreatePlanWithFile("userbox/settings.bin");

        Assert.Throws<InvalidDataException>(plan.Validate);
    }

    [Fact]
    public void RejectsStagingDirectoryEqualToWorkDirectory()
    {
        using var fixture = new UpdateFixture();
        var valid = fixture.CreatePlanWithFile("new.bin");

        Assert.Throws<InvalidDataException>(() => (valid with { StagingDirectory = valid.WorkDirectory }).Validate());
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("folder\\file.bin")]
    [InlineData("Data/settings.json")]
    [InlineData("CON.txt")]
    [InlineData("folder/.cab-stage/file.bin")]
    public void RejectsInvalidPlanFilePaths(string path)
    {
        using var fixture = new UpdateFixture();

        Assert.Throws<InvalidDataException>(() => fixture.CreatePlanWithFile(path).Validate());
    }

    [Fact]
    public void RejectsReparsePointInInstallationPathWhenSupported()
    {
        using var fixture = new UpdateFixture();
        var actual = Path.Combine(fixture.Root, "actual-install");
        Directory.CreateDirectory(actual);
        var link = Path.Combine(fixture.Install, "linked-install");
        try { Directory.CreateSymbolicLink(link, actual); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => PortableUpdatePaths.EnsureNoReparsePoints(link));
        Assert.Throws<InvalidDataException>(() => PortableUpdatePaths.Resolve(fixture.Install, "linked-install/file.bin"));
    }

    private sealed class StubStarter(
        Func<string, string, bool>? confirmed = null,
        Action<string, string>? restored = null) : IPortableApplicationStarter
    {
        public int ConfirmedStarts { get; private set; }
        public int RestoredStarts { get; private set; }

        public Task<bool> StartAndConfirmAsync(string executable, string dataDirectory, string healthEventName, CancellationToken cancellationToken)
        {
            ConfirmedStarts++;
            return Task.FromResult(confirmed?.Invoke(executable, dataDirectory) ?? true);
        }

        public Task StartRestoredAsync(string executable, string dataDirectory, CancellationToken cancellationToken)
        {
            RestoredStarts++;
            restored?.Invoke(executable, dataDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly Process _parent = Process.GetCurrentProcess();
        private readonly bool _includeAddedFile;
        public string Root { get; }
        public string Install { get; }
        public string Data { get; }
        public string Work { get; }
        public string Staging { get; }

        public UpdateFixture(string dataDirectoryName = "Data", bool includeAddedFile = false)
        {
            _includeAddedFile = includeAddedFile;
            Root = Path.Combine(Path.GetTempPath(), "cab-update-applier-" + Guid.NewGuid().ToString("N"));
            Install = Path.Combine(Root, "install");
            Data = Path.Combine(Install, dataDirectoryName);
            Work = Path.Combine(Root, "work");
            Staging = Path.Combine(Work, "staging");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Staging);
        }

        public void WriteData(string relative, string value) => Write(Data, relative, value);
        public void WriteUnmanagedFile(string relative, string value) => Write(Install, relative, value);
        public void WriteOldManagedFile(string relative, string value) => Write(Install, relative, value);

        public void WriteOldManagedManifest(params string[] files) =>
            File.WriteAllText(Path.Combine(Install, "portable-files.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files }));

        public PortableUpdatePlan CreatePlanWithFile(string extraPath)
        {
            var files = BasePayload().Keys.ToDictionary(path => path, _ => new string('a', 64), StringComparer.OrdinalIgnoreCase);
            files[extraPath] = new string('a', 64);
            return NewPlan(files);
        }

        public async Task<PortableUpdatePlan> CreatePlanAsync(bool? includeAddedFile = null)
        {
            var files = BasePayload();
            if (includeAddedFile ?? _includeAddedFile) files["added/new.dll"] = Convert.ToBase64String("new-added-file"u8.ToArray());
            var manifestNames = files.Keys.ToArray();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, files = manifestNames });
            files["portable-files.json"] = Convert.ToBase64String(manifest);

            foreach (var (relative, text) in files)
            {
                var bytes = relative == "portable-files.json" ? manifest : Convert.FromBase64String(text);
                WriteBytes(Staging, relative, bytes);
            }
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in files.Keys)
                hashes[relative] = await PortableUpdatePaths.HashAsync(Path.Combine(Staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            return NewPlan(hashes);
        }

        private Dictionary<string, string> BasePayload() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["ChatAgentBridge.Desktop.exe"] = Convert.ToBase64String("new-app"u8.ToArray()),
            ["ChatAgentBridge.Updater.exe"] = Convert.ToBase64String("new-updater"u8.ToArray()),
            ["portable-release.json"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, releaseVersion = "0.1.0-preview.6", architecture = "x64" })),
            ["portable-files.json"] = string.Empty
        };

        private PortableUpdatePlan NewPlan(Dictionary<string, string> files)
        {
            var startTime = _parent.StartTime.ToUniversalTime().Ticks;
            return new PortableUpdatePlan(1, Install, Data, Staging, Work, Environment.ProcessId,
                startTime, files, "0.1.0-preview.6", "x64", Guid.NewGuid().ToString("N"));
        }

        private static void Write(string root, string relative, string value) => WriteBytes(root, relative, System.Text.Encoding.UTF8.GetBytes(value));
        private static void WriteBytes(string root, string relative, byte[] bytes)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose()
        {
            _parent.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
