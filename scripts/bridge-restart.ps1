param(
  [switch]$Build,
  [string]$Actor = "service-script",
  [string]$Reason = "manual-restart"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$stopScript = if ($env:CAB_BRIDGE_STOP_SCRIPT) { $env:CAB_BRIDGE_STOP_SCRIPT } else { Join-Path $PSScriptRoot "bridge-stop.ps1" }
$startScript = if ($env:CAB_BRIDGE_START_SCRIPT) { $env:CAB_BRIDGE_START_SCRIPT } else { Join-Path $PSScriptRoot "bridge-start.ps1" }
$powershellPath = Get-BridgePowerShellPath
$previousProcessId = Get-BridgePidFileProcessId

Write-BridgeAuditEvent -Type "service.restart.requested" -Actor $Actor -Reason $Reason -Fields @{
  build = $Build.IsPresent
}

& $powershellPath -NoProfile -ExecutionPolicy Bypass -File $stopScript -Actor $Actor -Reason $Reason

Start-Sleep -Milliseconds 500
$stillRunning = @()
if ($null -ne $previousProcessId) {
  $existingProcess = Get-Process -Id $previousProcessId -ErrorAction SilentlyContinue
  if ($existingProcess) {
    $stillRunning = @($previousProcessId)
  }
}
if ($stillRunning.Count -gt 0) {
  Write-BridgeAuditEvent -Type "service.restart.failed" -Actor $Actor -Reason "stop-incomplete" -Fields @{
    build = $Build.IsPresent
    previousProcessId = $previousProcessId
    remainingProcessIds = $stillRunning
  }
  throw "Bridge stop did not complete before restart. Remaining process IDs: $($stillRunning -join ', ')"
}

if ($Build) {
  & $powershellPath -NoProfile -ExecutionPolicy Bypass -File $startScript -Actor $Actor -Reason $Reason
} else {
  & $powershellPath -NoProfile -ExecutionPolicy Bypass -File $startScript -SkipBuild -Actor $Actor -Reason $Reason
}

Write-BridgeAuditEvent -Type "service.restart.completed" -Actor $Actor -Reason $Reason -Fields @{
  build = $Build.IsPresent
}
