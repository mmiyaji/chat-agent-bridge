$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$pidFile = $script:BridgePidFile

$bridgeProcesses = Get-BridgeProcesses
$pidFileValue = if (Test-Path $pidFile) { (Get-Content $pidFile -Raw).Trim() } else { "" }
$adminListenerProcessIds = Get-BridgeAdminListenerProcessIds

if ($bridgeProcesses.Count -eq 0) {
  if (!(Test-Path $pidFile)) {
    Write-Output "Bridge is not running."
    exit 0
  }

  if ([string]::IsNullOrWhiteSpace($pidFileValue)) {
    Write-Output "Bridge PID file is empty."
    exit 1
  }

  Write-Output "Bridge PID file exists but no matching bridge process is running."
  exit 1
}

$selectedBridgeProcess = $bridgeProcesses[-1]
$bridgeProcessId = [string]$selectedBridgeProcess.ProcessId

$port = if ($env:CAB_PORT) {
  [int]$env:CAB_PORT
} elseif (Read-BridgeSettingsValue "port") {
  [int](Read-BridgeSettingsValue "port")
} else {
  8787
}

$adminPort = if ($env:CAB_ADMIN_PORT) {
  [int]$env:CAB_ADMIN_PORT
} elseif (Read-BridgeSettingsValue "adminPort") {
  [int](Read-BridgeSettingsValue "adminPort")
} else {
  $port + 1
}

$bridgeHost = Resolve-BridgeLoopbackHost -Value $(if ($env:CAB_HOST) { $env:CAB_HOST } elseif (Read-BridgeSettingsValue "host") { [string](Read-BridgeSettingsValue "host") } else { "127.0.0.1" }) -Fallback "127.0.0.1"
$adminHost = Resolve-BridgeLoopbackHost -Value $(if ($env:CAB_ADMIN_HOST) { $env:CAB_ADMIN_HOST } elseif (Read-BridgeSettingsValue "adminHost") { [string](Read-BridgeSettingsValue "adminHost") } elseif (Read-BridgeSettingsValue "host") { [string](Read-BridgeSettingsValue "host") } else { "127.0.0.1" }) -Fallback "127.0.0.1"

Write-Output "Bridge PID: $bridgeProcessId"
if ($bridgeProcesses.Count -gt 1) {
  $otherProcessIds = @($bridgeProcesses | Where-Object { $_.ProcessId -ne $selectedBridgeProcess.ProcessId } | Select-Object -ExpandProperty ProcessId)
  Write-Output "Additional bridge PIDs: $($otherProcessIds -join ', ')"
}
if (![string]::IsNullOrWhiteSpace($pidFileValue) -and $pidFileValue -ne $bridgeProcessId) {
  Write-Output "Bridge PID file mismatch: $pidFileValue"
}
if ($adminListenerProcessIds.Count -gt 0 -and ($adminListenerProcessIds -notcontains $selectedBridgeProcess.ProcessId)) {
  Write-Output "Admin listener PIDs: $($adminListenerProcessIds -join ', ')"
}
Write-Output "Bridge URL: http://${bridgeHost}:$port"
Write-Output "Admin URL: http://${adminHost}:$adminPort/admin"

try {
  $health = Invoke-WebRequest -UseBasicParsing "http://${bridgeHost}:$port/healthz" | Select-Object -ExpandProperty Content
  Write-Output "Bridge health: $health"
} catch {
  Write-Output "Bridge health: unavailable"
}

try {
  $adminHealth = Invoke-WebRequest -UseBasicParsing "http://${adminHost}:$adminPort/admin/healthz" | Select-Object -ExpandProperty Content
  Write-Output "Admin health: $adminHealth"
} catch {
  Write-Output "Admin health: unavailable"
}
