param(
  [string]$Actor = "service-script",
  [string]$Reason = "install-autostart"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$taskName = "ChatAgentBridge"
$repoRoot = $script:BridgeRepoRoot
$startScript = Join-Path $PSScriptRoot "bridge-start.ps1"
$powershellPath = Get-BridgePowerShellPath

$actionArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -SkipBuild' -f $startScript

$action = New-ScheduledTaskAction `
  -Execute $powershellPath `
  -Argument $actionArguments `
  -WorkingDirectory $repoRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

$settings = New-ScheduledTaskSettingsSet `
  -RestartCount 3 `
  -RestartInterval (New-TimeSpan -Minutes 1) `
  -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries

Register-ScheduledTask `
  -TaskName $taskName `
  -Action $action `
  -Trigger $trigger `
  -Settings $settings `
  -RunLevel Limited `
  -Description "Starts Chat Agent Bridge (prebuilt dist, -SkipBuild) at user logon." `
  -Force | Out-Null

Write-BridgeAuditEvent -Type "service.autostart.installed" -Actor $Actor -Reason $Reason -Fields @{
  taskName = $taskName
  startScript = $startScript
  repoRoot = $repoRoot
}

Write-Output "Registered scheduled task '$taskName' to start the bridge at logon."
Write-Output ""
Write-Output "Task details:"
Write-Output "  Action:  $powershellPath $actionArguments"
Write-Output "  WorkDir: $repoRoot"
Write-Output "  Trigger: at logon of $env:USERDOMAIN\$env:USERNAME"
Write-Output ""
Write-Output "Note: this starts the prebuilt dist/ with -SkipBuild. After pulling code changes, run:"
Write-Output "  npm run service:restart:build"
Write-Output ""
Write-Output "To test it immediately without logging off:"
Write-Output "  Start-ScheduledTask -TaskName $taskName"
Write-Output ""
Write-Output "To remove it:"
Write-Output "  npm run service:uninstall-autostart"
