param(
  [string]$Actor = "service-script",
  [string]$Reason = "manual-stop"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

Write-BridgeAuditEvent -Type "service.stop.requested" -Actor $Actor -Reason $Reason

$pidFile = $script:BridgePidFile

if (!(Test-Path $pidFile)) {
  $orphanProcessIds = @(Get-BridgeProcessIds) + @(Get-BridgeAdminListenerProcessIds)
  if ($orphanProcessIds.Count -eq 0) {
    Write-Output "Bridge is not running."
    Write-BridgeAuditEvent -Type "service.stop.skipped" -Actor $Actor -Reason "not-running"
    exit 0
  }

  foreach ($processId in ($orphanProcessIds | Select-Object -Unique)) {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
    Write-Output "Stopped bridge process $processId."
  }

  Write-BridgeAuditEvent -Type "service.stop.completed" -Actor $Actor -Reason $Reason -Fields @{
    processIds = @($orphanProcessIds | Select-Object -Unique)
  }

  exit 0
}

$bridgeProcessId = (Get-Content $pidFile -Raw).Trim()
$processIds = @($bridgeProcessId) + (Get-BridgeProcessIds | Where-Object { $_ -ne [int]$bridgeProcessId }) + (Get-BridgeAdminListenerProcessIds | Where-Object { $_ -ne [int]$bridgeProcessId })
$stoppedAny = $false

foreach ($processId in ($processIds | Select-Object -Unique)) {
  $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
  if ($process) {
    Stop-Process -Id $processId -Force
    Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
    Write-Output "Stopped bridge process $processId."
    $stoppedAny = $true
  }
}

if (!$stoppedAny) {
  Write-Output "No running process was found for PID $bridgeProcessId."
  Write-BridgeAuditEvent -Type "service.stop.skipped" -Actor $Actor -Reason "process-not-found" -Fields @{
    processId = if ($bridgeProcessId) { [int]$bridgeProcessId } else { $null }
  }
} else {
  Write-BridgeAuditEvent -Type "service.stop.completed" -Actor $Actor -Reason $Reason -Fields @{
    processIds = @($processIds | Select-Object -Unique)
  }
}

Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
