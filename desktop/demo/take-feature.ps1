# Resets the demo (Japanese) and plays one feature scenario; -Record captures it.
param([ValidateSet('project', 'agent')][string]$Scenario, [switch]$Record, [switch]$NoReset, [switch]$AutoSwitch)
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
if (-not $NoReset) {
    & (Join-Path $d 'reset-demo.ps1') -Lang ja -Theme dark -AutoSwitch:$AutoSwitch
    & (Join-Path $d 'open-browser.ps1') -Lang ja
    & (Join-Path $d 'prep-mattermost-ui.ps1') -Lang ja
}
& (Join-Path $d 'run-scenario.ps1') -Scenario $Scenario -Record:$Record -AutoSwitch:$AutoSwitch
& (Join-Path $d 'stage.ps1')
Start-Sleep 2
& (Join-Path $d 'shot.ps1') "final-$Scenario"
& (Join-Path $d 'stage.ps1') -Off
