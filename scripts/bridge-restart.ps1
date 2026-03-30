param(
  [switch]$Build
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

$stopScript = Join-Path $PSScriptRoot "bridge-stop.ps1"
$startScript = Join-Path $PSScriptRoot "bridge-start.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript
if ($Build) {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript
} else {
  powershell -NoProfile -ExecutionPolicy Bypass -File $startScript -SkipBuild
}
