[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TestArchive,
    [string]$UpdateArchive,
    [string]$WorkingDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ChatAgentBridge/UpdateTryouts'),
    [switch]$NoLaunch,
    [switch]$CleanupOnly,
    [ValidateRange(1, 50)][int]$KeepTryouts = 3
)

$ErrorActionPreference = 'Stop'
$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
for ($ancestor=$workingRoot; $ancestor; $ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Choose a working directory without symbolic links or junctions; the production updater rejects those paths.'
    }
}

function Get-GeneratedTryouts {
    if (-not (Test-Path -LiteralPath $workingRoot -PathType Container)) { return @() }
    Get-ChildItem -LiteralPath $workingRoot -Directory -Force | Where-Object {
        $_.Name -match '^portable-update-tryout-[0-9a-f]{32}$' -and
        -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'Start-Tryout.ps1') -PathType Leaf) -and
        ((Get-Content -LiteralPath (Join-Path $_.FullName 'README.txt') -TotalCount 1 -ErrorAction SilentlyContinue) -eq 'Chat Agent Bridge: local portable update tryout')
    }
}

function Get-TryoutActiveProcessPaths {
    try {
        @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ChatAgentBridge.Desktop.exe' OR Name = 'ChatAgentBridge.Updater.exe'" -ErrorAction Stop |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) } |
            ForEach-Object { [IO.Path]::GetFullPath($_.ExecutablePath) })
    } catch {
        throw 'Could not safely check whether a tryout is running. No old tryouts were removed.'
    }
}

function Remove-OldTryouts {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([string]$CurrentTryout)
    $candidates = @(Get-GeneratedTryouts)
    if ($candidates.Count -le $KeepTryouts) {
        return [pscustomobject]@{ Removed=0; Active=0; Remaining=$candidates.Count }
    }

    $processPaths = @(Get-TryoutActiveProcessPaths)
    $rootPrefix = [IO.Path]::TrimEndingDirectorySeparator($workingRoot) + [IO.Path]::DirectorySeparatorChar
    $active = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        $candidatePath = [IO.Path]::GetFullPath($candidate.FullName)
        if (-not $candidatePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetDirectoryName($candidatePath) -ne [IO.Path]::TrimEndingDirectorySeparator($workingRoot)) {
            continue
        }
        $prefix = [IO.Path]::TrimEndingDirectorySeparator($candidatePath) + [IO.Path]::DirectorySeparatorChar
        if ($processPaths | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }) {
            [void]$active.Add($candidatePath)
        }
    }

    $slots = [Math]::Max(0, $KeepTryouts - $active.Count)
    $inactive = @($candidates | Where-Object { -not $active.Contains([IO.Path]::GetFullPath($_.FullName)) } |
        Sort-Object @{Expression={ if (Test-Path -LiteralPath (Join-Path $_.FullName '.last-used')) { (Get-Item -LiteralPath (Join-Path $_.FullName '.last-used')).LastWriteTimeUtc } else { $_.LastWriteTimeUtc } }; Ascending=$false })
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in ($inactive | Select-Object -First $slots)) { [void]$keep.Add([IO.Path]::GetFullPath($candidate.FullName)) }

    $removed = 0
    foreach ($candidate in $inactive) {
        $candidatePath = [IO.Path]::GetFullPath($candidate.FullName)
        if ($candidatePath -eq $CurrentTryout -or $keep.Contains($candidatePath)) { continue }
        try {
            $reparsePoint = Get-ChildItem -LiteralPath $candidatePath -Force -Recurse -ErrorAction Stop |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
            if ($reparsePoint) {
                Write-Warning "Keeping tryout with a link or junction: $($candidate.Name)"
                continue
            }
            if ($PSCmdlet.ShouldProcess($candidatePath, 'Remove old portable update tryout')) {
                Remove-Item -LiteralPath $candidatePath -Recurse -Force -ErrorAction Stop
                $removed++
            }
        } catch {
            Write-Warning "Could not safely remove tryout '$($candidate.Name)': $($_.Exception.Message)"
        }
    }
    $remaining = @(Get-GeneratedTryouts).Count
    [pscustomobject]@{ Removed=$removed; Active=$active.Count; Remaining=$remaining }
}

if ($CleanupOnly) {
    $cleanup = Remove-OldTryouts -CurrentTryout ''
    Write-Output "Tryout cleanup: removed $($cleanup.Removed), active $($cleanup.Active), remaining $($cleanup.Remaining); retention limit $KeepTryouts."
    return
}

if ([string]::IsNullOrWhiteSpace($TestArchive) -or [string]::IsNullOrWhiteSpace($UpdateArchive)) {
    throw 'Specify both -TestArchive and -UpdateArchive, or use -CleanupOnly.'
}
$testZip = (Resolve-Path -LiteralPath $TestArchive).Path
$updateZip = (Resolve-Path -LiteralPath $UpdateArchive).Path
$archivePattern = '^ChatAgentBridge\.Portable-(\d+\.\d+\.\d+(?:-preview\.\d+)?)-win-(x64|arm64)\.zip$'
if ([IO.Path]::GetFileName($updateZip) -notmatch $archivePattern) { throw 'Use a named portable update ZIP.' }
$version = $Matches[1]
$architecture = $Matches[2]
if ([IO.Path]::GetFileName($testZip) -notmatch $archivePattern -or $Matches[2] -ne $architecture) {
    throw 'Use a portable test ZIP for the same architecture.'
}
$oldVersion = $Matches[1]
foreach ($candidate in @(@{Path=$testZip;Version=$oldVersion},@{Path=$updateZip;Version=$version})) {
    $archive = [IO.Compression.ZipFile]::OpenRead($candidate.Path)
    try {
        $entry = $archive.GetEntry('ChatAgentBridge.Portable/portable-release.json')
        if (-not $entry -or $entry.Length -gt 65536) { throw 'Use an updater-compatible ZIP with portable-release.json.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.schemaVersion -ne 1 -or $metadata.releaseVersion -ne $candidate.Version -or $metadata.architecture -ne $architecture) {
            throw 'Portable ZIP filename and release metadata must match.'
        }
    } finally { $archive.Dispose() }
}
$root = Join-Path $workingRoot ('portable-update-tryout-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $root 'fixture'
$assets = Join-Path $fixture 'assets'
New-Item -ItemType Directory -Path $root,$assets | Out-Null
Expand-Archive -LiteralPath $testZip -DestinationPath $root
$install = Join-Path $root 'ChatAgentBridge.Portable'
if (-not (Test-Path -LiteralPath (Join-Path $install 'ChatAgentBridge.Updater.exe'))) {
    throw 'The test ZIP must include the updater and be built with CABPortableUpdateTest=true.'
}
$assembly = Join-Path $install 'ChatAgentBridge.Desktop.dll'
if (-not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($assembly)).Contains('PortableUpdateTestFeed')) {
    throw 'The supplied test archive is a normal build. Rebuild with CABPortableUpdateTest=true.'
}
$assetName = [IO.Path]::GetFileName($updateZip)
Copy-Item -LiteralPath $updateZip -Destination (Join-Path $assets $assetName)
$tag = 'v' + $version
$release = @(@{
    tag_name = $tag; draft = $false; prerelease = $version.Contains('-')
    html_url = "https://github.com/mmiyaji/chat-agent-bridge/releases/tag/$tag"
    assets = @(@{
        name = $assetName; size = (Get-Item -LiteralPath $updateZip).Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $updateZip -Algorithm SHA256).Hash.ToLowerInvariant()
        browser_download_url = "https://github.com/mmiyaji/chat-agent-bridge/releases/download/$tag/$assetName"
    })
})
ConvertTo-Json -InputObject $release -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'releases.json') -Encoding utf8
$data = Join-Path $install 'Data'
New-Item -ItemType Directory -Path $data | Out-Null
@{Language='ja'} | ConvertTo-Json | Set-Content (Join-Path $data 'language.json') -Encoding utf8
@{Theme='dark'} | ConvertTo-Json | Set-Content (Join-Path $data 'appearance.json') -Encoding utf8
@{schemaVersion=3;step=0;status='deferred'} | ConvertTo-Json | Set-Content (Join-Path $data 'onboarding.json') -Encoding utf8
@{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'pricing-auto.json') -Encoding utf8
'This file must survive the portable update.' | Set-Content (Join-Path $data 'update-retention-marker.txt') -Encoding utf8
$notes = @"
Chat Agent Bridge: local portable update tryout

Test version: $oldVersion
Update version: $version (local ZIP, unpublished)
App: $install
Data: $data
Fixture: $fixture

In Information, choose Check for updates, Download, then Restart and update.
The updater confirmation can also be cancelled.
Your normal app settings and credentials are not used.
To start this older test app again before applying the update, run Start-Tryout.ps1.
After updating, create a fresh tryout using desktop/New-PortableUpdateTryout.ps1.
New tryouts keep the newest three folders by default; older inactive tryouts are removed.
Do not distribute the test build. No GitHub release is created by this script.
"@
$notes | Set-Content (Join-Path $root 'README.txt') -Encoding utf8
$launcher = @'
$ErrorActionPreference='Stop'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot '.last-used'), [DateTime]::UtcNow.ToString('O'))
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot 'ChatAgentBridge.Portable/ChatAgentBridge.Desktop.exe'))
$start.UseShellExecute=$false
$start.WorkingDirectory=Join-Path $PSScriptRoot 'ChatAgentBridge.Portable'
$start.ArgumentList.Add('--data-dir')
$start.ArgumentList.Add((Join-Path $start.WorkingDirectory 'Data'))
$start.Environment['CAB_PORTABLE_UPDATE_TEST_DIRECTORY']=Join-Path $PSScriptRoot 'fixture'
[void][Diagnostics.Process]::Start($start)
'@
$launcher | Set-Content (Join-Path $root 'Start-Tryout.ps1') -Encoding utf8
$cleanup = Remove-OldTryouts -CurrentTryout ([IO.Path]::GetFullPath($root))
Write-Output "Tryout cleanup: removed $($cleanup.Removed), active $($cleanup.Active), remaining $($cleanup.Remaining); retention limit $KeepTryouts."
$appId = $null
if (-not $NoLaunch) {
    [IO.File]::WriteAllText((Join-Path $root '.last-used'), [DateTime]::UtcNow.ToString('O'))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $install
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($data)
    $start.Environment['CAB_PORTABLE_UPDATE_TEST_DIRECTORY'] = $fixture
    $appId = ([Diagnostics.Process]::Start($start)).Id
}
[pscustomobject]@{ Root=$root; Install=$install; Data=$data; Fixture=$fixture; ProcessId=$appId; TestVersion=$oldVersion; UpdateVersion=$version }
