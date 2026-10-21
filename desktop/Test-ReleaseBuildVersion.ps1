$ErrorActionPreference = 'Stop'
$resolve = Join-Path $PSScriptRoot 'Resolve-ReleaseBuildVersion.ps1'
foreach ($case in @(@('v1.2.3', '1.2.3', '1.2.3.0', $false), @('1.2.3-preview.10', '1.2.3-preview.10', '1.2.3.10', $true))) {
    $result = & $resolve -Version $case[0]
    if ($result.ReleaseVersion -cne $case[1] -or $result.ApplicationVersion -cne $case[2] -or $result.Prerelease -ne $case[3]) { throw "Unexpected version result for $($case[0])" }
}
foreach ($invalid in @('v1.2', 'v01.2.3', 'v1.2.3-rc.1', 'v1.2.3-preview.01', 'v65535.0.0', 'v1.0.0-preview.65535', 'V1.2.3', "1.2.3`nmalicious")) {
    $rejected = $false
    try { & $resolve -Version $invalid | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid version was accepted: $invalid" }
}
Write-Host 'Release version tests passed.'
