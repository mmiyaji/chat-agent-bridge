using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;
using ChatAgentBridge.Core.Updates;

public sealed class PortableIdentityUpdateTests
{
    [Fact]
    public async Task SignedPublicIdentityBundleIsStagedAndManagedWithOtherUpdateFiles()
    {
        using var fixture = new UpdateFixture();
        var staged = await fixture.StageAsync(fixture.Payload(identity: true));
        Assert.Contains("Identity/" + PortableIdentityBundle.CertificateFileName, staged.Files.Keys);
        Assert.Contains("Identity/" + PortableIdentityBundle.PackageFileName, staged.Files.Keys);
        PortableIdentityBundle.LoadValidated(staged.DirectoryPath);
        var starter = new Starter();
        var result = await new PortableUpdateApplier(starter).ApplyAsync(fixture.Plan(staged));
        Assert.True(result.Success);
        Assert.Equal(1, starter.Starts);
        Assert.Equal("private-settings", File.ReadAllText(Path.Combine(fixture.Data, "settings.json")));
        Assert.Equal(fixture.SourceBundle.PackageSha256, PortableIdentityBundle.LoadValidated(fixture.Install).PackageSha256);
    }

    [Fact]
    public async Task OlderArchiveWithoutIdentityRemainsValid()
    {
        using var fixture = new UpdateFixture();
        var staged = await fixture.StageAsync(fixture.Payload(identity: false));
        Assert.DoesNotContain(staged.Files.Keys, p => p.StartsWith("Identity/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Identity/portable-identity.json")]
    [InlineData("Identity/ChatAgentBridge.Portable.identity.msix")]
    [InlineData("Identity/ChatAgentBridge.Portable.cer")]
    public async Task PartialIdentityBundleIsRejectedBeforeStagingDestinationExists(string missing)
    {
        using var fixture = new UpdateFixture();
        var payload = fixture.Payload(identity: true);
        payload.Remove(missing);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(payload));
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Theory]
    [InlineData("Identity/extra.txt")]
    [InlineData("Identity/private.pfx")]
    [InlineData("Identity/Other.cer")]
    [InlineData("Identity/Other.msix")]
    [InlineData("Other/ChatAgentBridge.Portable.cer")]
    [InlineData("Other/ChatAgentBridge.Portable.identity.msix")]
    [InlineData("Other/installer.appx")]
    public async Task IdentityExceptionDoesNotAllowOtherCertificatesInstallersOrIdentityFiles(string unexpected)
    {
        using var fixture = new UpdateFixture();
        var payload = fixture.Payload(identity: true);
        payload[unexpected] = [1, 2, 3];
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(payload));
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Fact]
    public async Task PublicCertificateTamperingIsRejectedDespiteMatchingOuterArchiveChecksums()
    {
        using var fixture = new UpdateFixture();
        var payload = fixture.Payload(identity: true);
        payload["Identity/" + PortableIdentityBundle.CertificateFileName] = [1, 2, 3];
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(payload));
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Fact]
    public async Task ArchiveCannotStripExistingIdentityBeforeAnyInstallMutation()
    {
        using var fixture = new UpdateFixture();
        var before = File.ReadAllBytes(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe"));
        var staged = await fixture.StageAsync(fixture.Payload(identity: false));
        var plan = fixture.Plan(staged);
        var starter = new Starter();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PortableUpdateApplier(starter).ApplyAsync(plan));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Install, "ChatAgentBridge.Desktop.exe")));
        Assert.Equal("private-settings", File.ReadAllText(Path.Combine(fixture.Data, "settings.json")));
        Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.Equal(0, starter.Starts);
        Assert.Equal(fixture.SourceBundle.PackageSha256, PortableIdentityBundle.LoadValidated(fixture.Install).PackageSha256);
    }

    private sealed class Starter : IPortableApplicationStarter
    {
        public int Starts { get; private set; }
        public Task<bool> StartAndConfirmAsync(string executable, string dataDirectory, string healthEventName, CancellationToken cancellationToken)
        { Starts++; return Task.FromResult(true); }
        public Task StartRestoredAsync(string executable, string dataDirectory, CancellationToken cancellationToken)
        { Starts++; return Task.CompletedTask; }
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly PortableIdentityBundleTests.BundleFixture _bundle = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-identity-update-tests-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(_root, "install");
        public string Data => Path.Combine(Install, "Data");
        public string Work => Path.Combine(_root, "work");
        public string Staging => Path.Combine(Work, "staging");
        public PortableIdentityBundle SourceBundle => PortableIdentityBundle.LoadValidated(_bundle.Root);

        public UpdateFixture()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Path.Combine(Install, "Identity"));
            File.WriteAllText(Path.Combine(Data, "settings.json"), "private-settings");
            File.WriteAllBytes(Path.Combine(Install, "ChatAgentBridge.Desktop.exe"), "old-app"u8.ToArray());
            foreach (var file in Directory.GetFiles(_bundle.Identity)) File.Copy(file, Path.Combine(Install, "Identity", Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(Install, "portable-files.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, files = new[] { "ChatAgentBridge.Desktop.exe", "Identity/portable-identity.json", "Identity/ChatAgentBridge.Portable.identity.msix", "Identity/ChatAgentBridge.Portable.cer" }
            }));
        }

        public Dictionary<string, byte[]> Payload(bool identity)
        {
            var pe = new byte[128]; pe[0] = (byte)'M'; pe[1] = (byte)'Z';
            BitConverter.GetBytes(64).CopyTo(pe, 0x3c); BitConverter.GetBytes(0x00004550u).CopyTo(pe, 64); BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 68);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ChatAgentBridge.Desktop.exe"] = pe, ["ChatAgentBridge.Desktop.dll"] = [1], ["ChatAgentBridge.Desktop.runtimeconfig.json"] = "{}"u8.ToArray(),
                ["ChatAgentBridge.Updater.exe"] = pe, ["portable-release.json"] = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, releaseVersion = "0.1.0-preview.6", architecture = "x64" })
            };
            if (identity)
                foreach (var file in Directory.GetFiles(_bundle.Identity)) result["Identity/" + Path.GetFileName(file)] = File.ReadAllBytes(file);
            return result;
        }

        public async Task<StagedPortableRelease> StageAsync(Dictionary<string, byte[]> files)
        {
            files["portable-files.json"] = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, files = files.Keys.Append("portable-files.json").Append("SHA256SUMS.txt").ToArray() });
            files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\n", files.Select(f => $"{Convert.ToHexStringLower(SHA256.HashData(f.Value))} *{f.Key}")) + "\n");
            var archive = Path.Combine(_root, "update.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    using var output = zip.CreateEntry("ChatAgentBridge.Portable/" + file.Key).Open();
                    output.Write(file.Value);
                }
            var name = "ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip";
            var release = new PortableRelease("0.1.0-preview.6", "v0.1.0-preview.6", name,
                new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/download/v0.1.0-preview.6/" + name), new string('a', 64), 1,
                new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6"));
            return await PortableArchiveStager.ExtractAsync(archive, Staging, release, default);
        }

        public PortableUpdatePlan Plan(StagedPortableRelease staged)
        {
            using var parent = Process.GetCurrentProcess();
            return new(1, Install, Data, Staging, Work, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
                staged.Files.ToDictionary(p => p.Key, p => p.Value), "0.1.0-preview.6", "x64", Guid.NewGuid().ToString("N"));
        }

        public void Dispose() { _bundle.Dispose(); Directory.Delete(_root, recursive: true); }
    }
}
