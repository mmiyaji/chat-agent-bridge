param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Certificate
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj'
$output = Join-Path $PSScriptRoot "artifacts/publish/$Architecture"
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
Write-Output "Created: $package"
Write-Output 'Development identity only. Replace Identity with Partner Center values before Store submission.'
