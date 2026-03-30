$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$pidFile = Join-Path $runtimeDir "bridge.pid"
$port = if ($env:CAB_PORT) { $env:CAB_PORT } else { "8787" }

if (!(Test-Path $pidFile)) {
  Write-Output "Bridge is not running."
  exit 0
}

$bridgeProcessId = Get-Content $pidFile -Raw
$process = Get-Process -Id $bridgeProcessId -ErrorAction SilentlyContinue
if (!$process) {
  Write-Output "Bridge PID file exists but process $bridgeProcessId is not running."
  exit 1
}

Write-Output "Bridge PID: $bridgeProcessId"
try {
  $health = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$port/healthz" | Select-Object -ExpandProperty Content
  Write-Output "Health: $health"
} catch {
  Write-Output "Health: unavailable"
}
