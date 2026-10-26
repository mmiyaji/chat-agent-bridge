# Resets the demo with generated sample activity (dark theme, Japanese) and plays the History and usage scenario.
param([switch]$Record)
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
& (Join-Path $d 'reset-demo.ps1') -Lang ja -Theme dark -SeedActivity
& (Join-Path $d 'run-activity.ps1') -Record:$Record
