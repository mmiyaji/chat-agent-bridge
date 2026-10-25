# Resets the demo, then checks the switch auto-approval setting in the settings page.
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
. (Join-Path $d 'demo-env.ps1')
& (Join-Path $d 'reset-demo.ps1') -Lang ja -Theme dark
& (Join-Path $d 'check-switch-setting.ps1')
Start-Sleep 2
"settings: $((Get-Content (Join-Path $CabData 'settings.json') -Raw | ConvertFrom-Json | Select-Object ChatApprovalMode, AutoApproveSwitchCommands | ConvertTo-Json -Compress))"
