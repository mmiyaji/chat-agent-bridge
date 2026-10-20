param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$resolved = & (Join-Path $PSScriptRoot 'Resolve-ReleaseBuildVersion.ps1') -Version $Tag
if ($Tag -cne $resolved.Tag) { throw 'Use a canonical release tag with the lowercase v prefix.' }
$repository = 'mmiyaji/chat-agent-bridge'
if ($env:GH_REPO -and $env:GH_REPO -cne $repository) { throw 'Publishing is restricted to the application repository.' }
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$files = @()
$expectedAssets = @{}
foreach ($architecture in @('x64', 'ARM64')) {
    $name = "ChatAgentBridge.Portable-$($resolved.ReleaseVersion)-win-$($architecture.ToLowerInvariant()).zip"
    $archive = Join-Path $directory $name
    $checksum = "$archive.sha256"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or -not (Test-Path -LiteralPath $checksum -PathType Leaf)) { throw "Missing archive/checksum: $name" }
    $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = (Get-Content -LiteralPath $checksum -Raw).Trim()
    if ($line -cne "$sha  $name") { throw "Checksum file does not match archive: $name" }
    & (Join-Path $PSScriptRoot 'Create-PortableArchive.ps1') -Architecture $architecture -ValidateOnly -ArchivePath $archive | Out-Host
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry('ChatAgentBridge.Portable/portable-release.json')
        if (-not $entry) { throw "Missing release metadata: $name" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.releaseVersion -cne $resolved.ReleaseVersion) { throw "Archive version differs from tag: $name" }
    } finally { $zip.Dispose() }
    $files += $archive, $checksum
    foreach ($file in @($archive, $checksum)) {
        $expectedAssets[(Split-Path -Leaf $file)] = @{
            Size = (Get-Item -LiteralPath $file).Length
            Digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
if ($ValidateOnly) { Write-Host 'Both portable packages are valid; no GitHub changes made.'; return }

function Invoke-Gh([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
    return $output
}

# Listing avoids treating authentication/network errors as a missing release.
$pages = (Invoke-Gh @('api', '--paginate', '--slurp', "repos/$repository/releases?per_page=100")) | ConvertFrom-Json -NoEnumerate
$existing = @($pages | ForEach-Object { $_ } | Where-Object { $_.tag_name -ceq $Tag })
if ($existing.Count -gt 1) { throw 'Duplicate release tags in API response.' }
if ($existing.Count -eq 0) {
    $arguments = @('release', 'create', $Tag, '--repo', $repository, '--verify-tag', '--draft', '--title', "Chat Agent Bridge $($resolved.ReleaseVersion)", '--generate-notes')
    if ($resolved.Prerelease) { $arguments += '--prerelease' }
    Invoke-Gh $arguments | Out-Host
} elseif ($existing[0].prerelease -ne $resolved.Prerelease) {
    throw 'Existing release channel differs from tag.'
}
$isPublished = $existing.Count -eq 1 -and -not $existing[0].draft
if (-not $isPublished) {
    # Only an unpublished draft can be replaced on a retry.
    Invoke-Gh (@('release', 'upload', $Tag, '--repo', $repository, '--clobber') + $files) | Out-Host
}
$release = (Invoke-Gh @('api', "repos/$repository/releases/tags/$Tag")) | ConvertFrom-Json
if ($release.tag_name -cne $Tag) { throw 'Unexpected release tag.' }
foreach ($asset in $release.assets) {
    if (-not $expectedAssets.ContainsKey($asset.name)) { throw "Unexpected existing release asset: $($asset.name). Remove it from the draft before retrying." }
}
foreach ($name in $expectedAssets.Keys) {
    $asset = @($release.assets | Where-Object { $_.name -ceq $name })
    if ($asset.Count -ne 1 -or $asset[0].size -ne $expectedAssets[$name].Size -or $asset[0].digest -cne $expectedAssets[$name].Digest) {
        throw "Uploaded asset size or SHA-256 differs from local archive: $name"
    }
}
if (-not $isPublished) { Invoke-Gh @('release', 'edit', $Tag, '--repo', $repository, '--draft=false') | Out-Host }
Write-Host "Release verified: https://github.com/$repository/releases/tag/$Tag"
