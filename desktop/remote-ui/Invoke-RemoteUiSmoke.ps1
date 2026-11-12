# Runs on the dev PC. Pushes HEAD to the test PC over SSH, starts the CabUiSmoke task there
# (which runs in the logged-on desktop session), waits for it and copies the results back.
# Nothing appears on this PC's screen.
param(
    [string]$HostName = 'stampede',
    [int]$TimeoutMinutes = 30,
    [string]$OutDirectory = (Join-Path $PSScriptRoot '..\artifacts\remote-ui'),
    # Optional local script run on the test PC instead of SmokeTest.ps1 (receives -AppPid -DataDirectory -OutDirectory).
    [string]$ProbeScript
)
$ErrorActionPreference = 'Stop'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
# The test PC uses PowerShell as the OpenSSH default shell (see README), so git paths and commands below are PowerShell.
$remote = "${HostName}:C:/cab-ci/repo.git"

# Include uncommitted changes to tracked files (untracked files are not sent) without touching the working tree.
$commit = git stash create
if (-not $commit) { $commit = git rev-parse HEAD }
git push --quiet --force $remote "${commit}:refs/heads/ui-test"
if ($LASTEXITCODE -ne 0) { throw 'git push to the test PC failed.' }
$mode = ''
if ($ProbeScript) {
    scp -q $ProbeScript "${HostName}:C:/cab-ci/probe.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the probe script.' }
    $mode = ' probe'
}
ssh $HostName "Set-Content C:\cab-ci\request.txt '$runId$mode';schtasks /run /tn CabUiSmoke | Out-Null; exit `$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { throw 'Could not start the CabUiSmoke task.' }
Write-Host "Started run $runId on $HostName"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 15
    $status = (ssh $HostName 'Get-Content C:\cab-ci\out\status.txt -ErrorAction SilentlyContinue') -join ''
    if ((Get-Date) -gt $deadline) { throw "Timed out; last status: $status" }
} until ($status -match "^$runId (passed|failed)")
$state = $Matches[1]

if (Test-Path $OutDirectory) { Remove-Item $OutDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
scp -q -r "${HostName}:C:/cab-ci/out/*" $OutDirectory
Write-Host "Result: $state (files in $([IO.Path]::GetFullPath($OutDirectory)))"
$json = Join-Path $OutDirectory 'smoke-results.json'
if (Test-Path $json) {
    Get-Content $json -Raw | ConvertFrom-Json | ForEach-Object { "$(if ($_.passed) {'PASS'} else {'FAIL'}): $($_.name) $($_.error)" }
}
if ($state -ne 'passed') { exit 1 }
