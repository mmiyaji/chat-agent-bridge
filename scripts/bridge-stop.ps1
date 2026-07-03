param(
  [string]$Actor = "service-script",
  [string]$Reason = "manual-stop"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

Write-BridgeAuditEvent -Type "service.stop.requested" -Actor $Actor -Reason $Reason

$pidFile = $script:BridgePidFile

# Discover any bridge processes independently of the pid file. These are all
# validated as genuine bridge processes by the common helpers, so they are safe
# to stop even when the pid file is stale or absent.
$discoveredProcessIds = @(@(Get-BridgeProcessIds) + @(Get-BridgeAdminListenerProcessIds) | Select-Object -Unique)

$pidFileProcessId = $null
$pidFilePresent = Test-Path $pidFile
if ($pidFilePresent) {
  $rawPid = Get-BridgePidFileProcessId
  if ($null -ne $rawPid) {
    if (Test-BridgeProcessId -ProcessId $rawPid) {
      $pidFileProcessId = $rawPid
    } else {
      # The PID in the pid file no longer belongs to the bridge (dead process or
      # a reused PID owned by something else). Never stop it. Drop the stale file.
      Write-Output "Stale PID file (process $rawPid is not the bridge). Removing it."
      Write-BridgeAuditEvent -Type "service.stop.stale-pid" -Actor $Actor -Reason "stale-pid" -Fields @{
        processId = $rawPid
      }
      Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    }
  } else {
    # Empty or unreadable pid file. Remove it so it does not confuse future runs.
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
  }
}

$processIds = @()
if ($null -ne $pidFileProcessId) {
  $processIds += $pidFileProcessId
}
foreach ($processId in $discoveredProcessIds) {
  if ($null -ne $processId) {
    $processIds += [int]$processId
  }
}
$processIds = @($processIds | Select-Object -Unique)

if ($processIds.Count -eq 0) {
  Write-Output "Bridge is not running."
  Write-BridgeAuditEvent -Type "service.stop.skipped" -Actor $Actor -Reason "not-running"
  if ($null -ne $pidFileProcessId) {
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
  }
  exit 0
}

$stoppedProcessIds = @()
foreach ($processId in $processIds) {
  # Only ever stop PIDs that were validated as genuine bridge processes above.
  $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
  if ($process) {
    Stop-Process -Id $processId -Force
    Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
    Write-Output "Stopped bridge process $processId."
    $stoppedProcessIds += $processId
  }
}

if ($stoppedProcessIds.Count -eq 0) {
  Write-Output "No running bridge process was found."
  Write-BridgeAuditEvent -Type "service.stop.skipped" -Actor $Actor -Reason "process-not-found"
} else {
  Write-BridgeAuditEvent -Type "service.stop.completed" -Actor $Actor -Reason $Reason -Fields @{
    processIds = @($stoppedProcessIds | Select-Object -Unique)
  }
}

Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
