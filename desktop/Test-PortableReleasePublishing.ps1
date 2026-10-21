param(
    [string]$Tag = 'v0.0.0-preview.0',
    [Parameter(Mandatory)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'

$publishScript = Join-Path $PSScriptRoot 'Publish-PortableRelease.ps1'
$resolved = & (Join-Path $PSScriptRoot 'Resolve-ReleaseBuildVersion.ps1') -Version $Tag
if ($Tag -cne $resolved.Tag) { throw 'Use a canonical release tag with the lowercase v prefix.' }
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path

$previousGhRepo = [Environment]::GetEnvironmentVariable('GH_REPO', 'Process')
$mockGhBox = @{ State = $null }
$previousLastExitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadLastExitCode = $null -ne $previousLastExitCodeVariable
$lastExitCodeValue = if ($hadLastExitCode) { $previousLastExitCodeVariable.Value } else { $null }

$ghMock = {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $state = $mockGhBox.State
    if ($null -eq $state) { throw 'Mock GitHub CLI is not configured.' }
    $global:LASTEXITCODE = 0
    if ($Arguments.Count -ge 3 -and $Arguments[0] -ceq 'api' -and $Arguments[1] -ceq '--paginate') {
        $state.Actions.Add('list')
        if ($state.ListFailure) {
            $global:LASTEXITCODE = 1
            return 'Mock GitHub API authentication/list failure.'
        }
        $pages = @(@($state.Releases))
        return ConvertTo-Json -InputObject $pages -Depth 20 -Compress
    }
    if ($Arguments.Count -ge 2 -and $Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'create') {
        $state.Actions.Add('create')
        $state.Release = [pscustomobject]@{
            tag_name = $Arguments[2]
            draft = $true
            prerelease = $Arguments -ccontains '--prerelease'
            assets = @()
        }
        $state.Releases = @($state.Release)
        return 'created draft'
    }
    if ($Arguments.Count -ge 2 -and $Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'upload') {
        $state.Actions.Add('upload')
        if (-not $state.Release) { throw 'Mock upload had no draft release.' }
        $assets = @()
        foreach ($file in $Arguments | Select-Object -Skip 6) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Mock upload path does not exist: $file" }
            $info = Get-Item -LiteralPath $file
            $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($state.DigestMismatch -and $assets.Count -eq 0) { $digest = 'sha256:' + ('0' * 64) }
            $assets += [pscustomobject]@{ name = $info.Name; size = $info.Length; digest = $digest }
        }
        $state.Release.assets = $assets
        return 'uploaded assets'
    }
    if ($Arguments.Count -ge 2 -and $Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'edit') {
        $state.Actions.Add('publish')
        $state.Release.draft = $false
        return 'published release'
    }
    throw "Unexpected GitHub CLI command in offline test: $($Arguments -join ' ')"
}.GetNewClosure()
Set-Item -Path Function:gh -Value $ghMock

function New-MockState {
    param([switch]$ListFailure, [switch]$DigestMismatch, [object[]]$ExistingReleases = @())
    return [pscustomobject]@{
        Actions = [System.Collections.Generic.List[string]]::new()
        ListFailure = [bool]$ListFailure
        DigestMismatch = [bool]$DigestMismatch
        Releases = @($ExistingReleases)
        Release = if ($ExistingReleases.Count -gt 0) { $ExistingReleases[0] } else { $null }
    }
}

function Invoke-Publish {
    & $publishScript -Tag $Tag -PackageDirectory $packagePath | Out-Null
}

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if (($Expected -join ',') -cne ($Actual -join ',')) {
        throw "$Message Expected [$($Expected -join ',')], got [$($Actual -join ',')]."
    }
}

try {
    $resolvedGh = Get-Command gh -ErrorAction Stop
    if ($resolvedGh.CommandType -ne 'Function' -or $resolvedGh.ScriptBlock -ne $ghMock) {
        throw 'The gh command is shadowed by an alias or another command; refusing to run the publishing test.'
    }
    # Keep the test pinned to this repository even if the caller has GH_REPO set.
    $env:GH_REPO = 'mmiyaji/chat-agent-bridge'

    $mockGhBox.State = New-MockState
    Invoke-Publish
    Assert-Equal @('list', 'create', 'upload', 'list', 'publish') $mockGhBox.State.Actions 'Draft creation/upload/verification/publish sequence changed.'
    if ($mockGhBox.State.Release.draft) { throw 'Happy-path mock release was not published.' }
    if (@($mockGhBox.State.Release.assets).Count -ne 4) { throw 'Happy-path mock release did not verify all four archive/checksum assets.' }
    Write-Host 'PASS: creates a draft, uploads both architectures, verifies all assets, and publishes.'

    $mockGhBox.State = New-MockState -DigestMismatch
    $mismatchRejected = $false
    try { Invoke-Publish } catch { $mismatchRejected = $true }
    if (-not $mismatchRejected) { throw 'Digest mismatch unexpectedly reached successful publication.' }
    Assert-Equal @('list', 'create', 'upload', 'list') $mockGhBox.State.Actions 'Digest mismatch must stop before publish.'
    if (-not $mockGhBox.State.Release.draft) { throw 'Digest mismatch published the release.' }
    Write-Host 'PASS: digest mismatch leaves the release as a draft.'

    $publishedAssets = @()
    foreach ($architecture in @('x64', 'ARM64')) {
        $platform = $architecture.ToLowerInvariant()
        $archiveName = "ChatAgentBridge.Portable-$($resolved.ReleaseVersion)-win-$platform.zip"
        foreach ($name in @($archiveName, "$archiveName.sha256")) {
            $path = Join-Path $packagePath $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing archive/checksum: $name" }
            $file = Get-Item -LiteralPath $path
            $publishedAssets += [pscustomobject]@{
                name = $name
                size = $file.Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    $existingPublished = [pscustomobject]@{
        tag_name = $Tag
        draft = $false
        prerelease = [bool]$resolved.Prerelease
        assets = $publishedAssets
    }
    $mockGhBox.State = New-MockState -ExistingReleases @($existingPublished)
    Invoke-Publish
    Assert-Equal @('list', 'list') $mockGhBox.State.Actions 'A matching published release must only be verified.'
    Write-Host 'PASS: a matching published release is verified without replacing or uploading assets.'

    $mockGhBox.State = New-MockState -ListFailure
    $listFailureRejected = $false
    try { Invoke-Publish } catch { $listFailureRejected = $true }
    if (-not $listFailureRejected) { throw 'GitHub list/authentication failure was treated as a missing release.' }
    Assert-Equal @('list') $mockGhBox.State.Actions 'List failure must stop before create/upload/publish.'
    Write-Host 'PASS: GitHub list/authentication failure does not create a release.'
}
finally {
    [Environment]::SetEnvironmentVariable('GH_REPO', $previousGhRepo, 'Process')
    $mockGhBox.State = $null
    if ($hadLastExitCode) {
        $global:LASTEXITCODE = $lastExitCodeValue
    } else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}
