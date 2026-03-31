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

Write-BridgeAuditEvent -Type "service.restart.requested" -Actor $Actor -Reason $Reason -Fields @{
  build = $Build.IsPresent
}

powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -Actor $Actor -Reason $Reason
if ($Build) {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript -Actor $Actor -Reason $Reason
} else {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript -SkipBuild -Actor $Actor -Reason $Reason
}

Write-BridgeAuditEvent -Type "service.restart.completed" -Actor $Actor -Reason $Reason -Fields @{
  build = $Build.IsPresent
}
