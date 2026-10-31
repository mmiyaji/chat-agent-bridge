# Safe directory reset helpers for the demo setup. This file has no environment or filesystem side effects
# when dot-sourced; callers pass every path explicitly.
$script:CabDemoOwnershipMarker = '.cab-demo-owned'
$script:CabDemoOwnershipValue = 'chat-agent-bridge demo sample directory v1'

function Resolve-CabDemoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A demo directory path is required.' }
    [IO.Path]::GetFullPath($Path)
}

function Test-CabDemoDirectChild([string]$Path, [string]$Parent) {
    $fullPath = Resolve-CabDemoPath $Path
    $fullParent = (Resolve-CabDemoPath $Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actualParent = [IO.Path]::GetDirectoryName($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return [string]::Equals($actualParent, $fullParent, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CabDemoNoReparsePath([string]$Path) {
    $fullPath = Resolve-CabDemoPath $Path
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -eq
        $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
        throw "Refusing to reset a filesystem root: $fullPath"
    }

    $current = $root
    $tail = $fullPath.Substring($root.Length)
    foreach ($part in ($tail -split '[\\/]+' | Where-Object { $_ })) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to reset through a reparse point: $current"
            }
        }
    }
}

function Reset-CabDemoDirectory([string]$Path, [string]$LegacyRoot) {
    $fullPath = Resolve-CabDemoPath $Path
    Assert-CabDemoNoReparsePath $fullPath
    $legacy = Test-CabDemoDirectChild $fullPath $LegacyRoot

    $exists = Test-Path -LiteralPath $fullPath
    if ($exists) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (-not $item.PSIsContainer) { throw "Refusing to reset a non-directory demo target: $fullPath" }
        if (-not $legacy) {
            $marker = Join-Path $fullPath $script:CabDemoOwnershipMarker
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                throw "Refusing to delete existing external demo directory without an ownership marker: $fullPath"
            }
            $markerItem = Get-Item -LiteralPath $marker -Force
            if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                (Get-Content -LiteralPath $marker -Raw) -cne ($script:CabDemoOwnershipValue + "`n" + $fullPath)) {
                throw "Refusing to delete external demo directory with an invalid ownership marker: $fullPath"
            }
            Remove-Item -LiteralPath $fullPath -Recurse -Force
        }
        else { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    if (-not $legacy) {
        $marker = Join-Path $fullPath $script:CabDemoOwnershipMarker
        [IO.File]::WriteAllText($marker, $script:CabDemoOwnershipValue + "`n" + $fullPath, [Text.UTF8Encoding]::new($false))
    }
    return $fullPath
}
