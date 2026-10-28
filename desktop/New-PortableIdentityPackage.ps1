[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Layout,
    [Parameter(Mandatory)][string] $CertificateThumbprint,
    [string] $MakeAppxPath,
    [string] $SignToolPath,
    [string] $TimestampUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageName = 'mmiyaji.ChatAgentBridge'
$publisher = 'CN=mmiyaji'
$applicationId = 'App'
$packageVersion = '1.0.0.0'
$identityFolderName = 'Identity'
$manifestTemplate = Join-Path $PSScriptRoot 'portable-identity/Package.manifest.xml'

function Get-Sha256Hex {
    param([Parameter(Mandatory)][byte[]] $Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory)][string] $Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Test-PathWithin {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $Path)
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath($Path)
    return $candidate.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory)][string] $Path)
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a reparse point in the identity package path: $Path"
    }
}

function Resolve-SdkTool {
    param([Parameter(Mandatory)][string] $Name, [string] $ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = (Resolve-Path -LiteralPath $ExplicitPath).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Name not found: $ExplicitPath" }
        return $resolved
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    if (-not (Test-Path -LiteralPath $kitsRoot -PathType Container)) {
        throw "Windows SDK tools were not found. Install the Windows 10/11 SDK or pass -$($Name -replace '\.exe$','Path')."
    }
    $candidates = @(Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64/$Name" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -eq 0) { throw "$Name was not found under $kitsRoot." }
    return $candidates[0]
}

function Invoke-NativeChecked {
    param([Parameter(Mandatory)][string] $Executable, [Parameter(Mandatory)][string[]] $Arguments, [Parameter(Mandatory)][string] $Operation)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE." }
}

function Get-SigningCertificate {
    param([Parameter(Mandatory)][string] $Thumbprint)
    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') { throw 'CertificateThumbprint must be a 40-character SHA-1 thumbprint.' }
    $matches = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' | Where-Object { $_.Thumbprint -eq $normalized })
    if ($matches.Count -ne 1) { throw "A unique certificate with thumbprint $normalized was not found in CurrentUser\My." }
    $certificate = $matches[0]
    if ($certificate.Subject -cne $publisher) { throw "The signing certificate Subject must be exactly '$publisher'; found '$($certificate.Subject)'." }
    if (-not $certificate.HasPrivateKey) { throw 'The selected certificate has no private key for signing.' }
    $now = Get-Date
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw 'The selected signing certificate is outside its validity period.' }
    return $certificate
}

function Assert-SignedPackage {
    param([Parameter(Mandatory)][string] $PackagePath, [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2] $ExpectedCertificate)
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('AppxSignature.p7x')
        if ($null -eq $entry) { throw 'The signed package has no AppxSignature.p7x entry.' }
        $memory = [System.IO.MemoryStream]::new()
        $input = $entry.Open()
        try { $input.CopyTo($memory) }
        finally { $input.Dispose() }
        $signature = $memory.ToArray()
        $memory.Dispose()
        if ($signature.Length -lt 5 -or [System.Text.Encoding]::ASCII.GetString($signature, 0, 4) -cne 'PKCX') {
            throw 'The package signature entry has an invalid PKCX header.'
        }
        $cmsBytes = [byte[]]::new($signature.Length - 4)
        [Array]::Copy($signature, 4, $cmsBytes, 0, $cmsBytes.Length)
        $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($cmsBytes)
        $cms.CheckSignature($true)
        if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) {
            throw 'The package must have exactly one embedded signing certificate.'
        }
        $signer = $cms.SignerInfos[0].Certificate
        if ([Convert]::ToBase64String($signer.RawData) -cne [Convert]::ToBase64String($ExpectedCertificate.RawData)) {
            throw 'The package signature leaf certificate does not match the selected certificate.'
        }
    }
    finally { $archive.Dispose() }
}

$resolvedLayout = (Resolve-Path -LiteralPath $Layout).Path
if (-not (Test-Path -LiteralPath $resolvedLayout -PathType Container)) { throw "Portable layout not found: $Layout" }
Assert-NoReparsePoint -Path $resolvedLayout
$resolvedLayout = [System.IO.Path]::GetFullPath($resolvedLayout)

$expectedFiles = @(
    'ChatAgentBridge.Desktop.exe',
    'Assets/StoreLogo.png',
    'Assets/Square150x150Logo.scale-200.png',
    'Assets/Square44x44Logo.scale-200.png'
)
foreach ($relative in $expectedFiles) {
    $path = Join-Path $resolvedLayout $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required portable layout file is missing: $relative" }
    Assert-NoReparsePoint -Path $path
}
Assert-NoReparsePoint -Path (Join-Path $resolvedLayout 'Assets')
if (-not (Test-Path -LiteralPath $manifestTemplate -PathType Leaf)) { throw "Identity manifest template not found: $manifestTemplate" }

$identityDirectory = Join-Path $resolvedLayout $identityFolderName
if (Test-Path -LiteralPath $identityDirectory) { Assert-NoReparsePoint -Path $identityDirectory }
$resolvedIdentityDirectory = [System.IO.Path]::GetFullPath($identityDirectory)
if (-not (Test-PathWithin -Root $resolvedLayout -Path $resolvedIdentityDirectory)) { throw 'Identity output path escaped the portable layout.' }

$certificate = Get-SigningCertificate -Thumbprint $CertificateThumbprint
$timestampUri = $null
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    if (-not [uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) {
        throw '-TimestampUrl must be an absolute HTTP or HTTPS URL.'
    }
}
$makeAppx = Resolve-SdkTool -Name 'makeappx.exe' -ExplicitPath $MakeAppxPath
$signTool = Resolve-SdkTool -Name 'signtool.exe' -ExplicitPath $SignToolPath
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('cab-portable-identity-' + [guid]::NewGuid().ToString('N'))
$scratchManifest = Join-Path $scratch 'AppxManifest.xml'
$scratchPackage = Join-Path $scratch 'ChatAgentBridge.Portable.identity.msix'
$outputPackage = Join-Path $identityDirectory 'ChatAgentBridge.Portable.identity.msix'
$outputCertificate = Join-Path $identityDirectory 'ChatAgentBridge.Portable.cer'
$outputMetadata = Join-Path $identityDirectory 'portable-identity.json'

try {
    if (Test-PathWithin -Root $resolvedLayout -Path $scratch) { throw 'Temporary package staging must be outside the portable layout.' }
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Copy-Item -LiteralPath $manifestTemplate -Destination $scratchManifest
    [xml]$manifest = Get-Content -LiteralPath $scratchManifest -Raw
    $ns = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $ns.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
    $ns.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $ns.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
    if ($null -eq $identity -or $identity.GetAttribute('Name') -cne $packageName -or
        $identity.GetAttribute('Publisher') -cne $publisher -or $identity.GetAttribute('Version') -cne $packageVersion -or
        $identity.GetAttribute('ProcessorArchitecture') -cne 'neutral') {
        throw 'The identity manifest package name, publisher, version, or architecture does not match the build contract.'
    }
    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $ns)
    if ($null -eq $application -or $application.GetAttribute('Id') -cne $applicationId -or
        $application.GetAttribute('Executable') -cne 'ChatAgentBridge.Desktop.exe' -or
        $application.GetAttribute('TrustLevel', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -cne 'mediumIL' -or
        $application.GetAttribute('RuntimeBehavior', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -cne 'win32App' -or
        $application.HasAttribute('EntryPoint')) {
        throw 'The identity manifest application definition does not match the build contract.'
    }
    $allowExternal = $manifest.SelectSingleNode('/f:Package/f:Properties/uap10:AllowExternalContent', $ns)
    if ($null -eq $allowExternal -or $allowExternal.InnerText -cne 'true') { throw 'AllowExternalContent must be true.' }
    $toast = $manifest.SelectSingleNode('//desktop:ToastNotificationActivation[@ToastActivatorCLSID="71F2D23E-C3A6-4385-B377-612FE0DA9AB1"]', $ns)
    $comServer = $manifest.SelectSingleNode('//com:ExeServer[@Executable="ChatAgentBridge.Desktop.exe"]', $ns)
    $comClass = $manifest.SelectSingleNode('//com:Class[@Id="71F2D23E-C3A6-4385-B377-612FE0DA9AB1"]', $ns)
    if ($null -eq $toast -or $null -eq $comServer -or $comServer.GetAttribute('Arguments') -cne '----AppNotificationActivated:' -or $null -eq $comClass) {
        throw 'The identity manifest must register the app notification activator and matching COM server.'
    }

    Invoke-NativeChecked -Executable $makeAppx -Arguments @('pack', '/o', '/d', $scratch, '/nv', '/p', $scratchPackage) -Operation 'MakeAppx pack'
    $signArguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My')
    if ($null -ne $timestampUri) { $signArguments += @('/tr', $timestampUri.AbsoluteUri, '/td', 'SHA256') }
    $signArguments += $scratchPackage
    Invoke-NativeChecked -Executable $signTool -Arguments $signArguments -Operation 'SignTool sign'
    Assert-SignedPackage -PackagePath $scratchPackage -ExpectedCertificate $certificate

    $certificateSha256 = Get-Sha256Hex -Bytes $certificate.RawData
    $packageSha256 = Get-FileSha256Hex -Path $scratchPackage
    $metadata = [ordered]@{
        schemaVersion = 1
        packageName = $packageName
        publisher = $publisher
        applicationId = $applicationId
        packageVersion = $packageVersion
        certificateSha256 = $certificateSha256
        packageSha256 = $packageSha256
    }
    $json = ConvertTo-Json -InputObject $metadata -Depth 3
    $publicCertificate = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

    if (-not (Test-Path -LiteralPath $identityDirectory -PathType Container)) { New-Item -ItemType Directory -Path $identityDirectory | Out-Null }
    Assert-NoReparsePoint -Path $identityDirectory
    foreach ($target in @($outputPackage, $outputCertificate, $outputMetadata)) {
        if (Test-Path -LiteralPath $target) { Assert-NoReparsePoint -Path $target }
    }
    Copy-Item -LiteralPath $scratchPackage -Destination $outputPackage -Force
    [System.IO.File]::WriteAllBytes($outputCertificate, $publicCertificate)
    [System.IO.File]::WriteAllText($outputMetadata, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    if ((Get-FileSha256Hex -Path $outputPackage) -cne $packageSha256) { throw 'The copied identity package hash does not match the signed package.' }
    $exported = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($outputCertificate)
    try {
        if ((Get-Sha256Hex -Bytes $exported.RawData) -cne $certificateSha256 -or $exported.HasPrivateKey) {
            throw 'The exported certificate does not match the signing leaf or contains a private key.'
        }
    }
    finally { $exported.Dispose() }
    Write-Output "Identity package: $outputPackage"
    Write-Output "Public certificate: $outputCertificate"
    Write-Output "Identity metadata: $outputMetadata"
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
