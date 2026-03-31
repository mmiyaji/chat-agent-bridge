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

function Get-BridgeProcesses {
  return @(Get-CimInstance Win32_Process |
    Where-Object {
      $_.Name -eq "node.exe" -and
      $_.CommandLine -match "dist/index.js"
    } |
    Sort-Object CreationDate)
}

function Get-BridgeProcessIds {
  return @(Get-BridgeProcesses | Select-Object -ExpandProperty ProcessId)
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
  return @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $adminPort -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique)
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
