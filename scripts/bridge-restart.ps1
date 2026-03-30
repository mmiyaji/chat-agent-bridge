param(
  [switch]$Build
)

$ErrorActionPreference = "Stop"

$stopScript = if ($env:CAB_BRIDGE_STOP_SCRIPT) { $env:CAB_BRIDGE_STOP_SCRIPT } else { Join-Path $PSScriptRoot "bridge-stop.ps1" }
$startScript = if ($env:CAB_BRIDGE_START_SCRIPT) { $env:CAB_BRIDGE_START_SCRIPT } else { Join-Path $PSScriptRoot "bridge-start.ps1" }

powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript
if ($Build) {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript
} else {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript -SkipBuild
}
