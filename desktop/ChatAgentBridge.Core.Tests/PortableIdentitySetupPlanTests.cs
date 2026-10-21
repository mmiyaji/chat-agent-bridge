using System.Text.Json;
using ChatAgentBridge.Core.Deployment;

public sealed class PortableIdentitySetupPlanTests
{
    [Fact]
    public async Task ReadsConfirmedPlanOnlyFromItsNonceScopedWorkDirectory()
    {
        using var fixture = new PlanFixture();
        var path = Path.Combine(fixture.Plan.WorkDirectory, PortableIdentitySetupPlan.FileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture.Plan));
        var actual = await PortableIdentitySetupPlan.ReadAsync(path);
        Assert.Equal(fixture.Plan, actual);
        Assert.Equal(Path.Combine(actual.WorkDirectory, "ready-" + actual.Nonce), actual.ReadyFile);
        Assert.Equal(Path.Combine(actual.WorkDirectory, "commit-" + actual.Nonce), actual.CommitFile);
        var misplaced = Path.Combine(fixture.Plan.WorkDirectory, "other-plan.json");
        File.Copy(path, misplaced);
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableIdentitySetupPlan.ReadAsync(misplaced));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-nonce")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    [InlineData("000000000000000000000000000000000")]
    public void RejectsMissingMalformedOrMismatchedNonce(string nonce)
    {
        using var fixture = new PlanFixture();
        Assert.Throws<InvalidDataException>(() => (fixture.Plan with { Nonce = nonce }).Validate());
        Assert.Throws<InvalidDataException>(() => (fixture.Plan with { Nonce = Guid.NewGuid().ToString("N") }).Validate());
    }

    [Theory]
    [InlineData("install-relative")]
    [InlineData("install-root")]
    [InlineData("install-noncanonical")]
    [InlineData("data-relative")]
    [InlineData("data-contains-install")]
    [InlineData("work-overlaps-install")]
    [InlineData("work-overlaps-data")]
    [InlineData("unscoped-work")]
    public void RejectsUnsafePathsAndDirectoryRelationships(string kind)
    {
        using var fixture = new PlanFixture();
        var plan = kind switch
        {
            "install-relative" => fixture.Plan with { InstallDirectory = "relative" },
            "install-root" => fixture.Plan with { InstallDirectory = Path.GetPathRoot(fixture.Plan.InstallDirectory)! },
            "install-noncanonical" => fixture.Plan with { InstallDirectory = Path.Combine(fixture.Plan.InstallDirectory, "..", "other") },
            "data-relative" => fixture.Plan with { DataDirectory = "relative" },
            "data-contains-install" => fixture.Plan with { DataDirectory = Path.GetDirectoryName(fixture.Plan.InstallDirectory)! },
            "work-overlaps-install" => fixture.Plan with { InstallDirectory = fixture.Plan.WorkDirectory },
            "work-overlaps-data" => fixture.Plan with { DataDirectory = fixture.Plan.WorkDirectory },
            _ => fixture.Plan with { WorkDirectory = Path.Combine(Path.GetTempPath(), "unscoped-identity-work") }
        };
        Assert.Throws<InvalidDataException>(() => plan.Validate());
    }

    [Theory]
    [InlineData("unsupported-schema")]
    [InlineData("parent-pid")]
    [InlineData("parent-start")]
    [InlineData("operation")]
    [InlineData("package-hash")]
    [InlineData("certificate-hash")]
    [InlineData("unregister-trust")]
    [InlineData("unregister-relocate")]
    public void RejectsInvalidAuthorizationSnapshot(string kind)
    {
        using var fixture = new PlanFixture();
        var plan = kind switch
        {
            "unsupported-schema" => fixture.Plan with { SchemaVersion = 2 },
            "parent-pid" => fixture.Plan with { ParentProcessId = 0 },
            "parent-start" => fixture.Plan with { ParentStartTimeUtcTicks = 0 },
            "operation" => fixture.Plan with { Operation = "register-all-users" },
            "package-hash" => fixture.Plan with { PackageSha256 = "invalid" },
            "certificate-hash" => fixture.Plan with { CertificateSha256 = new string('g', 64) },
            "unregister-trust" => fixture.Plan with { Operation = "unregister", TrustCertificate = true },
            _ => fixture.Plan with { Operation = "unregister", AllowRelocate = true }
        };
        Assert.Throws<InvalidDataException>(() => plan.Validate());
    }

    [Fact]
    public async Task RejectsOversizedAndMalformedPlanBeforeSetup()
    {
        using var fixture = new PlanFixture();
        var path = Path.Combine(fixture.Plan.WorkDirectory, PortableIdentitySetupPlan.FileName);
        await File.WriteAllTextAsync(path, new string(' ', 32769));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableIdentitySetupPlan.ReadAsync(path));
        await File.WriteAllTextAsync(path, "{");
        await Assert.ThrowsAsync<JsonException>(() => PortableIdentitySetupPlan.ReadAsync(path));
    }

    [Fact]
    public void RejectsBundleReplacementAfterUserConfirmedItsHashes()
    {
        using var fixture = new PlanFixture();
        using var bundleFixture = new PortableIdentityBundleTests.BundleFixture();
        var bundle = PortableIdentityBundle.LoadValidated(bundleFixture.Root);
        var confirmed = fixture.Plan with { PackageSha256 = bundle.PackageSha256, CertificateSha256 = bundle.CertificateSha256 };
        confirmed.ValidateBundle(bundle);
        Assert.Throws<InvalidDataException>(() => (confirmed with { PackageSha256 = new string('0', 64) }).ValidateBundle(bundle));
        Assert.Throws<InvalidDataException>(() => (confirmed with { CertificateSha256 = new string('0', 64) }).ValidateBundle(bundle));
        using var replacement = new PortableIdentityBundleTests.BundleFixture();
        Assert.Throws<InvalidDataException>(() => confirmed.ValidateBundle(PortableIdentityBundle.LoadValidated(replacement.Root)));
    }

    private sealed class PlanFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-identity-plan-tests-" + Guid.NewGuid().ToString("N"));
        public PortableIdentitySetupPlan Plan { get; }

        public PlanFixture()
        {
            var nonce = Guid.NewGuid().ToString("N");
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatAgentBridge", "notification-setup", nonce);
            Plan = new(1, Path.Combine(_root, "install"), Path.Combine(_root, "data"), work,
                123, DateTime.UtcNow.Ticks, nonce, "register", new string('a', 64), new string('b', 64), false, false);
            Directory.CreateDirectory(Plan.InstallDirectory);
            Directory.CreateDirectory(Plan.DataDirectory);
            Directory.CreateDirectory(work);
        }

        public void Dispose()
        {
            Directory.Delete(Plan.WorkDirectory, recursive: true);
            Directory.Delete(_root, recursive: true);
        }
    }
}
