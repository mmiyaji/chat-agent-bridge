param(
  [string]$Actor = "service-script",
  [string]$Reason = "uninstall-autostart"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$taskName = "ChatAgentBridge"

$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (!$existingTask) {
  Write-Output "Scheduled task '$taskName' is not installed. Nothing to do."
  Write-BridgeAuditEvent -Type "service.autostart.uninstalled.skipped" -Actor $Actor -Reason "not-installed" -Fields @{
    taskName = $taskName
  }
  exit 0
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false

Write-BridgeAuditEvent -Type "service.autostart.uninstalled" -Actor $Actor -Reason $Reason -Fields @{
  taskName = $taskName
}

Write-Output "Removed scheduled task '$taskName'."
