# Entry script for the remote-win-ui-test skill: builds the portable app and the test agent on the test PC,
# then runs FileAttachmentsUiE2E.ps1 against an offline chat fixture. Results go to $env:REMOTE_UI_OUT.
param([ValidateSet('ja','en')][string]$Language = 'ja', [string]$MattermostStateFile, [switch]$KeepRunningOnFailure)
$ErrorActionPreference = 'Stop'
$out = if ($env:REMOTE_UI_OUT) { $env:REMOTE_UI_OUT } else { Join-Path $PSScriptRoot '..\artifacts\remote-ui' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$desktop = Split-Path $PSScriptRoot -Parent
$artifacts = [IO.Path]::GetFullPath((Join-Path $desktop 'artifacts'))
# The offline chat fixture needs the repository's node packages (ws); the runner cleans untracked files.
Push-Location (Split-Path $desktop -Parent)
try { npm ci --ignore-scripts --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } } finally { Pop-Location }
dotnet build (Join-Path $desktop 'ChatAgentBridge.Desktop\ChatAgentBridge.Desktop.csproj') -p:CABPortable=true -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
$exe = Get-ChildItem (Join-Path $desktop 'ChatAgentBridge.Desktop\bin\AnyCPU\Debug\portable') -Recurse -Filter 'ChatAgentBridge.Desktop.exe' |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Built exe not found.' }
# The E2E only accepts a portable app under artifacts\file-attachments-publish.
$publish = [IO.Path]::GetFullPath((Join-Path $artifacts 'file-attachments-publish'))
$artifactRoot = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publish.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($publish) -ne 'file-attachments-publish') {
    throw "Refusing to replace a path outside the attachment test artifacts: $publish"
}
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
Copy-Item (Split-Path $exe) $publish -Recurse
dotnet publish (Join-Path $desktop 'ChatAgentBridge.TestAgent\ChatAgentBridge.TestAgent.csproj') -c Release -r win-x64 --self-contained `
    -o (Join-Path $artifacts 'file-attachment-test-agent') -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Test agent publish failed.' }
$testArgs = @{ AppDirectory = $publish; Portable = $true; Language = $Language }
if ($MattermostStateFile) { $testArgs.MattermostStateFile = $MattermostStateFile }
if ($KeepRunningOnFailure) { $testArgs.KeepRunningOnFailure = $true }
& (Join-Path $desktop 'FileAttachmentsUiE2E.ps1') @testArgs *>&1 | Tee-Object -Variable log
$code = $LASTEXITCODE
$run = ($log | Select-String 'File attachment UI results: (.+)$').Matches.Groups[1].Value
if ($run -and (Test-Path $run)) {
    foreach ($file in (Get-ChildItem $run -Recurse -Include *.png, results.json, server-results-on-failure.json)) {
        $name = if ($file.Name -eq 'failure.png') { 'app-window-failure.png' } else { $file.Name }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $out $name) -Force
    }
}
exit $code
