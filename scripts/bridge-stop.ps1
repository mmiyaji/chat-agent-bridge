$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$pidFile = Join-Path $runtimeDir "bridge.pid"

if (!(Test-Path $pidFile)) {
  Write-Output "Bridge is not running."
  exit 0
}

$bridgeProcessId = Get-Content $pidFile -Raw
$process = Get-Process -Id $bridgeProcessId -ErrorAction SilentlyContinue
if ($process) {
  Stop-Process -Id $bridgeProcessId -Force
  Write-Output "Stopped bridge process $bridgeProcessId."
} else {
  Write-Output "No running process was found for PID $bridgeProcessId."
}

Remove-Item $pidFile -Force
