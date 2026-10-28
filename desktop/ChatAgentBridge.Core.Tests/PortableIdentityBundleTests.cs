using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;

public sealed class PortableIdentityBundleTests
{
    [Fact]
    public void ValidPublicBundleReturnsOnlyFixedAbsolutePathsAndDefensiveCertificateCopy()
    {
        using var fixture = new BundleFixture();
        var bundle = PortableIdentityBundle.LoadValidated(fixture.Root);
        Assert.Equal(Path.Combine(fixture.Identity, PortableIdentityBundle.PackageFileName), bundle.PackagePath);
        Assert.Equal(Path.Combine(fixture.Identity, PortableIdentityBundle.CertificateFileName), bundle.CertificatePath);
        Assert.Equal(fixture.Certificate.RawData, bundle.PublicCertificateBytes);
        bundle.PublicCertificateBytes[0] = 0;
        Assert.Equal(fixture.Certificate.RawData, bundle.PublicCertificateBytes);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("certificate")]
    public void AlteredBundleFileFailsBeforeAnyTrustOperation(string file)
    {
        using var fixture = new BundleFixture();
        File.AppendAllText(file == "package" ? fixture.PackagePath : fixture.CertificatePath, "tampered");
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Theory]
    [InlineData("bad-json")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("null")]
    public void RejectsMalformedOrAmbiguousMetadata(string kind)
    {
        using var fixture = new BundleFixture();
        var json = File.ReadAllText(fixture.MetadataPath);
        json = kind switch
        {
            "bad-json" => "{",
            "duplicate" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "unknown" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"packagePath\":\"../other.msix\""),
            "unsupported" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            _ => json.Replace("\"applicationId\":\"App\"", "\"applicationId\":null")
        };
        File.WriteAllText(fixture.MetadataPath, json);
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Theory]
    [InlineData("wrong-subject")]
    [InlineData("ca")]
    [InlineData("wrong-eku")]
    [InlineData("multiple-eku")]
    public void RejectsCertificatesThatMustNeverBeOfferedForTrust(string kind)
    {
        using var fixture = new BundleFixture(kind);
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    public void StructuralValidationLeavesCertificateTimeAndTimestampPolicyToWindows(string kind)
    {
        using var fixture = new BundleFixture(kind);
        var bundle = PortableIdentityBundle.LoadValidated(fixture.Root);
        Assert.Equal(fixture.Certificate.RawData, bundle.PublicCertificateBytes);
    }

    [Fact]
    public void MatchingCertificateChecksumDoesNotPermitDifferentPackageSigner()
    {
        using var fixture = new BundleFixture();
        using var other = new BundleFixture();
        File.WriteAllBytes(fixture.CertificatePath, other.Certificate.RawData);
        fixture.WriteMetadata();
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("signature-tamper")]
    [InlineData("extra-executable")]
    [InlineData("traversal-entry")]
    [InlineData("duplicate-entry")]
    [InlineData("oversized-entry")]
    public void RejectsUnsafeOrUnsignedPackageContentEvenWithUpdatedMetadataHash(string kind)
    {
        using var fixture = new BundleFixture();
        fixture.WritePackage(kind: kind);
        fixture.WriteMetadata();
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Theory]
    [InlineData("mmiyaji.ChatAgentBridge", "Other.Portable")]
    [InlineData("mmiyaji.ChatAgentBridge", "ChatAgentBridge.Portable")]
    [InlineData("CN=mmiyaji", "CN=Other")]
    [InlineData("1.0.0.0", "1.0.0.1")]
    [InlineData("ProcessorArchitecture=\"neutral\"", "ProcessorArchitecture=\"x64\"")]
    [InlineData("ChatAgentBridge.Desktop.exe", "Other.exe")]
    [InlineData("----AppNotificationActivated:", "--run-other-action")]
    [InlineData("71F2D23E-C3A6-4385-B377-612FE0DA9AB1", "00000000-0000-0000-0000-000000000000")]
    [InlineData("<u10:AllowExternalContent>true", "<u10:AllowExternalContent>false")]
    [InlineData("unvirtualizedResources", "broadFileSystemAccess")]
    [InlineData("win32App", "packagedClassicApp")]
    [InlineData("Name=\"mmiyaji.ChatAgentBridge\"", "Name=\"mmiyaji.ChatAgentBridge\" ResourceId=\"other\"")]
    [InlineData("Executable=\"ChatAgentBridge.Desktop.exe\" u10:TrustLevel", "Executable=\"ChatAgentBridge.Desktop.exe\" EntryPoint=\"Other.Entry\" u10:TrustLevel")]
    public void RejectsReSignedPackageWithUnexpectedIdentityOrActivation(string before, string after)
    {
        using var fixture = new BundleFixture();
        fixture.WritePackage(BundleFixture.Manifest.Replace(before, after));
        fixture.WriteMetadata();
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Fact]
    public void RejectsXmlExternalEntitiesAndMultipleApplications()
    {
        using var fixture = new BundleFixture();
        foreach (var manifest in new[]
        {
            "<!DOCTYPE Package [<!ENTITY x SYSTEM 'file:///never-read'>]>" + BundleFixture.Manifest.Replace("<DisplayName>Bridge", "<DisplayName>&x;"),
            BundleFixture.Manifest.Replace("</Applications>", "<Application Id=\"Other\"/></Applications>")
        })
        {
            fixture.WritePackage(manifest);
            fixture.WriteMetadata();
            Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
        }
    }

    [Fact]
    public void OversizedMetadataAndRelativeInstallRootAreRejected()
    {
        using var fixture = new BundleFixture();
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated("relative-install"));
        File.WriteAllText(fixture.MetadataPath, new string(' ', 16 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(fixture.Root));
    }

    [Fact]
    public void ReparsePointIdentityAncestorIsRejected()
    {
        using var fixture = new BundleFixture();
        var link = fixture.Root + "-link";
        try
        {
            try { Directory.CreateSymbolicLink(link, fixture.Root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            Assert.Throws<InvalidDataException>(() => PortableIdentityBundle.LoadValidated(link));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    internal sealed class BundleFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cab-identity-tests-" + Guid.NewGuid().ToString("N"));
        public string Identity => Path.Combine(Root, "Identity");
        public string MetadataPath => Path.Combine(Identity, PortableIdentityBundle.MetadataFileName);
        public string CertificatePath => Path.Combine(Identity, PortableIdentityBundle.CertificateFileName);
        public string PackagePath => Path.Combine(Identity, PortableIdentityBundle.PackageFileName);
        public X509Certificate2 Certificate { get; }
        private readonly RSA _key = RSA.Create(2048);

        public BundleFixture(string certificateKind = "valid")
        {
            Directory.CreateDirectory(Identity);
            var request = new CertificateRequest(certificateKind == "wrong-subject" ? "CN=Other" : "CN=mmiyaji", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateKind == "ca", false, 0, true));
            var purposes = new OidCollection { new(certificateKind == "wrong-eku" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.3") };
            if (certificateKind == "multiple-eku") purposes.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(certificateKind == "not-yet-valid" ? 1 : -2), DateTimeOffset.UtcNow.AddDays(certificateKind == "expired" ? -1 : 30));
            File.WriteAllBytes(CertificatePath, Certificate.RawData);
            WritePackage();
            WriteMetadata();
        }

        public void WriteMetadata() => File.WriteAllText(MetadataPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, packageName = PortableIdentityBundle.PackageName, publisher = PortableIdentityBundle.Publisher,
            applicationId = PortableIdentityBundle.ApplicationId, packageVersion = PortableIdentityBundle.PackageVersion,
            certificateSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CertificatePath))),
            packageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(PackagePath)))
        }));

        public void WritePackage(string? manifest = null, string kind = "valid")
        {
            using var output = File.Create(PackagePath);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            Add(zip, "AppxManifest.xml", Encoding.UTF8.GetBytes(manifest ?? Manifest));
            Add(zip, "AppxBlockMap.xml", "<BlockMap/>"u8.ToArray());
            Add(zip, "[Content_Types].xml", "<Types/>"u8.ToArray());
            if (kind != "unsigned")
            {
                var cms = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), [0x30, 0x00]));
                cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
                var signature = "PKCX"u8.ToArray().Concat(cms.Encode()).ToArray();
                if (kind == "signature-tamper") signature[^1] ^= 0xff;
                Add(zip, "AppxSignature.p7x", signature);
            }
            if (kind == "extra-executable") Add(zip, "unexpected.exe", [0x4d, 0x5a]);
            if (kind == "traversal-entry") Add(zip, "../AppxManifest.xml", [1]);
            if (kind == "duplicate-entry") Add(zip, "AppxManifest.xml", [1]);
            if (kind == "oversized-entry") Add(zip, "AppxManifest.xml", new byte[512 * 1024 + 1]);
        }

        private static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            using var output = zip.CreateEntry(name).Open();
            output.Write(bytes);
        }

        public void Dispose()
        {
            Certificate.Dispose();
            _key.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        public const string Manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:u10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
              xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Identity Name="mmiyaji.ChatAgentBridge" Publisher="CN=mmiyaji" Version="1.0.0.0" ProcessorArchitecture="neutral"/>
              <Properties><DisplayName>Bridge</DisplayName><u10:AllowExternalContent>true</u10:AllowExternalContent></Properties>
              <Resources><Resource Language="en-US"/></Resources>
              <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0"/></Dependencies>
              <Capabilities><rescap:Capability Name="runFullTrust"/><rescap:Capability Name="unvirtualizedResources"/></Capabilities>
              <Applications><Application Id="App" Executable="ChatAgentBridge.Desktop.exe" u10:TrustLevel="mediumIL" u10:RuntimeBehavior="win32App">
                <uap:VisualElements DisplayName="Bridge"/>
                <Extensions>
                  <desktop:Extension Category="windows.toastNotificationActivation"><desktop:ToastNotificationActivation ToastActivatorCLSID="71F2D23E-C3A6-4385-B377-612FE0DA9AB1"/></desktop:Extension>
                  <com:Extension Category="windows.comServer"><com:ComServer><com:ExeServer Executable="ChatAgentBridge.Desktop.exe" Arguments="----AppNotificationActivated:"><com:Class Id="71F2D23E-C3A6-4385-B377-612FE0DA9AB1"/></com:ExeServer></com:ComServer></com:Extension>
                </Extensions>
              </Application></Applications>
            </Package>
            """;
    }
}
