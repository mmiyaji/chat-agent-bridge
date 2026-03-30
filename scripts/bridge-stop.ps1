$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$pidFile = $script:BridgePidFile

if (!(Test-Path $pidFile)) {
  $orphanProcessIds = @(Get-BridgeProcessIds) + @(Get-BridgeAdminListenerProcessIds)
  if ($orphanProcessIds.Count -eq 0) {
    Write-Output "Bridge is not running."
    exit 0
  }

  foreach ($processId in ($orphanProcessIds | Select-Object -Unique)) {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
    Write-Output "Stopped bridge process $processId."
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
}

Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
