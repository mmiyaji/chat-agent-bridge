$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$pidFile = Join-Path $runtimeDir "bridge.pid"
$settingsFile = if ($env:CAB_SETTINGS_FILE) {
  $env:CAB_SETTINGS_FILE
} else {
  Join-Path $repoRoot "data\admin-settings.json"
}

function Get-BridgeProcesses {
  return @(Get-CimInstance Win32_Process |
    Where-Object {
      $_.Name -eq "node.exe" -and
      $_.CommandLine -match "dist/index.js"
    } |
    Sort-Object CreationDate)
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

function Resolve-LoopbackHost {
  param(
    [string]$Value,
    [string]$Fallback
  )

  if ([string]::IsNullOrWhiteSpace($Value)) {
    return $Fallback
  }

  switch ($Value.Trim()) {
    "0.0.0.0" { return "127.0.0.1" }
    "::" { return "127.0.0.1" }
    default { return $Value.Trim() }
  }
}

$bridgeProcesses = Get-BridgeProcesses
$pidFileValue = if (Test-Path $pidFile) { (Get-Content $pidFile -Raw).Trim() } else { "" }
$adminListenerProcessIds = Get-AdminListenerProcessIds

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
} elseif (Read-SettingsValue "port") {
  [int](Read-SettingsValue "port")
} else {
  8787
}

$adminPort = if ($env:CAB_ADMIN_PORT) {
  [int]$env:CAB_ADMIN_PORT
} elseif (Read-SettingsValue "adminPort") {
  [int](Read-SettingsValue "adminPort")
} else {
  $port + 1
}

$bridgeHost = Resolve-LoopbackHost -Value $(if ($env:CAB_HOST) { $env:CAB_HOST } elseif (Read-SettingsValue "host") { [string](Read-SettingsValue "host") } else { "127.0.0.1" }) -Fallback "127.0.0.1"
$adminHost = Resolve-LoopbackHost -Value $(if ($env:CAB_ADMIN_HOST) { $env:CAB_ADMIN_HOST } elseif (Read-SettingsValue "adminHost") { [string](Read-SettingsValue "adminHost") } elseif (Read-SettingsValue "host") { [string](Read-SettingsValue "host") } else { "127.0.0.1" }) -Fallback "127.0.0.1"

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
