param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Certificate
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj'
# Package a fresh layout each time. winapp run creates an AppX child layout;
# reusing its parent can package that stale copy even when winapp reports exclusion.
$output = Join-Path $PSScriptRoot "artifacts/package-layout/$Architecture/$([Guid]::NewGuid().ToString('N'))"
$package = Join-Path $PSScriptRoot "artifacts/ChatAgentBridge.WinUI.$Architecture.msix"

& dotnet publish $project -c Release -p:Platform=$Architecture -r "win-$($Architecture.ToLowerInvariant())" --self-contained true -p:WindowsAppSDKSelfContained=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$manifest = Join-Path $output 'AppxManifest.xml'
$buildManifest = Join-Path $PSScriptRoot "ChatAgentBridge.Desktop/bin/$Architecture/Release/net10.0-windows10.0.26100.0/win-$($Architecture.ToLowerInvariant())/AppxManifest.xml"
if (-not (Test-Path -LiteralPath $buildManifest)) { throw "Generated manifest not found: $buildManifest" }
Copy-Item -LiteralPath $buildManifest -Destination $manifest -Force
$packageArgs = @('package', $output, '--manifest', $manifest, '--output', $package)
if ($Certificate) { $packageArgs += @('--cert', (Resolve-Path -LiteralPath $Certificate).Path) }
& winapp @packageArgs
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('AppIcon.ico', 'StoreLogo.png', 'Square44x44Logo.scale-200.png', 'Square150x150Logo.scale-200.png', 'Wide310x150Logo.scale-200.png', 'SplashScreen.scale-200.png', 'OverviewArchitecture.png')) {
        if ($entryNames -notcontains "Assets/$required") { throw "Package is missing required visual asset: $required" }
    }
    if ($archive.Entries | Where-Object { $_.FullName -match '^(?i:AppX)/|ChatAgentBridge\.TestAgent|mattermost-e2e\.json' }) {
        throw 'Package contains a nested development layout or a test fixture.'
    }
} finally { $archive.Dispose() }
Write-Output "Created: $package"
Write-Output 'Development identity only. Replace Identity with Partner Center values before Store submission.'
