param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Certificate,
    [securestring]$CertificatePassword = (ConvertTo-SecureString 'password' -AsPlainText -Force),
    [string]$Version,
    [string]$PackageName = 'ChatAgentBridge.WinUI.Development',
    [string]$OutputPath,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'

if ($Version -and $Version -notmatch '^\d{1,5}(\.\d{1,5}){3}$') {
    throw "Version must contain four numeric components, for example 0.1.0.0: $Version"
}
if ($Version) {
    foreach ($component in $Version.Split('.')) {
        if ([int]$component -gt 65535) { throw "Version components must not exceed 65535: $Version" }
    }
}
if ($PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw "Invalid package identity name: $PackageName" }

$project = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj'
# A unique layout prevents winapp run's nested AppX child layout from being reused.
$layout = Join-Path $PSScriptRoot "artifacts/package-layout/$Architecture/$([Guid]::NewGuid().ToString('N'))"
if ($OutputPath) {
    $package = [IO.Path]::GetFullPath($OutputPath)
} else {
    $package = Join-Path $PSScriptRoot "artifacts/ChatAgentBridge.WinUI.$Architecture.msix"
}
$packageDirectory = Split-Path -Parent $package
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

& dotnet publish $project -c Release -p:Platform=$Architecture -r "win-$($Architecture.ToLowerInvariant())" --self-contained true -p:WindowsAppSDKSelfContained=true -o $layout
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$manifest = Join-Path $layout 'AppxManifest.xml'
$buildManifest = Join-Path $PSScriptRoot "ChatAgentBridge.Desktop/bin/$Architecture/Release/net10.0-windows10.0.26100.0/win-$($Architecture.ToLowerInvariant())/AppxManifest.xml"
if (-not (Test-Path -LiteralPath $buildManifest)) { throw "Generated manifest not found: $buildManifest" }
Copy-Item -LiteralPath $buildManifest -Destination $manifest -Force

# Only the generated layout is changed; the checked-in source manifest keeps its development identity.
[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw
$identity = $manifestXml.SelectSingleNode("//*[local-name()='Identity']")
if (-not $identity) { throw 'Generated manifest has no Identity element.' }
if ($Version) { $identity.SetAttribute('Version', $Version) }
$identity.SetAttribute('Name', $PackageName)
$manifestXml.Save($manifest)

$packageArgs = @('package', $layout, '--manifest', $manifest, '--output', $package)
if ($Certificate) {
    $packageArgs += @('--cert', (Resolve-Path -LiteralPath $Certificate).Path)
    if ($CertificatePassword) {
        $passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
        try { $passwordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr) }
        $packageArgs += @('--cert-password', $passwordText)
    }
}
if ($TimestampUrl) {
    if (-not $Certificate) { throw 'TimestampUrl requires a signing certificate.' }
    $passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
    try { $passwordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr) }
    # winapp package has no timestamp option; create unsigned, then sign with a timestamp.
    $packageArgs = @('package', $layout, '--manifest', $manifest, '--output', $package)
}
& winapp @packageArgs
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }
if ($TimestampUrl) {
    & winapp sign $package (Resolve-Path -LiteralPath $Certificate).Path --password $passwordText --timestamp $TimestampUrl
    if ($LASTEXITCODE -ne 0) { throw 'Timestamped MSIX signing failed.' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('AppIcon.ico', 'StoreLogo.png', 'Square44x44Logo.scale-200.png', 'Square150x150Logo.scale-200.png', 'Wide310x150Logo.scale-200.png', 'SplashScreen.scale-200.png', 'OverviewArchitecture.png')) {
        if ($entryNames -notcontains "Assets/$required") { throw "Package is missing required visual asset: $required" }
    }
    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
        if ($entryNames -notcontains $runtimeFile) { throw "Self-contained package is missing required runtime file: $runtimeFile" }
    }
    if ($archive.Entries | Where-Object { $_.FullName -match '^(?i:AppX)/|ChatAgentBridge\.TestAgent|mattermost-e2e\.json' }) {
        throw 'Package contains a nested development layout or a test fixture.'
    }
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if (-not $manifestEntry) { throw 'Package has no root AppxManifest.xml.' }
    $manifestReader = New-Object IO.StreamReader($manifestEntry.Open())
    try { [xml]$finalManifest = $manifestReader.ReadToEnd() } finally { $manifestReader.Dispose() }
    $finalIdentity = $finalManifest.SelectSingleNode("//*[local-name()='Identity']")
    if ($finalIdentity.GetAttribute('Name') -ne $PackageName) { throw 'Packaged identity name does not match the requested PackageName.' }
    if ($Version -and $finalIdentity.GetAttribute('Version') -ne $Version) { throw 'Packaged identity version does not match the requested Version.' }
    if ($finalManifest.SelectSingleNode("//*[local-name()='PackageDependency']")) { throw 'Self-contained package unexpectedly declares a framework package dependency.' }
} finally { $archive.Dispose() }
Write-Output "Created: $package"
