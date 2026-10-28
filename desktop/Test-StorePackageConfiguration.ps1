$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifestPath = Join-Path $root 'ChatAgentBridge.Desktop\Package.appxmanifest'
$projectPath = Join-Path $root 'ChatAgentBridge.Desktop\ChatAgentBridge.Desktop.csproj'
$packageScript = Join-Path $root 'Create-StorePackage.ps1'
$version = '1.2.3.0'
$publisher = 'CN=11223344-5566-7788-99AA-BBCCDDEEFF00'
$displayName = 'Configuration Test Publisher'

[xml]$developmentManifest = Get-Content -LiteralPath $manifestPath -Raw
if ($developmentManifest.Package.Identity.Name -cne 'ChatAgentBridge.WinUI.Development' -or
    $developmentManifest.Package.Identity.Publisher -cne 'CN=ChatAgentBridgeDevelopment') {
    throw 'Default development package identity changed unexpectedly.'
}

# The validation-only path must generate and validate a Store manifest without building a package.
$validation = & $packageScript -PackageName org.ruhenheim.ChatAgentBridge -Publisher $publisher `
    -PublisherDisplayName $displayName -PackageVersion $version -ValidateOnly 2>&1 | Out-String
if ($validation -notmatch 'Store package configuration validated: org\.ruhenheim\.ChatAgentBridge 1\.2\.3\.0') {
    throw "Store manifest validation did not complete: $validation"
}

function Assert-StoreScriptRejects([hashtable]$Parameters, [string]$Name) {
    try {
        & $packageScript @Parameters -ValidateOnly *> $null
        throw "Store package script accepted invalid $Name."
    }
    catch {
        if ($_.Exception.Message -like "Store package script accepted invalid*") { throw }
    }
}
Assert-StoreScriptRejects @{ PackageName = 'Other.Package'; Publisher = $publisher; PublisherDisplayName = $displayName; PackageVersion = $version } 'package name'
Assert-StoreScriptRejects @{ PackageName = 'org.ruhenheim.ChatAgentBridge'; Publisher = 'CN=ChatAgentBridgeDevelopment'; PublisherDisplayName = $displayName; PackageVersion = $version } 'development publisher'
Assert-StoreScriptRejects @{ PackageName = 'org.ruhenheim.ChatAgentBridge'; Publisher = $publisher; PublisherDisplayName = $displayName; PackageVersion = '1.2.3' } 'package version'
Assert-StoreScriptRejects @{ PackageName = 'org.ruhenheim.ChatAgentBridge'; Publisher = $publisher; PublisherDisplayName = $displayName; PackageVersion = '1.2.3.1' } 'nonzero Store revision'

# Exercise the MSBuild guard directly with a generated manifest; invalid portable combinations and
# mismatched identity properties must fail before any build/package work starts.
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ("cab-store-config-" + [guid]::NewGuid().ToString('N'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedTempDirectory = [IO.Path]::GetFullPath($tempDirectory)
if (-not $resolvedTempDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary Store configuration test directory is outside the system temp directory.'
}
$tempManifest = Join-Path $tempDirectory 'Store.appxmanifest'
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
try {
    $storeManifest = [xml]$developmentManifest.OuterXml
    $storeManifest.Package.Identity.SetAttribute('Name', 'org.ruhenheim.ChatAgentBridge')
    $storeManifest.Package.Identity.SetAttribute('Publisher', $publisher)
    $storeManifest.Package.Identity.SetAttribute('Version', $version)
    $storeManifest.Package.Properties.PublisherDisplayName = $displayName
    $storeManifest.Save($tempManifest)

    $baseArgs = @(
        'msbuild', $projectPath, '-target:ValidateCabStoreBuild',
        '-p:CABStore=true', "-p:CABStoreManifest=$tempManifest",
        '-p:CABStorePackageName=org.ruhenheim.ChatAgentBridge', "-p:CABStorePublisher=$publisher",
        "-p:CABStorePublisherDisplayName=$displayName", "-p:CABStorePackageVersion=$version"
    )
    & dotnet @baseArgs *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Valid Store identity failed the MSBuild guard.' }

    foreach ($portableSwitch in @('CABPortable', 'CABPortableIdentity', 'CABPortableUpdateTest', 'CABDiscordFixture')) {
        & dotnet @baseArgs "-p:$portableSwitch=true" *> $null
        if ($LASTEXITCODE -eq 0) { throw "MSBuild guard accepted incompatible switch $portableSwitch." }
    }

    & dotnet @baseArgs '-p:CABStorePublisher=CN=DifferentPublisher' *> $null
    if ($LASTEXITCODE -eq 0) { throw 'MSBuild guard accepted a manifest/property Publisher mismatch.' }
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'Store packaging executable configuration checks passed.'
