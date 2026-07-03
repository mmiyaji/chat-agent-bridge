$script:BridgeRepoRoot = Split-Path -Parent $PSScriptRoot
$script:BridgeRuntimeDir = Join-Path $script:BridgeRepoRoot "data\runtime"
$script:BridgePidFile = Join-Path $script:BridgeRuntimeDir "bridge.pid"
$script:BridgeSettingsFile = if ($env:CAB_SETTINGS_FILE) {
  $env:CAB_SETTINGS_FILE
} else {
  Join-Path $script:BridgeRepoRoot "data\admin-settings.json"
}
$script:BridgeAuditLogFile = if ($env:CAB_AUDIT_LOG_FILE) {
  $env:CAB_AUDIT_LOG_FILE
} else {
  Join-Path $script:BridgeRepoRoot "data\audit.jsonl"
}
$script:BridgePowerShellPath = Join-Path $(if ($env:SystemRoot) { $env:SystemRoot } else { "C:\Windows" }) "System32\WindowsPowerShell\v1.0\powershell.exe"

function Read-BridgeSettingsValue {
  param(
    [Parameter(Mandatory = $true)][string]$Key
  )

  if (!(Test-Path $script:BridgeSettingsFile)) {
    return $null
  }

  try {
    $raw = Get-Content $script:BridgeSettingsFile -Raw | ConvertFrom-Json
    return $raw.$Key
  } catch {
    return $null
  }
}

function Get-BridgePidFileProcessId {
  if (!(Test-Path $script:BridgePidFile)) {
    return $null
  }

  try {
    $raw = (Get-Content $script:BridgePidFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
      return $null
    }
    return [int]$raw
  } catch {
    return $null
  }
}

function Test-BridgeProcessId {
  param(
    [Parameter(Mandatory = $true)][int]$ProcessId
  )

  $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (!$process) {
    return $false
  }

  $processName = $process.ProcessName
  $isNode = ($processName -eq "node") -or ($processName -eq "node.exe")
  if (!$isNode) {
    return $false
  }

  # Best effort: confirm the command line points at the bridge entry point.
  # If the command line cannot be read (missing cmdlet, access denied, WMI
  # failure), fall back to the name-only check above.
  if (!(Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
    return $true
  }

  try {
    $cimProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    if ($null -eq $cimProcess) {
      return $true
    }
    $commandLine = $cimProcess.CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) {
      return $true
    }
    return [bool]($commandLine -match "dist[\\/]index\.js")
  } catch {
    return $true
  }
}

function Get-BridgeProcessFromPidFile {
  $processId = Get-BridgePidFileProcessId
  if ($null -eq $processId) {
    return $null
  }

  if (!(Test-BridgeProcessId -ProcessId $processId)) {
    return $null
  }

  return Get-Process -Id $processId -ErrorAction SilentlyContinue
}

function Get-BridgeProcesses {
  $primaryProcess = Get-BridgeProcessFromPidFile
  if ($primaryProcess) {
    return @($primaryProcess)
  }

  if (!(Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
    return @()
  }

  try {
    return @(Get-CimInstance Win32_Process |
      Where-Object {
        $_.Name -eq "node.exe" -and
        $_.CommandLine -match "dist/index.js"
      } |
      Sort-Object CreationDate)
  } catch {
    return @()
  }
}

function Get-BridgeProcessIds {
  return @(
    Get-BridgeProcesses |
      ForEach-Object {
        if ($null -ne $_.ProcessId) {
          $_.ProcessId
          return
        }
        if ($null -ne $_.Id) {
          $_.Id
        }
      } |
      Where-Object { $null -ne $_ } |
      Select-Object -Unique
  )
}

function Get-BridgeAdminPort {
  if ($env:CAB_ADMIN_PORT) {
    return [int]$env:CAB_ADMIN_PORT
  }

  $persistedAdminPort = Read-BridgeSettingsValue "adminPort"
  if ($persistedAdminPort) {
    return [int]$persistedAdminPort
  }

  $basePort = if ($env:CAB_PORT) {
    [int]$env:CAB_PORT
  } elseif (Read-BridgeSettingsValue "port") {
    [int](Read-BridgeSettingsValue "port")
  } else {
    8787
  }

  return $basePort + 1
}

function Get-BridgeAdminListenerProcessIds {
  $adminPort = Get-BridgeAdminPort
  if (!(Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
    return @()
  }

  try {
    return @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $adminPort -State Listen -ErrorAction SilentlyContinue |
      Select-Object -ExpandProperty OwningProcess -Unique |
      Where-Object { Test-BridgeProcessId -ProcessId $_ })
  } catch {
    return @()
  }
}

function Resolve-BridgeLoopbackHost {
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

function Get-BridgePowerShellPath {
  return $script:BridgePowerShellPath
}

function Import-BridgeEnvironment {
  param(
    [Parameter(Mandatory = $true)][string[]]$Names
  )

  foreach ($name in $Names) {
    if ([string]::IsNullOrWhiteSpace((Get-Item "Env:$name" -ErrorAction SilentlyContinue).Value)) {
      $userValue = [Environment]::GetEnvironmentVariable($name, "User")
      if (![string]::IsNullOrWhiteSpace($userValue)) {
        Set-Item -Path "Env:$name" -Value $userValue
      }
    }
  }
}

function Refresh-BridgePathEnvironment {
  try {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $segments = @()

    foreach ($value in @($machinePath, $userPath)) {
      if ([string]::IsNullOrWhiteSpace($value)) {
        continue
      }

      foreach ($segment in $value.Split(";")) {
        $trimmed = $segment.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
          continue
        }
        if ($segments -contains $trimmed) {
          continue
        }
        $segments += $trimmed
      }
    }

    if ($segments.Count -gt 0) {
      $joined = $segments -join ";"
      Set-Item -Path "Env:Path" -Value $joined
      Set-Item -Path "Env:PATH" -Value $joined
    }
  } catch {
    # Best effort only. Service lifecycle should not fail because PATH refresh failed.
  }
}

function Write-BridgeAuditEvent {
  param(
    [Parameter(Mandatory = $true)][string]$Type,
    [string]$Actor,
    [string]$Reason,
    [hashtable]$Fields
  )

  try {
    $payload = @{
      timestamp = [DateTime]::UtcNow.ToString("o")
      type = $Type
      source = "service-script"
    }

    if (![string]::IsNullOrWhiteSpace($Actor)) {
      $payload.actor = $Actor
    }

    if (![string]::IsNullOrWhiteSpace($Reason)) {
      $payload.reason = $Reason
    }

    if ($Fields) {
      foreach ($entry in $Fields.GetEnumerator()) {
        $payload[$entry.Key] = $entry.Value
      }
    }

    $auditPath = $script:BridgeAuditLogFile
    $auditDir = Split-Path -Parent $auditPath
    if ($auditDir) {
      New-Item -ItemType Directory -Force -Path $auditDir | Out-Null
    }
    ($payload | ConvertTo-Json -Compress -Depth 8) | Add-Content -Path $auditPath -Encoding utf8
  } catch {
    # Best effort only. Service lifecycle should not fail because audit logging could not write.
  }
}
