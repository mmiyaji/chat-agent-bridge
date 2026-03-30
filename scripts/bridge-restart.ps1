$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot "bridge-stop.ps1")
& (Join-Path $PSScriptRoot "bridge-start.ps1")
