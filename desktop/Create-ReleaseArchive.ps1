[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Certificate,
    [string]$PublicCertificate,
    [securestring]$CertificatePassword = (ConvertTo-SecureString 'password' -AsPlainText -Force),
    [string]$ReleaseVersion = '0.1.0-preview.1',
    [string]$PackageVersion = '0.1.0.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/releases'),
    [string]$TimestampUrl,
    [string]$ExistingPackage,
    [switch]$ValidateOnly,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'

$trustVerifierSource = @'
using System;
using System.Runtime.InteropServices;
namespace CabPreviewTrustV1 {
    public static class TrustVerifier {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA trustData);
        public static uint VerifyFile(string path) {
            IntPtr pathPtr = Marshal.StringToCoTaskMemUni(path);
            IntPtr fileInfoPtr = IntPtr.Zero;
            try {
                WINTRUST_FILE_INFO fileInfo = new WINTRUST_FILE_INFO();
                fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
                fileInfo.pcwszFilePath = pathPtr;
                fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                WINTRUST_DATA data = new WINTRUST_DATA();
                data.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                data.dwUIChoice = 2; // WTD_UI_NONE
                data.fdwRevocationChecks = 0; // WTD_REVOKE_NONE
                data.dwUnionChoice = 1; // WTD_CHOICE_FILE
                data.pFile = fileInfoPtr;
                data.dwStateAction = 0; // WTD_STATEACTION_IGNORE
                data.dwProvFlags = 0x00001000; // WTD_CACHE_ONLY_URL_RETRIEVAL; do not initiate network access
                Guid actionId = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                return unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref actionId, ref data));
            } finally {
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }
}
'@

function Ensure-TrustVerifierType {
    if (-not ('CabPreviewTrustV1.TrustVerifier' -as [type])) { Add-Type -TypeDefinition $trustVerifierSource -Language CSharp }
}

function Get-CertificateFromPfx([string]$Path, [securestring]$Password) {
    $data = Get-PfxData -FilePath $Path -Password $Password
    if ($data.EndEntityCertificates.Count -ne 1) { throw 'The PFX must contain exactly one end-entity signing certificate.' }
    return $data.EndEntityCertificates[0]
}

function Remove-DirectoryUnderRoot([string]$Path, [string]$Parent) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recursively remove a path outside its intended parent: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) { Remove-Item -LiteralPath $resolvedPath -Recurse -Force }
}

function Get-PackageManifest([string]$PackagePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw 'MSIX does not contain AppxManifest.xml at its root.' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return $xml.SelectSingleNode("//*[local-name()='Identity']")
    } finally { $zip.Dispose() }
}

function Assert-PackageSignature([string]$PackagePath, [string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if (-not $signature.SignerCertificate) { throw 'MSIX has no readable Authenticode signer certificate.' }
    if ($signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw 'MSIX signer thumbprint does not match the supplied public certificate.' }
    Ensure-TrustVerifierType
    $trustResult = [CabPreviewTrustV1.TrustVerifier]::VerifyFile([IO.Path]::GetFullPath($PackagePath))
    $untrustedRootHresult = [Convert]::ToUInt32('800B0109', 16)
    if ($trustResult -ne 0 -and $trustResult -ne $untrustedRootHresult) {
        throw ("WinVerifyTrust rejected the MSIX signature with HRESULT 0x{0:X8} (PowerShell status: {1}; {2})." -f $trustResult, $signature.Status, $signature.StatusMessage)
    }
}

function Assert-ReleaseArchive([string]$ArchivePath, [string]$ExpectedThumbprint, [string]$ExpectedPublisher, [string]$ExpectedPackageVersion, [string]$ExpectedArchitecture) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $required = @('ChatAgentBridge.WinUI.Preview.msix', 'ChatAgentBridge.WinUI.Preview.cer', 'README.ja.txt', 'README.en.txt', 'Install.ps1', 'SHA256SUMS.txt')
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $temporary = Join-Path ([IO.Path]::GetTempPath()) "cab-release-validate-$([Guid]::NewGuid().ToString('N'))"
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($names.Count -ne $required.Count -or @($required | Where-Object { $names -notcontains $_ }).Count -gt 0) {
            throw "Archive contents are unexpected. Required at root: $($required -join ', ')"
        }
        New-Item -ItemType Directory -Path $temporary | Out-Null
        foreach ($name in $required) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($name), (Join-Path $temporary $name), $true)
        }
        $checksumLines = Get-Content -LiteralPath (Join-Path $temporary 'SHA256SUMS.txt')
        foreach ($name in $required | Where-Object { $_ -ne 'SHA256SUMS.txt' }) {
            $line = $checksumLines | Where-Object { $_ -match ('^([0-9a-fA-F]{64}) \*?' + [regex]::Escape($name) + '$') } | Select-Object -First 1
            if (-not $line) { throw "SHA256SUMS.txt has no checksum for $name." }
            $expectedHash = ($line -split '\s+', 2)[0]
            $actualHash = (Get-FileHash -LiteralPath (Join-Path $temporary $name) -Algorithm SHA256).Hash
            if ($actualHash -ne $expectedHash) { throw "Checksum mismatch: $name" }
        }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $temporary 'ChatAgentBridge.WinUI.Preview.cer'))
        if ($certificate.Thumbprint -ne $ExpectedThumbprint) { throw 'Public CER thumbprint does not match the pinned release thumbprint.' }
        if ($certificate.Subject -ne $ExpectedPublisher) { throw 'Public CER subject does not match the MSIX publisher.' }
        $packagePath = Join-Path $temporary 'ChatAgentBridge.WinUI.Preview.msix'
        Assert-PackageSignature $packagePath $ExpectedThumbprint
        $identity = Get-PackageManifest $packagePath
        if (-not $identity -or $identity.GetAttribute('Name') -ne 'ChatAgentBridge.WinUI.Preview') { throw 'MSIX does not have the stable preview package identity.' }
        if ($identity.GetAttribute('Publisher') -ne $ExpectedPublisher) { throw 'MSIX publisher does not match the supplied public certificate.' }
        if ($identity.GetAttribute('Version') -ne $ExpectedPackageVersion) { throw "MSIX version does not match the requested package version $ExpectedPackageVersion." }
        $expectedProcessorArchitecture = $ExpectedArchitecture.ToLowerInvariant()
        if ($identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant() -ne $expectedProcessorArchitecture) { throw "MSIX processor architecture does not match $ExpectedArchitecture." }
        $msix = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $msixNames = @($msix.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
                if ($msixNames -notcontains $runtimeFile) { throw "Self-contained package is missing required runtime file: $runtimeFile" }
            }
            if ($msix.GetEntry('AppxManifest.xml')) {
                $manifestEntry = $msix.GetEntry('AppxManifest.xml')
                $reader = New-Object IO.StreamReader($manifestEntry.Open())
                try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($manifest.SelectSingleNode("//*[local-name()='PackageDependency']")) { throw 'Self-contained package unexpectedly declares a framework package dependency.' }
            }
        } finally { $msix.Dispose() }
        Write-Output "Valid release archive: $ArchivePath"
        Write-Output "Identity: $($identity.GetAttribute('Name')) $($identity.GetAttribute('Version'))"
        Write-Output "Signer thumbprint: $ExpectedThumbprint"
    } finally {
        $zip.Dispose()
        Remove-DirectoryUnderRoot $temporary ([IO.Path]::GetTempPath())
    }
}

if ($ValidateOnly) {
    if (-not $ArchivePath) { throw '-ArchivePath is required with -ValidateOnly.' }
    $resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
    $tempCer = Join-Path ([IO.Path]::GetTempPath()) "cab-preview-cert-$([Guid]::NewGuid().ToString('N')).cer"
    try {
        $cerEntry = $zip.GetEntry('ChatAgentBridge.WinUI.Preview.cer')
        $installerEntry = $zip.GetEntry('Install.ps1')
        if (-not $cerEntry -or -not $installerEntry) { throw 'Archive is missing the preview certificate or installer.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($cerEntry, $tempCer, $true)
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($tempCer)
        $installerReader = New-Object IO.StreamReader($installerEntry.Open())
        try { $installerContents = $installerReader.ReadToEnd() } finally { $installerReader.Dispose() }
        if (-not $installerContents.Contains("`$expectedThumbprint = '$($cert.Thumbprint)'")) { throw 'Installer does not pin the included certificate thumbprint.' }
        if (-not $installerContents.Contains("`$expectedPublisher = '$($cert.Subject.Replace("'", "''"))'")) { throw 'Installer does not pin the included certificate publisher.' }
        Assert-ReleaseArchive $resolvedArchive $cert.Thumbprint $cert.Subject $PackageVersion $Architecture
    } finally {
        $zip.Dispose()
        if (Test-Path -LiteralPath $tempCer) {
            $tempCerFullPath = [IO.Path]::GetFullPath($tempCer)
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $tempCerFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove temporary certificate outside the temp directory.' }
            Remove-Item -LiteralPath $tempCerFullPath -Force
        }
    }
    return
}
if (-not $Certificate -or -not $PublicCertificate) { throw 'Certificate and PublicCertificate are required when creating an archive.' }
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+-preview\.\d+$') { throw 'ReleaseVersion must use the form 0.1.0-preview.1.' }
if ($PackageVersion -notmatch '^\d{1,5}(\.\d{1,5}){3}$') { throw 'PackageVersion must contain four numeric components, for example 0.1.0.0.' }
$pfxPath = (Resolve-Path -LiteralPath $Certificate).Path
$cerPath = (Resolve-Path -LiteralPath $PublicCertificate).Path
$pfxCertificate = Get-CertificateFromPfx $pfxPath $CertificatePassword
$cerCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
if ($pfxCertificate.Thumbprint -ne $cerCertificate.Thumbprint) { throw 'PFX and public CER contain different certificates.' }
$thumbprint = $cerCertificate.Thumbprint
$publisher = $cerCertificate.Subject
$packageName = 'ChatAgentBridge.WinUI.Preview'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $outputRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $packagePath = Join-Path $stage "$packageName.msix"
    $packageScript = Join-Path $PSScriptRoot 'Package.ps1'
    $packageParameters = @{
        Architecture = $Architecture
        Certificate = $pfxPath
        CertificatePassword = $CertificatePassword
        Version = $PackageVersion
        PackageName = $packageName
        OutputPath = $packagePath
    }
    if ($TimestampUrl) { $packageParameters.TimestampUrl = $TimestampUrl }
    if ($ExistingPackage) {
        $existingPackagePath = (Resolve-Path -LiteralPath $ExistingPackage).Path
        Copy-Item -LiteralPath $existingPackagePath -Destination $packagePath
    } else {
        & $packageScript @packageParameters
    }
    if (-not (Test-Path -LiteralPath $packagePath)) { throw 'Package.ps1 did not create the requested MSIX.' }
    Assert-PackageSignature $packagePath $thumbprint
    $identity = Get-PackageManifest $packagePath
    if ($identity.GetAttribute('Publisher') -ne $publisher) { throw 'Generated MSIX publisher does not match the supplied certificate subject.' }

    Copy-Item -LiteralPath $cerPath -Destination (Join-Path $stage "$packageName.cer")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution/README.en.txt') -Destination (Join-Path $stage 'README.en.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution/README.ja.txt') -Destination (Join-Path $stage 'README.ja.txt')

    $installer = @'
[CmdletBinding()]
param([switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
$trustVerifierSource = @"
__TRUST_VERIFIER_SOURCE__
"@
if (-not ('CabPreviewTrustV1.TrustVerifier' -as [type])) { Add-Type -TypeDefinition $trustVerifierSource -Language CSharp }
$expectedThumbprint = '__THUMBPRINT__'
$expectedPublisher = '__PUBLISHER__'
$packageName = 'ChatAgentBridge.WinUI.Preview'
$root = $PSScriptRoot
$packagePath = Join-Path $root "$packageName.msix"
$certificatePath = Join-Path $root "$packageName.cer"
$checksumPath = Join-Path $root 'SHA256SUMS.txt'
foreach ($path in @($packagePath, $certificatePath, $checksumPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $([IO.Path]::GetFileName($path))" }
}
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($cert.Thumbprint -ne $expectedThumbprint -or $cert.Subject -ne $expectedPublisher) { throw 'The included public certificate does not match the pinned release signer.' }
$hashLine = Get-Content -LiteralPath $checksumPath | Where-Object { $_ -match ('^([0-9a-fA-F]{64}) \*?' + [regex]::Escape("$packageName.msix") + '$') } | Select-Object -First 1
if (-not $hashLine) { throw 'SHA256SUMS.txt has no checksum for the MSIX.' }
$expectedHash = ($hashLine -split '\s+', 2)[0]
$actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'MSIX SHA-256 checksum does not match SHA256SUMS.txt.' }
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) { throw 'MSIX signer does not match the pinned release certificate.' }
$trustResult = [CabPreviewTrustV1.TrustVerifier]::VerifyFile([IO.Path]::GetFullPath($packagePath))
$untrustedRootHresult = [Convert]::ToUInt32('800B0109', 16)
if ($trustResult -ne 0 -and $trustResult -ne $untrustedRootHresult) { throw ("WinVerifyTrust rejected the MSIX signature with HRESULT 0x{0:X8} (PowerShell status: {1}; {2})." -f $trustResult, $signature.Status, $signature.StatusMessage) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entry = $zip.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'MSIX has no root AppxManifest.xml.' }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
    if (-not $identity -or $identity.GetAttribute('Name') -ne $packageName -or $identity.GetAttribute('Publisher') -ne $expectedPublisher) { throw 'MSIX package identity or publisher is unexpected.' }
} finally { $zip.Dispose() }
Write-Output "Validated $packageName ($($identity.GetAttribute('Version'))), signer $expectedThumbprint."
if ($ValidateOnly) { return }
Add-AppxPackage -Path $packagePath
Write-Output 'Installed for the current Windows user.'
'@
    $installer = $installer.Replace('__TRUST_VERIFIER_SOURCE__', $trustVerifierSource.Trim()).Replace('__THUMBPRINT__', $thumbprint).Replace('__PUBLISHER__', $publisher.Replace("'", "''"))
    Set-Content -LiteralPath (Join-Path $stage 'Install.ps1') -Value $installer -Encoding utf8

    $checksums = foreach ($name in @("$packageName.msix", "$packageName.cer", 'README.ja.txt', 'README.en.txt', 'Install.ps1')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$name"
    }
    $checksums | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii

    $archivePath = Join-Path $outputRoot "$packageName-$ReleaseVersion-$Architecture.zip"
    if (Test-Path -LiteralPath $archivePath) { throw "Release archive already exists: $archivePath" }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archivePath -CompressionLevel Optimal
    Assert-ReleaseArchive $archivePath $thumbprint $publisher $PackageVersion $Architecture
    Write-Output "Created: $archivePath"
} finally {
    Remove-DirectoryUnderRoot $stage $outputRoot
}
