[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Architecture = 'x64',
    [string] $ReleaseVersion = '0.1.0-preview.2',
    [string] $ApplicationVersion = '0.1.0.1',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/portable-releases'),
    [switch] $ValidateOnly,
    [string] $ArchivePath,
    [string] $ExistingLayout
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj'
$distributionDir = Join-Path $PSScriptRoot 'distribution'
$expectedMachine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
$requiredNames = @('ChatAgentBridge.Desktop.exe', 'ChatAgentBridge.Desktop.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.Insights.Resource.dll', 'ChatAgentBridge.Desktop.pri', 'README.portable.ja.txt', 'README.portable.en.txt')
$forbiddenDirectoryNames = @('data', 'private-data', 'privatedata', 'settings', 'history', 'sessions', 'audit', 'credentials', '.credentials', 'token', 'tokens', 'secrets', 'chatagentbridge.testagent', 'appx', 'msix')
$knownPackageMetadataNames = @('appxmanifest.xml', 'appxblockmap.xml', 'appxsignature.p7x', '[content_types].xml')

function Assert-PortableContents {
    param([Parameter(Mandatory)][string] $Layout)

    $resolved = (Resolve-Path -LiteralPath $Layout).Path
    $files = @(Get-ChildItem -LiteralPath $resolved -File -Recurse -Force)
    $directories = @(Get-ChildItem -LiteralPath $resolved -Directory -Recurse -Force)

    foreach ($directory in $directories) {
        if ($forbiddenDirectoryNames -contains $directory.Name.ToLowerInvariant()) {
            throw "Forbidden private or test directory in portable payload: $($directory.FullName)"
        }
    }

    foreach ($file in $files) {
        $extension = $file.Extension.ToLowerInvariant()
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name).ToLowerInvariant()
        if ($forbiddenDirectoryNames -contains $stem) { throw "Private or test file is forbidden in portable payload: $($file.FullName)" }
        if ($extension -in @('.pfx', '.cer', '.msix', '.appx')) {
            throw "Forbidden certificate or installer/package file in portable payload: $($file.FullName)"
        }
        if ($file.Name -match '(?i)testagent|^chatagentbridge\.testagent') {
            throw "TestAgent content is forbidden in portable payload: $($file.FullName)"
        }
        if ($extension -eq '.pdb') {
            throw "Debug symbols must be removed before validation: $($file.FullName)"
        }
    }

    $exe = Join-Path $resolved 'ChatAgentBridge.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'ChatAgentBridge.Desktop.exe is missing.' }

    $stream = [System.IO.File]::OpenRead($exe)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'The desktop executable has no valid DOS header.' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) { throw 'The desktop executable has an invalid PE header offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'The desktop executable has no valid PE signature.' }
        $machine = $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
    if ($machine -ne $expectedMachine) {
        $actual = if ($machine -eq 0x8664) { 'x64' } elseif ($machine -eq 0xAA64) { 'ARM64' } else { ('0x{0:X4}' -f $machine) }
        throw "Executable architecture is $actual; expected $Architecture."
    }

    $runtimeConfigPath = Join-Path $resolved 'ChatAgentBridge.Desktop.runtimeconfig.json'
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $runtimeOptionsProperty = $runtimeConfig.PSObject.Properties['runtimeOptions']
    $runtimeOptions = if ($runtimeOptionsProperty) { $runtimeOptionsProperty.Value } else { $null }
    if ($null -eq $runtimeOptions) { throw 'runtimeconfig.json has no runtimeOptions object.' }
    $references = @()
    $frameworkProperty = $runtimeOptions.PSObject.Properties['framework']
    $frameworksProperty = $runtimeOptions.PSObject.Properties['frameworks']
    if ($frameworkProperty -and $frameworkProperty.Value) { $references += @($frameworkProperty.Value) }
    if ($frameworksProperty -and $frameworksProperty.Value) { $references += @($frameworksProperty.Value) }
    if ($references.Count -gt 0) {
        $includedProperty = $runtimeOptions.PSObject.Properties['includedFrameworks']
        $included = @()
        if ($includedProperty) { $included = @($includedProperty.Value | ForEach-Object { "$( $_.name )/$( $_.version )" }) }
        foreach ($reference in $references) {
            $key = "$( $reference.name )/$( $reference.version )"
            if ($included -notcontains $key) {
                throw "runtimeconfig.json refers to external framework $key; portable builds must include their frameworks."
            }
        }
    }

    foreach ($name in $requiredNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $resolved $name) -PathType Leaf)) {
            throw "Required portable runtime file is missing: $name"
        }
    }

    $sumFile = Join-Path $resolved 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $sumFile -PathType Leaf)) { throw 'SHA256SUMS.txt is missing.' }
    $listed = @{}
    foreach ($line in Get-Content -LiteralPath $sumFile) {
        if ($line -notmatch '^(?<hash>[A-Fa-f0-9]{64}) \*(?<path>.+)$') { throw "Invalid SHA256SUMS.txt row: $line" }
        $relativeSlash = $Matches.path.Replace('\', '/')
        if ($relativeSlash.StartsWith('/') -or $relativeSlash -match '(^|/)\.\.(?:/|$)') { throw "Unsafe checksum path: $relativeSlash" }
        $relative = $relativeSlash.Replace('/', '\')
        if ($listed.ContainsKey($relative)) { throw "Duplicate checksum path: $relative" }
        $listed[$relative] = $Matches.hash.ToLowerInvariant()
    }
    $payloadFiles = @($files | Where-Object { $_.Name -ne 'SHA256SUMS.txt' })
    if ($listed.Count -ne $payloadFiles.Count) { throw 'SHA256SUMS.txt does not cover every payload file exactly once.' }
    foreach ($file in $payloadFiles) {
        $relative = (Get-RelativePath -BasePath $resolved -TargetPath $file.FullName).Replace('\', '/')
        $key = $relative.Replace('/', '\')
        if (-not $listed.ContainsKey($key)) { throw "SHA256SUMS.txt does not list $relative" }
        $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $listed[$key]) { throw "Checksum mismatch: $relative" }
    }

    [pscustomobject]@{ Layout = $resolved; FileCount = $files.Count; Architecture = $Architecture }
}

function Assert-NoPrivateOrForbiddenContent {
    param([Parameter(Mandatory)][string] $Layout)
    $resolved = (Resolve-Path -LiteralPath $Layout).Path
    if ($forbiddenDirectoryNames -contains (Split-Path -Leaf $resolved).ToLowerInvariant()) {
        throw "Forbidden private or test source directory: $resolved"
    }
    foreach ($directory in Get-ChildItem -LiteralPath $resolved -Directory -Recurse -Force) {
        if ($forbiddenDirectoryNames -contains $directory.Name.ToLowerInvariant()) {
            throw "Forbidden private or test directory in portable source: $($directory.FullName)"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $resolved -File -Recurse -Force) {
        $extension = $file.Extension.ToLowerInvariant()
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name).ToLowerInvariant()
        if ($forbiddenDirectoryNames -contains $stem -or $file.Name -match '(?i)testagent|^chatagentbridge\.testagent' -or $extension -in @('.pfx', '.cer', '.msix', '.appx')) {
            throw "Forbidden private or test file in portable source: $($file.FullName)"
        }
    }
}

function Remove-KnownPackagingMetadata {
    param([Parameter(Mandatory)][string] $Layout)

    foreach ($name in $knownPackageMetadataNames) {
        Get-ChildItem -LiteralPath $Layout -File -Recurse -Force | Where-Object { $_.Name.ToLowerInvariant() -eq $name } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    Get-ChildItem -LiteralPath $Layout -Directory -Recurse -Force | Where-Object { $_.Name -ieq 'AppxMetadata' } |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName -Force }
        }
    Get-ChildItem -LiteralPath $Layout -File -Recurse -Force | Where-Object { $_.Extension -ieq '.pdb' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

function New-PayloadChecksums {
    param([Parameter(Mandatory)][string] $Layout)

    $sumPath = Join-Path $Layout 'SHA256SUMS.txt'
    $rows = @(Get-ChildItem -LiteralPath $Layout -File -Recurse -Force | Where-Object { $_.FullName -ne $sumPath } |
        Sort-Object { Get-RelativePath -BasePath $Layout -TargetPath $_.FullName } | ForEach-Object {
            $relative = (Get-RelativePath -BasePath $Layout -TargetPath $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$relative"
        })
    [System.IO.File]::WriteAllLines($sumPath, $rows, [System.Text.UTF8Encoding]::new($false))
}

function Get-SafeArtifactPath {
    param([Parameter(Mandatory)][string] $Parent, [Parameter(Mandatory)][string] $ChildName)
    $parentFull = [System.IO.Path]::GetFullPath($Parent)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $parentFull $ChildName))
    if (-not $candidate.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing artifact path outside its parent: $candidate"
    }
    return $candidate
}

function Get-RelativePath {
    param([Parameter(Mandatory)][string] $BasePath, [Parameter(Mandatory)][string] $TargetPath)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $baseFull = [System.IO.Path]::GetFullPath($BasePath).TrimEnd($separator) + $separator
    $targetFull = [System.IO.Path]::GetFullPath($TargetPath)
    if (-not $targetFull.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected folder: $targetFull"
    }
    return $targetFull.Substring($baseFull.Length)
}

if ($ValidateOnly) {
    if (-not $ArchivePath -and -not $ExistingLayout) { throw 'Use -ArchivePath or -ExistingLayout with -ValidateOnly.' }
    if ($ArchivePath -and $ExistingLayout) { throw 'Choose either -ArchivePath or -ExistingLayout with -ValidateOnly.' }
    if ($ExistingLayout) {
        $result = Assert-PortableContents -Layout $ExistingLayout
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $fullArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
        $artifactRoot = Join-Path $PSScriptRoot 'artifacts'
        New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
        $validationRoot = Get-SafeArtifactPath -Parent $artifactRoot -ChildName ('.portable-validate-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $validationRoot | Out-Null
        $zip = [System.IO.Compression.ZipFile]::OpenRead($fullArchive)
        try {
            if (-not ($zip.Entries | Where-Object { $_.FullName -match '^ChatAgentBridge\.Portable/' })) { throw 'Archive is missing the ChatAgentBridge.Portable/ root folder.' }
            foreach ($entry in $zip.Entries) {
                $normalized = $entry.FullName.Replace('\', '/')
                if (-not $normalized.StartsWith('ChatAgentBridge.Portable/', [System.StringComparison]::Ordinal)) { throw "Unexpected ZIP root entry: $normalized" }
                if ($normalized.StartsWith('/') -or $normalized -match '(^|/)\.\.(?:/|$)') { throw "Unsafe ZIP entry: $($entry.FullName)" }
                $parts = $normalized.Split('/')
                if ($parts | Where-Object { $forbiddenDirectoryNames -contains $_.ToLowerInvariant() }) { throw "Forbidden private or test path in ZIP: $normalized" }
                if ($forbiddenDirectoryNames -contains [System.IO.Path]::GetFileNameWithoutExtension($entry.Name).ToLowerInvariant()) { throw "Forbidden private or test file in ZIP: $normalized" }
                if ($entry.Name -match '(?i)testagent|^chatagentbridge\.testagent' -or $entry.Name -match '(?i)\.(pfx|cer|msix|appx|pdb)$') { throw "Forbidden file in ZIP: $normalized" }
            }
        }
        finally { $zip.Dispose() }
        Expand-Archive -LiteralPath $fullArchive -DestinationPath $validationRoot
        $layout = Join-Path $validationRoot 'ChatAgentBridge.Portable'
        if (-not (Test-Path -LiteralPath $layout -PathType Container)) { throw 'Archive root folder is malformed.' }
        $result = Assert-PortableContents -Layout $layout
        $result | Add-Member NoteProperty Archive $fullArchive
    }
    Write-Output $result
    return
}

if ($ArchivePath) { throw '-ArchivePath is only supported with -ValidateOnly.' }
if (-not $ExistingLayout -and -not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Desktop project not found: $project" }
foreach ($readme in @('README.portable.ja.txt', 'README.portable.en.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $distributionDir $readme) -PathType Leaf)) { throw "Distribution note missing: $readme" }
}

$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$stageRoot = Get-SafeArtifactPath -Parent $artifactRoot -ChildName ('.portable-build-' + [guid]::NewGuid().ToString('N'))
$layout = Join-Path $stageRoot 'ChatAgentBridge.Portable'
New-Item -ItemType Directory -Path $layout | Out-Null

$runtime = if ($Architecture -eq 'x64') { 'win-x64' } else { 'win-arm64' }
if ($ExistingLayout) {
    if (-not (Test-Path -LiteralPath $ExistingLayout -PathType Container)) { throw "Existing publish layout not found: $ExistingLayout" }
    Assert-NoPrivateOrForbiddenContent -Layout $ExistingLayout
    Get-ChildItem -LiteralPath $ExistingLayout -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $layout -Recurse -Force
    }
}
else {
    $publishArgs = @(
        'publish', $project, '-c', 'Release', ('-p:Platform=' + $Architecture),
        '-r', $runtime, '--self-contained', 'true', '-p:CABPortable=true',
        ('-p:Version=' + $ReleaseVersion), ('-p:AssemblyVersion=' + $ApplicationVersion),
        ('-p:FileVersion=' + $ApplicationVersion), '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $layout
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

Remove-KnownPackagingMetadata -Layout $layout
Copy-Item -LiteralPath (Join-Path $distributionDir 'README.portable.ja.txt') -Destination $layout
Copy-Item -LiteralPath (Join-Path $distributionDir 'README.portable.en.txt') -Destination $layout
New-PayloadChecksums -Layout $layout
$null = Assert-PortableContents -Layout $layout

$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
$archiveName = "ChatAgentBridge.Portable-$ReleaseVersion-$runtime.zip"
$archiveFullPath = Get-SafeArtifactPath -Parent $outputFull -ChildName $archiveName
if (Test-Path -LiteralPath $archiveFullPath) { throw "Refusing to overwrite an existing archive: $archiveFullPath" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $archiveFullPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$archiveHash = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$archiveFullPath.sha256"
[System.IO.File]::WriteAllText($checksumPath, "$archiveHash  $([System.IO.Path]::GetFileName($archiveFullPath))`n", [System.Text.UTF8Encoding]::new($false))

Write-Output ([pscustomobject]@{ Archive = $archiveFullPath; SHA256 = $archiveHash; StagingLayout = $layout; Architecture = $Architecture; ReleaseVersion = $ReleaseVersion })
