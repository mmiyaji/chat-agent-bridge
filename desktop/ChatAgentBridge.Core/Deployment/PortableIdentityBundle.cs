using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ChatAgentBridge.Core.Updates;

namespace ChatAgentBridge.Core.Deployment;

/// <summary>Validates the narrowly scoped public identity bundle before deployment or certificate trust.</summary>
/// <remarks>Windows deployment verifies payload binding, certificate time validity, timestamps and trust policy.</remarks>
public sealed class PortableIdentityBundle
{
    public const string PackageName = "ChatAgentBridge.Portable";
    public const string Publisher = "CN=mmiyaji";
    public const string ApplicationId = "App";
    public const string PackageVersion = "1.0.0.0";
    public const string MetadataFileName = "portable-identity.json";
    public const string PackageFileName = "ChatAgentBridge.Portable.identity.msix";
    public const string CertificateFileName = "ChatAgentBridge.Portable.cer";
    public const string ToastActivatorClsid = "71F2D23E-C3A6-4385-B377-612FE0DA9AB1";
    private const string Executable = "ChatAgentBridge.Desktop.exe";
    private readonly byte[] _certificateBytes;

    public string MetadataPath { get; }
    public string PackagePath { get; }
    public string CertificatePath { get; }
    public string CertificateSha256 { get; }
    public string PackageSha256 { get; }
    public byte[] PublicCertificateBytes => (byte[])_certificateBytes.Clone();

    private PortableIdentityBundle(string metadata, string package, string certificate, byte[] certificateBytes, string certificateHash, string packageHash)
    {
        MetadataPath = metadata;
        PackagePath = package;
        CertificatePath = certificate;
        _certificateBytes = certificateBytes;
        CertificateSha256 = certificateHash;
        PackageSha256 = packageHash;
    }

    public static PortableIdentityBundle LoadValidated(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        if (!Path.IsPathFullyQualified(installDirectory)) throw Invalid("The install directory must be absolute.");
        var root = Path.GetFullPath(installDirectory);
        PortableUpdatePaths.EnsureNoReparsePoints(root);
        var identity = PortableUpdatePaths.Resolve(root, "Identity");
        var metadataPath = PortableUpdatePaths.Resolve(identity, MetadataFileName);
        var packagePath = PortableUpdatePaths.Resolve(identity, PackageFileName);
        var certificatePath = PortableUpdatePaths.Resolve(identity, CertificateFileName);
        try
        {
            using var metadata = JsonDocument.Parse(ReadBounded(metadataPath, 16 * 1024), new JsonDocumentOptions { MaxDepth = 4 });
            var json = metadata.RootElement;
            Require(json.ValueKind == JsonValueKind.Object, "Identity metadata must be an object.");
            string[] fields = ["schemaVersion", "packageName", "publisher", "applicationId", "packageVersion", "certificateSha256", "packageSha256"];
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in json.EnumerateObject())
                Require(fields.Contains(field.Name, StringComparer.Ordinal) && names.Add(field.Name), "Identity metadata contains an unexpected or duplicate field.");
            Require(names.Count == fields.Length, "Identity metadata is incomplete.");
            Require(json.GetProperty("schemaVersion").TryGetInt32(out var schema) && schema == 1, "Unsupported identity metadata schema.");
            Require(Text(json, "packageName") == PackageName && Text(json, "publisher") == Publisher &&
                Text(json, "applicationId") == ApplicationId && Text(json, "packageVersion") == PackageVersion, "Unexpected portable package identity.");
            var certificateHash = HashText(json, "certificateSha256");
            var packageHash = HashText(json, "packageSha256");
            var certificateBytes = ReadBounded(certificatePath, 64 * 1024);
            var packageBytes = ReadBounded(packagePath, 16 * 1024 * 1024);
            Require(Hash(certificateBytes) == certificateHash && Hash(packageBytes) == packageHash, "Identity bundle checksum mismatch.");
            using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            ValidateCertificate(certificate, certificateBytes);
            ValidatePackage(packageBytes, certificateBytes);
            return new(metadataPath, packagePath, certificatePath, certificateBytes, certificateHash, packageHash);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or CryptographicException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The portable identity bundle is invalid.", ex);
        }
    }

    private static void ValidateCertificate(X509Certificate2 certificate, byte[] raw)
    {
        Require(!certificate.HasPrivateKey && certificate.RawData.AsSpan().SequenceEqual(raw), "Only a public DER certificate is accepted.");
        Require(certificate.Subject == Publisher, "Unexpected identity certificate subject.");
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        Require(basic.Length == 1 && !basic[0].CertificateAuthority, "The identity certificate must be an explicit non-CA leaf.");
        Require(eku.Length == 1 && eku[0].EnhancedKeyUsages.Count == 1 && eku[0].EnhancedKeyUsages[0].Value == "1.3.6.1.5.5.7.3.3", "The identity certificate must permit only code signing.");
    }

    private static void ValidatePackage(byte[] bytes, byte[] certificate)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        string[] allowed = ["AppxManifest.xml", "AppxBlockMap.xml", "[Content_Types].xml", "AppxSignature.p7x"];
        Require(archive.Entries.Count == allowed.Length, "The identity package must contain only signed manifest packaging files.");
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            Require(allowed.Contains(entry.FullName, StringComparer.Ordinal) && !entries.ContainsKey(entry.FullName), "Unexpected or duplicate identity package entry.");
            Require(entry.Length is > 0 and <= 512 * 1024, "An identity package entry exceeds its size limit.");
            using var input = entry.Open();
            entries.Add(entry.FullName, ReadBounded(input, 512 * 1024));
        }
        ValidateManifest(entries["AppxManifest.xml"]);
        var signature = entries["AppxSignature.p7x"];
        Require(signature.Length > 4 && signature.AsSpan(0, 4).SequenceEqual("PKCX"u8), "The identity package has no valid signature header.");
        var cms = new SignedCms();
        cms.Decode(signature.AsSpan(4).ToArray());
        Require(!cms.Detached && cms.ContentInfo.ContentType.Value == "1.3.6.1.4.1.311.2.1.4" && cms.SignerInfos.Count == 1, "Unexpected identity package signature format.");
        cms.CheckSignature(verifySignatureOnly: true);
        Require(cms.SignerInfos[0].Certificate is { } signer && signer.RawData.AsSpan().SequenceEqual(certificate), "The package signer does not match the bundled public certificate.");
    }

    private static void ValidateManifest(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 512 * 1024 });
        var document = XDocument.Load(reader);
        XNamespace f = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace u10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XNamespace com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        var root = document.Root;
        Require(root?.Name == f + "Package", "Unexpected identity manifest root.");
        XName[] rootNames = [f + "Identity", f + "Properties", f + "Resources", f + "Dependencies", f + "Capabilities", f + "Applications"];
        Require(root!.Elements().All(e => rootNames.Contains(e.Name)) && root.Elements().Count() == rootNames.Length, "Unexpected identity manifest structure.");
        var id = One(root, f + "Identity");
        AttributesOnly(id, "Name", "Publisher", "Version", "ProcessorArchitecture");
        Require(Attr(id, "Name") == PackageName && Attr(id, "Publisher") == Publisher && Attr(id, "Version") == PackageVersion && Attr(id, "ProcessorArchitecture") == "neutral", "The manifest identity does not match the portable application.");
        Require(One(One(root, f + "Properties"), u10 + "AllowExternalContent").Value == "true", "The identity package must allow external content.");
        var dependencies = One(root, f + "Dependencies");
        Require(dependencies.Elements().Count() == 1 && Attr(One(dependencies, f + "TargetDeviceFamily"), "Name") == "Windows.Desktop", "Unexpected identity package dependencies.");
        var capabilities = One(root, f + "Capabilities").Elements().ToArray();
        Require(capabilities.Length == 2 && capabilities.All(e => e.Name == rescap + "Capability") &&
            capabilities.Select(e => Attr(e, "Name")).Order().SequenceEqual(new[] { "runFullTrust", "unvirtualizedResources" }), "Unexpected identity package capabilities.");
        var apps = One(root, f + "Applications");
        Require(apps.Elements().Count() == 1, "The identity package must declare exactly one application.");
        var app = One(apps, f + "Application");
        AttributesOnly(app, "Id", "Executable", u10 + "TrustLevel", u10 + "RuntimeBehavior");
        Require(Attr(app, "Id") == ApplicationId && Attr(app, "Executable") == Executable && Attr(app, u10 + "TrustLevel") == "mediumIL" && Attr(app, u10 + "RuntimeBehavior") == "win32App", "Unexpected external application activation.");
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        Require(app.Elements().Count() == 2 && app.Elements().All(e => e.Name == uap + "VisualElements" || e.Name == f + "Extensions"), "Unexpected application manifest content.");
        var extensions = One(app, f + "Extensions");
        Require(extensions.Elements().Count() == 2, "Unexpected application extensions.");
        var toast = One(extensions, desktop + "Extension");
        AttributesOnly(toast, "Category");
        Require(Attr(toast, "Category") == "windows.toastNotificationActivation" && toast.Elements().Count() == 1 && Attr(One(toast, desktop + "ToastNotificationActivation"), "ToastActivatorCLSID") == ToastActivatorClsid, "Unexpected toast activator.");
        AttributesOnly(One(toast, desktop + "ToastNotificationActivation"), "ToastActivatorCLSID");
        var comExtension = One(extensions, com + "Extension");
        AttributesOnly(comExtension, "Category");
        Require(Attr(comExtension, "Category") == "windows.comServer" && comExtension.Elements().Count() == 1, "Unexpected COM activation.");
        var server = One(comExtension, com + "ComServer");
        Require(server.Elements().Count() == 1, "Unexpected COM server count.");
        var executable = One(server, com + "ExeServer");
        AttributesOnly(executable, "Executable", "Arguments", "DisplayName");
        Require(Attr(executable, "Executable") == Executable && Attr(executable, "Arguments") == "----AppNotificationActivated:" && executable.Elements().Count() == 1 && Attr(One(executable, com + "Class"), "Id") == ToastActivatorClsid, "Unexpected COM notification executable or arguments.");
        AttributesOnly(One(executable, com + "Class"), "Id");
    }

    private static XElement One(XElement parent, XName name)
    {
        var elements = parent.Elements(name).ToArray();
        Require(elements.Length == 1, "A required manifest element is missing or duplicated.");
        return elements[0];
    }
    private static string? Attr(XElement element, XName name) => (string?)element.Attribute(name);
    private static void AttributesOnly(XElement element, params XName[] names) =>
        Require(element.Attributes().All(a => a.IsNamespaceDeclaration || names.Contains(a.Name)), "Unexpected identity or activation attribute.");
    private static string Text(JsonElement json, string name) => json.GetProperty(name).GetString() ?? throw Invalid("Identity metadata contains a null value.");
    private static string HashText(JsonElement json, string name)
    {
        var value = Text(json, name);
        Require(value.Length == 64 && value.All(Uri.IsHexDigit), "Invalid identity bundle checksum.");
        return value.ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ReadBounded(string path, int maximum)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length is > 0 && stream.Length <= maximum, "An identity bundle file exceeds its size limit.");
        return ReadBounded(stream, maximum);
    }
    private static byte[] ReadBounded(Stream stream, int maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            Require(output.Length + count <= maximum, "Identity bundle content exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static InvalidDataException Invalid(string message) => new(message);
    private static void Require(bool condition, string message) { if (!condition) throw Invalid(message); }
}
