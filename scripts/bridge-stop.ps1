$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$pidFile = Join-Path $runtimeDir "bridge.pid"
$settingsFile = if ($env:CAB_SETTINGS_FILE) {
  $env:CAB_SETTINGS_FILE
} else {
  Join-Path $repoRoot "data\admin-settings.json"
}

function Read-SettingsValue {
  param(
    [Parameter(Mandatory = $true)][string]$Key
  )

  if (!(Test-Path $settingsFile)) {
    return $null
  }

  try {
    $raw = Get-Content $settingsFile -Raw | ConvertFrom-Json
    return $raw.$Key
  } catch {
    return $null
  }
}

function Get-BridgeProcessIds {
  return @(Get-CimInstance Win32_Process |
    Where-Object {
      $_.Name -eq "node.exe" -and
      $_.CommandLine -match "dist/index.js"
    } |
    Select-Object -ExpandProperty ProcessId)
}

function Get-AdminListenerProcessIds {
  $adminPort = if ($env:CAB_ADMIN_PORT) {
    [int]$env:CAB_ADMIN_PORT
  } elseif (Read-SettingsValue "adminPort") {
    [int](Read-SettingsValue "adminPort")
  } else {
    $basePort = if ($env:CAB_PORT) { [int]$env:CAB_PORT } elseif (Read-SettingsValue "port") { [int](Read-SettingsValue "port") } else { 8787 }
    $basePort + 1
  }

  return @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $adminPort -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique)
}

if (!(Test-Path $pidFile)) {
  $orphanProcessIds = @(Get-BridgeProcessIds) + @(Get-AdminListenerProcessIds)
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
$processIds = @($bridgeProcessId) + (Get-BridgeProcessIds | Where-Object { $_ -ne [int]$bridgeProcessId }) + (Get-AdminListenerProcessIds | Where-Object { $_ -ne [int]$bridgeProcessId })
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
