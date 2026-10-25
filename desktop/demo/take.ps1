# Resets the demo in the given language and plays the scenario; -Record captures it.
param([ValidateSet('ja', 'en')][string]$Lang = 'ja', [switch]$Record)
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
& (Join-Path $d 'reset-demo.ps1') -Lang $Lang
& (Join-Path $d 'open-browser.ps1') -Lang $Lang
& (Join-Path $d 'prep-mattermost-ui.ps1') -Lang $Lang
& (Join-Path $d 'run-demo.ps1') -Lang $Lang -Record:$Record
& (Join-Path $d 'stage.ps1')
Start-Sleep 2
& (Join-Path $d 'shot.ps1') "final-$Lang"
& (Join-Path $d 'stage.ps1') -Off
