[CmdletBinding()]
param(
    [string] $PublishDirectory,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-PortableNotices([string] $Root, [string] $InventoryPath) {
    $rootPath = (Resolve-Path -LiteralPath $Root).Path
    $noticePath = Join-Path $rootPath 'THIRD-PARTY-NOTICES.txt'
    $licenseDir = Join-Path $rootPath 'Assets/Licenses'
    $catalogPath = Join-Path $licenseDir 'catalog.json'
    $publishedInventoryPath = Join-Path $licenseDir 'inventory.json'
    $depsPath = Join-Path $rootPath 'ChatAgentBridge.Desktop.deps.json'
    $runtimeConfigPath = Join-Path $rootPath 'ChatAgentBridge.Desktop.runtimeconfig.json'

    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) { throw 'Published root THIRD-PARTY-NOTICES.txt is missing.' }
    $catalog = @(Get-JsonFile $catalogPath)
    if ($catalog.Count -eq 0) { throw 'The license catalog is empty.' }
    $sourceInventory = Get-JsonFile $InventoryPath
    if ((Get-Sha256 $publishedInventoryPath) -ne (Get-Sha256 $InventoryPath)) {
        throw 'Published Assets/Licenses/inventory.json differs from the reviewed source inventory.'
    }
    $inventoryFiles = @{}
    foreach ($entry in $sourceInventory.noticeDocuments) { $inventoryFiles[$entry.file] = $entry }

    foreach ($entry in $catalog) {
        if ([string]::IsNullOrWhiteSpace($entry.name) -or [string]::IsNullOrWhiteSpace($entry.components)) {
            throw 'Each catalog entry must have a name and component description.'
        }
        if ($entry.file -notmatch '^[A-Za-z0-9._-]+$') { throw "Catalog file must be a bare filename: $($entry.file)" }
        $filePath = Join-Path $licenseDir $entry.file
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Catalog references a missing notice file: $($entry.file)" }
        if (-not $inventoryFiles.ContainsKey($entry.file)) { throw "Catalog notice is absent from the source inventory: $($entry.file)" }
        if ((Get-Sha256 $filePath) -ne $inventoryFiles[$entry.file].sha256) { throw "Notice file hash does not match inventory: $($entry.file)" }
    }
    $catalogFileNames = @($catalog | ForEach-Object { [string]$_.file } | Sort-Object -Unique)
    if ($catalogFileNames.Count -ne $catalog.Count) { throw 'The license catalog contains duplicate file entries.' }
    $inventoryFileNames = @($sourceInventory.noticeDocuments | ForEach-Object { [string]$_.file } | Sort-Object -Unique)
    if (($catalogFileNames.Count -ne $inventoryFileNames.Count) -or
        (Compare-Object -ReferenceObject $catalogFileNames -DifferenceObject $inventoryFileNames)) {
        throw 'The license catalog and notice inventory do not list the same documents.'
    }

    $aggregate = $sourceInventory.aggregateNotice
    if ((Get-Sha256 $noticePath) -ne $aggregate.sha256) { throw 'Published root THIRD-PARTY-NOTICES.txt does not match the reviewed source notice.' }

    $deps = Get-JsonFile $depsPath
    $runtimeConfig = Get-JsonFile $runtimeConfigPath
    $expectedRuntimeVersion = [string]$sourceInventory.runtimePack.version
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }) | Select-Object -First 1
    if ($null -eq $framework -or [string]$framework.version -ne $expectedRuntimeVersion) {
        $actual = if ($null -eq $framework) { '<missing>' } else { [string]$framework.version }
        throw "Self-contained .NET runtime version mismatch: inventory=$expectedRuntimeVersion, publish=$actual. Review and update the license inventory and notices for the new runtime."
    }

    $packageMap = @{}
    foreach ($package in $sourceInventory.packages) {
        $packageMap["$($package.id)/$($package.version)"] = $package
    }
    $noticeFiles = @{}
    foreach ($entry in $catalog) { $noticeFiles[$entry.file] = $true }
    $publishedLibraryMap = @{}
    foreach ($library in $sourceInventory.publishedLibraries) {
        $publishedLibraryMap[$library.identity] = $library
    }
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'package') {
            if (-not $packageMap.ContainsKey($library.Name)) { throw "Published runtime package is missing from inventory: $($library.Name)" }
            $package = $packageMap[$library.Name]
            if ([string]::IsNullOrWhiteSpace($package.noticeFile) -or -not $noticeFiles.ContainsKey([string]$package.noticeFile)) {
                throw "Published runtime package has no catalogued notice document: $($library.Name)"
            }
        } elseif ($library.Value.type -in @('runtimepack', 'reference')) {
            if (-not $publishedLibraryMap.ContainsKey($library.Name)) { throw "Published runtime library is missing from inventory: $($library.Name)" }
            $runtimeLibrary = $publishedLibraryMap[$library.Name]
            if ([string]::IsNullOrWhiteSpace($runtimeLibrary.noticeFile) -or -not $noticeFiles.ContainsKey([string]$runtimeLibrary.noticeFile)) {
                throw "Published runtime library has no catalogued notice document: $($library.Name)"
            }
        }
    }

    return [pscustomobject]@{
        RuntimeVersion = $expectedRuntimeVersion
        RuntimePackages = @($deps.libraries.PSObject.Properties | Where-Object {$_.Value.type -eq 'package'}).Count
        CatalogDocuments = $catalog.Count
        AggregateNoticeBytes = (Get-Item -LiteralPath $noticePath).Length
    }
}

function Write-FixtureFile([string] $Path, [string] $Text) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Set-Content -LiteralPath $Path -Value $Text -Encoding utf8
}

function Invoke-SelfTest {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cab-third-party-notices-' + [guid]::NewGuid().ToString('N'))
    $sourceInventoryPath = Join-Path $tempRoot 'source-inventory.json'
    $publishRoot = Join-Path $tempRoot 'publish'
    $licenseDir = Join-Path $publishRoot 'Assets/Licenses'
    try {
        New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
        Write-FixtureFile (Join-Path $licenseDir 'LICENSE-Test.txt') 'test license text'
        Write-FixtureFile (Join-Path $publishRoot 'THIRD-PARTY-NOTICES.txt') 'test aggregate notice'
        $noticeHash = Get-Sha256 (Join-Path $publishRoot 'THIRD-PARTY-NOTICES.txt')
        $licenseHash = Get-Sha256 (Join-Path $licenseDir 'LICENSE-Test.txt')
        $catalog = @(@{name='Test component';file='LICENSE-Test.txt';components='Test.Library 1.0.0'})
        $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $licenseDir 'catalog.json') -Encoding utf8
        $inventory = [ordered]@{
            schemaVersion = 1
            runtimePack = @{version='10.0.13'}
            packages = @(@{id='Test.Library';version='1.0.0';noticeFile='LICENSE-Test.txt'})
            publishedLibraries = @(
                @{identity='runtimepack.Test.SDK/1.0.0';type='runtimepack';noticeFile='LICENSE-Test.txt'},
                @{identity='runtimepack.Microsoft.NETCore.App.Runtime.win-arm64/10.0.13';type='runtimepack';noticeFile='LICENSE-Test.txt'},
                @{identity='Test.Reference/1.0.0';type='reference';noticeFile='LICENSE-Test.txt'}
            )
            noticeDocuments = @(@{file='LICENSE-Test.txt';sha256=$licenseHash})
            aggregateNotice = @{sha256=$noticeHash}
        }
        $inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $sourceInventoryPath -Encoding utf8
        $inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $licenseDir 'inventory.json') -Encoding utf8
        $fixtureLibraries = @{
            'Test.Library/1.0.0' = @{type='package'}
            'runtimepack.Test.SDK/1.0.0' = @{type='runtimepack'}
            'runtimepack.Microsoft.NETCore.App.Runtime.win-arm64/10.0.13' = @{type='runtimepack'}
            'Test.Reference/1.0.0' = @{type='reference'}
        }
        Write-FixtureFile (Join-Path $publishRoot 'ChatAgentBridge.Desktop.deps.json') (@{libraries=$fixtureLibraries} | ConvertTo-Json -Depth 5)
        Write-FixtureFile (Join-Path $publishRoot 'ChatAgentBridge.Desktop.runtimeconfig.json') (@{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.13'})}} | ConvertTo-Json -Depth 5)

        [void](Assert-PortableNotices $publishRoot $sourceInventoryPath)

        Remove-Item -LiteralPath (Join-Path $licenseDir 'LICENSE-Test.txt')
        $failed = $false
        try { [void](Assert-PortableNotices $publishRoot $sourceInventoryPath) } catch { $failed = $true }
        if (-not $failed) { throw 'Self-test failed: missing catalog file was accepted.' }
        Write-FixtureFile (Join-Path $licenseDir 'LICENSE-Test.txt') 'test license text'

        $fixtureLibraries['Test.Library/2.0.0'] = $fixtureLibraries['Test.Library/1.0.0']
        $fixtureLibraries.Remove('Test.Library/1.0.0')
        Write-FixtureFile (Join-Path $publishRoot 'ChatAgentBridge.Desktop.deps.json') (@{libraries=$fixtureLibraries} | ConvertTo-Json -Depth 5)
        $failed = $false
        try { [void](Assert-PortableNotices $publishRoot $sourceInventoryPath) } catch { $failed = $true }
        if (-not $failed) { throw 'Self-test failed: a runtime package missing from the inventory was accepted.' }
        $fixtureLibraries['Test.Library/1.0.0'] = $fixtureLibraries['Test.Library/2.0.0']
        $fixtureLibraries.Remove('Test.Library/2.0.0')
        Write-FixtureFile (Join-Path $publishRoot 'ChatAgentBridge.Desktop.deps.json') (@{libraries=$fixtureLibraries} | ConvertTo-Json -Depth 5)

        Write-FixtureFile (Join-Path $publishRoot 'ChatAgentBridge.Desktop.runtimeconfig.json') (@{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.12'})}} | ConvertTo-Json -Depth 5)
        $failed = $false
        try { [void](Assert-PortableNotices $publishRoot $sourceInventoryPath) } catch { $failed = $true }
        if (-not $failed) { throw 'Self-test failed: a runtime version mismatch was accepted.' }
        Write-Output 'Third-party notice validator self-tests passed (valid fixture, missing notice, missing dependency, runtime version mismatch).'
    }
    finally {
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
        $expectedPrefix = 'cab-third-party-notices-'
        if ($resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedTempRoot).StartsWith($expectedPrefix, [StringComparison]::Ordinal) -and
            (Test-Path -LiteralPath $resolvedTempRoot -PathType Container)) {
            Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    return
}
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { throw 'Pass -PublishDirectory <published portable layout>, or use -SelfTest.' }
$inventoryPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Assets/Licenses/inventory.json'
$result = Assert-PortableNotices $PublishDirectory $inventoryPath
Write-Output ("Validated third-party notices: {0} runtime packages, {1} catalog documents, .NET {2}, {3} notice bytes." -f $result.RuntimePackages, $result.CatalogDocuments, $result.RuntimeVersion, $result.AggregateNoticeBytes)
