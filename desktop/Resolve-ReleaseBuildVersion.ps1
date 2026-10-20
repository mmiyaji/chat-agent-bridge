param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview\.(0|[1-9][0-9]*))?$') {
    throw 'Version must be X.Y.Z or X.Y.Z-preview.N with an optional lowercase v prefix.'
}
$parts = @($Matches[1], $Matches[2], $Matches[3], $(if ($Matches.ContainsKey(4)) { $Matches[4] } else { '0' }))
foreach ($part in $parts) {
    $number = 0
    if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65534) { throw 'Version components must be between 0 and 65534.' }
}
$releaseVersion = $Version.TrimStart('v')
[pscustomobject]@{
    ReleaseVersion = $releaseVersion
    ApplicationVersion = $parts -join '.'
    Tag = "v$releaseVersion"
    Prerelease = $releaseVersion.Contains('-preview.')
}
