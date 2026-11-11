<#
.SYNOPSIS
Removes Windows notification registrations left behind by development and test builds of Chat Agent Bridge.

.DESCRIPTION
Portable (unpackaged) builds register a notification identity per executable path on every launch:
  HKCU:\Software\Classes\AppUserModelId\<path-based key>   NotificationGUID -> {GUID}
  HKCU:\Software\Classes\AppUserModelId\{GUID}             DisplayName, IconUri, CustomActivator
  HKCU:\Software\Classes\CLSID\<CustomActivator>          LocalServer32 = "<exe>" ----AppNotificationActivated:
  HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<AUMID>
Every test build in a new folder adds another set, and they appear as separate apps in Windows notification settings.

By default only registrations whose executable no longer exists are removed. -IncludeDevelopmentBuilds also removes
registrations for executables under artifacts, .artifacts, bin or obj folders, or under -Path. Registrations of
running processes are always kept. Packaged (Store / MSIX) registrations are never touched.
Use -WhatIf to preview. Removed keys are exported to -BackupDirectory first.

.EXAMPLE
./desktop/Clear-DevNotificationRegistrations.ps1 -IncludeDevelopmentBuilds -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $IncludeDevelopmentBuilds,
    [string[]] $Path = @(),
    [string] $BackupDirectory = (Join-Path $PSScriptRoot "artifacts/notification-registration-backups/$((Get-Date).ToString('yyyyMMdd-HHmmss'))")
)
$ErrorActionPreference = 'Stop'

$aumidRoot = 'HKCU:\Software\Classes\AppUserModelId'
$clsidRoot = 'HKCU:\Software\Classes\CLSID'
$settingsRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
$exeName = 'ChatAgentBridge.Desktop.exe'
$developmentSegments = @('\artifacts\', '\.artifacts\', '\bin\', '\obj\')
$extraRoots = @($Path | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' })

$running = @(Get-Process -Name 'ChatAgentBridge.Desktop' -ErrorAction SilentlyContinue |
    Where-Object Path | ForEach-Object { [IO.Path]::GetFullPath($_.Path) })

function Get-ActivatorExe([string] $clsid) {
    if (-not $clsid) { return $null }
    $server = (Get-ItemProperty "$clsidRoot\$clsid\LocalServer32" -ErrorAction SilentlyContinue).'(default)'
    if ($server -match '^"([^"]+)"') { return $Matches[1] }
    return $null
}

function Test-Removable([string] $exe) {
    if (-not $exe) { return $true }
    $full = [IO.Path]::GetFullPath($exe)
    if ($running -contains $full) { return $false }
    if (-not (Test-Path -LiteralPath $full)) { return $true }
    if ($IncludeDevelopmentBuilds -and ($developmentSegments | Where-Object { $full.Contains($_, [StringComparison]::OrdinalIgnoreCase) })) { return $true }
    return [bool]($extraRoots | Where-Object { $full.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
}

# GUID identities created by AppNotificationManager.Register for this app.
$identities = foreach ($key in Get-ChildItem $aumidRoot -ErrorAction SilentlyContinue) {
    if ($key.PSChildName -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { continue }
    $values = Get-ItemProperty $key.PSPath
    $exe = Get-ActivatorExe $values.CustomActivator
    $isApp = $values.DisplayName -in @('Chat Agent Bridge', 'ChatAgentBridge') -or
        ($exe -and [IO.Path]::GetFileName($exe) -ieq $exeName)
    if (-not $isApp) { continue }
    [pscustomobject]@{ Key = $key.PSChildName; Exe = $exe; Activator = $values.CustomActivator; Icon = $values.IconUri }
}

$remove = @($identities | Where-Object { Test-Removable $_.Exe })
$removedGuids = @($remove.Key)
$keptGuids = @(@($identities).Key | Where-Object { $removedGuids -notcontains $_ })

# Path-based keys map an executable path to one GUID identity.
$pathKeys = foreach ($key in Get-ChildItem $aumidRoot -ErrorAction SilentlyContinue) {
    if (-not $key.PSChildName.EndsWith(".$exeName", [StringComparison]::OrdinalIgnoreCase)) { continue }
    $guid = (Get-ItemProperty $key.PSPath).NotificationGUID
    if ($guid -and $keptGuids -contains $guid) { continue }
    [pscustomobject]@{ Key = $key.PSChildName; Guid = $guid }
}

Write-Host "App identities: $(@($identities).Count), removable: $($remove.Count), path keys removable: $(@($pathKeys).Count), running builds kept: $($running.Count)"
if ($remove.Count -eq 0 -and @($pathKeys).Count -eq 0) { return }

if ($PSCmdlet.ShouldProcess("$($remove.Count) identities and $(@($pathKeys).Count) path keys", 'Remove notification registrations')) {
    New-Item -ItemType Directory -Force $BackupDirectory | Out-Null
    reg export 'HKCU\Software\Classes\AppUserModelId' (Join-Path $BackupDirectory 'AppUserModelId.reg') /y | Out-Null
    reg export 'HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings' (Join-Path $BackupDirectory 'NotificationSettings.reg') /y | Out-Null
    $index = 0
    foreach ($item in $remove) {
        if ($item.Activator -and (Test-Path "$clsidRoot\$($item.Activator)")) {
            $index++
            reg export "HKCU\Software\Classes\CLSID\$($item.Activator)" (Join-Path $BackupDirectory "clsid-$index.reg") /y | Out-Null
            Remove-Item "$clsidRoot\$($item.Activator)" -Recurse
        }
        $settings = "$settingsRoot\$($item.Key)"
        if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings -Recurse }
        Remove-Item -LiteralPath "$aumidRoot\$($item.Key)" -Recurse
        # Icons that Windows App SDK copied for parameterless registrations.
        $cachedIcon = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsAppSDK\$($item.Key).png"
        if (Test-Path -LiteralPath $cachedIcon) { Remove-Item -LiteralPath $cachedIcon }
    }
    foreach ($item in $pathKeys) {
        $settings = "$settingsRoot\$($item.Key)"
        if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings -Recurse }
        Remove-Item -LiteralPath "$aumidRoot\$($item.Key)" -Recurse
    }
    Write-Host "Removed. Backup: $BackupDirectory"
}
