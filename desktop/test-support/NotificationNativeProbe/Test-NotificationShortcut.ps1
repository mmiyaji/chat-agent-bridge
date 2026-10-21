[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Create', 'Verify', 'Remove')][string] $Mode,
    [Parameter(Mandatory)][string] $PublishedExe,
    [Parameter(Mandatory)][string] $ShortcutPath,
    [string] $TargetArguments = '',
    [string] $ExplicitAppId,
    [string] $IconPath
)

$ErrorActionPreference = 'Stop'
$probeIconPath = ''
if ($IconPath) {
    if (-not [IO.Path]::IsPathFullyQualified($IconPath) -or [IO.Path]::GetExtension($IconPath) -ne '.ico' -or
        -not (Test-Path -LiteralPath $IconPath -PathType Leaf)) { throw 'IconPath must be an absolute existing .ico file.' }
    $probeIconPath = [IO.Path]::GetFullPath($IconPath)
}
if ($ExplicitAppId -and ($ExplicitAppId.Length -gt 128 -or $ExplicitAppId -cnotmatch '\AChatAgentBridge\.Diagnostics\.NotificationProbe\.[A-Za-z0-9][A-Za-z0-9.-]*\z')) {
    throw 'ExplicitAppId must use the allowed ChatAgentBridge.Diagnostics.NotificationProbe. namespace (maximum 128 characters).'
}
if ($ExplicitAppId -and $TargetArguments -cnotmatch ('(?:^|\s)--app-id\s+"?' + [regex]::Escape($ExplicitAppId) + '"?(?=\s|$)')) {
    throw 'TargetArguments must contain the matching --app-id diagnostic identity.'
}
if (-not [Environment]::Is64BitProcess) { throw 'Run this diagnostic in 64-bit PowerShell.' }
if (-not [IO.Path]::IsPathFullyQualified($PublishedExe) -or -not [IO.Path]::IsPathFullyQualified($ShortcutPath)) {
    throw 'Both paths must be absolute.'
}
$probeExecutable = [IO.Path]::GetFullPath($PublishedExe)
$probeShortcut = [IO.Path]::GetFullPath($ShortcutPath)
$programsDirectory = [IO.Path]::GetFullPath([Environment]::GetFolderPath('Programs'))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($probeShortcut), $programsDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($probeShortcut) -ne '.lnk') {
    throw 'Shortcut must be a .lnk directly inside the current user Start Menu Programs directory.'
}
if (-not [string]::Equals([IO.Path]::GetFileName($probeExecutable), 'NotificationNativeProbe.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This utility only accepts NotificationNativeProbe.exe.'
}
if (-not (Test-Path -LiteralPath $probeExecutable -PathType Leaf)) { throw 'Probe executable does not exist.' }
if ((Get-Item -LiteralPath $programsDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Programs directory must not be a reparse point.' }
if (Test-Path -LiteralPath $probeShortcut) {
    if ((Get-Item -LiteralPath $probeShortcut).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Shortcut must not be a reparse point.' }
    if ($Mode -eq 'Create') { throw 'Refusing to overwrite an existing shortcut.' }
} elseif ($Mode -ne 'Create') { throw 'Shortcut does not exist.' }

if (-not ('NotificationNativeProbe.ShortcutDiagnostic.ShortcutFile' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ProbeShortcutInterop.cs')
}
function Read-CheckedShortcut {
    $result = [NotificationNativeProbe.ShortcutDiagnostic.ShortcutFile]::Read($probeShortcut)
    if (-not [string]::Equals($result.Target, $probeExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $result.Arguments -cne $TargetArguments -or
        -not [string]::Equals($result.WorkingDirectory, [IO.Path]::GetDirectoryName($probeExecutable), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Shortcut target/arguments/working directory do not match the supplied exact probe values.'
    }
    if ($probeIconPath -and (-not [string]::Equals($result.IconPath, $probeIconPath, [StringComparison]::OrdinalIgnoreCase) -or $result.IconIndex -ne 0)) {
        throw 'Shortcut icon path/index do not match the supplied .ico file at index zero.'
    }
    return $result
}

if ($Mode -eq 'Remove') {
    $verified = Read-CheckedShortcut
    Remove-Item -LiteralPath $probeShortcut
    [pscustomobject]@{ mode = $Mode; shortcut = $probeShortcut; removed = -not (Test-Path -LiteralPath $probeShortcut); evidence = $verified } | ConvertTo-Json -Depth 4
    return
}

# Match SDK ConvertPathToKey and use its existing generated identity. Read registry only.
$mappedPath = $probeExecutable.Replace('\', '.')
$notificationAppId = if ($ExplicitAppId) { $ExplicitAppId } else {
    (Get-ItemProperty -LiteralPath ('HKCU:\Software\Classes\AppUserModelId\' + $mappedPath) -Name NotificationGUID).NotificationGUID
}
$activator = (Get-ItemProperty -LiteralPath ('HKCU:\Software\Classes\AppUserModelId\' + $notificationAppId) -Name CustomActivator).CustomActivator
$parsedActivator = [guid]::Parse($activator)
$serverKey = Get-Item -LiteralPath ('HKCU:\Software\Classes\CLSID\' + $activator + '\LocalServer32')
$serverCommand = [string]$serverKey.GetValue('')
$expectedServerCommand = '"' + $probeExecutable + '" ----AppNotificationActivated:'
if (-not [string]::Equals($serverCommand, $expectedServerCommand, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Existing CustomActivator LocalServer32 does not match the exact probe executable/SDK activation command.'
}
if ($Mode -eq 'Create') {
    [NotificationNativeProbe.ShortcutDiagnostic.ShortcutFile]::Create($probeShortcut, $probeExecutable, $TargetArguments, $notificationAppId, $parsedActivator, $probeIconPath)
}
$verified = Read-CheckedShortcut
if ($verified.AppUserModelId -cne $notificationAppId -or $verified.ToastActivatorClsid -ne $parsedActivator) {
    throw 'Shortcut identity does not match the existing SDK registration.'
}
[pscustomobject]@{ mode = $Mode; shortcut = $probeShortcut; registryReadOnly = $true; evidence = $verified } | ConvertTo-Json -Depth 4
