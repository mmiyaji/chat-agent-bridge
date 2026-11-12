# Runs desktop/SmokeTest.ps1 on the LAN test PC through the user-scope remote-win-ui-test skill
# (not bundled in this repository; see README.md). Nothing appears on this PC's screen.
param(
    [string]$HostName = $(if ($env:REMOTE_WIN_TEST_HOST) { $env:REMOTE_WIN_TEST_HOST } else { 'stampede' }),
    [string]$OutDirectory = (Join-Path $PSScriptRoot '..\artifacts\remote-ui'),
    # Optional local probe script run instead of SmokeTest.ps1 (receives -AppPid -DataDirectory -OutDirectory).
    [string]$ProbeScript
)
$ErrorActionPreference = 'Stop'
$skill = Join-Path $HOME '.agents\skills\remote-win-ui-test\scripts\Invoke-RemoteWinTest.ps1'
if (-not (Test-Path $skill)) { throw "remote-win-ui-test skill not found at $skill. See desktop/remote-ui/README.md." }
$params = @{ HostName = $HostName; Entry = 'desktop/remote-ui/Run-UiSmoke.ps1'; OutDirectory = $OutDirectory }
if ($ProbeScript) {
    $params.UploadFiles = @($ProbeScript)
    $params.EntryArgs = @('-Probe', (Split-Path $ProbeScript -Leaf))
}
& $skill @params
exit $LASTEXITCODE
