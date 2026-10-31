$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-cleanup-guard.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cab-demo-cleanup-guard-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $legacyRoot = Join-Path $testRoot 'default\samples'
    $legacyTarget = Join-Path $legacyRoot 'weather-cli'
    New-Item -ItemType Directory -Path $legacyTarget -Force | Out-Null
    $legacyFile = Join-Path $legacyTarget 'legacy.txt'
    [IO.File]::WriteAllText($legacyFile, 'legacy')
    Reset-CabDemoDirectory $legacyTarget $legacyRoot | Out-Null
    if (Test-Path -LiteralPath $legacyFile) { throw 'Default legacy directory contents were not reset.' }

    $externalRoot = Join-Path $testRoot 'external'
    $externalTarget = Join-Path $externalRoot 'todo-cli'
    New-Item -ItemType Directory -Path $externalRoot | Out-Null
    Reset-CabDemoDirectory $externalTarget $legacyRoot | Out-Null
    $marker = Join-Path $externalTarget '.cab-demo-owned'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'New external directory lacks its ownership marker.' }
    $ownedFile = Join-Path $externalTarget 'owned.txt'
    [IO.File]::WriteAllText($ownedFile, 'owned')
    Reset-CabDemoDirectory $externalTarget $legacyRoot | Out-Null
    if (Test-Path -LiteralPath $ownedFile) { throw 'Authorized external directory contents were not reset.' }

    $unmarked = Join-Path $externalRoot 'unmarked-cli'
    New-Item -ItemType Directory -Path $unmarked | Out-Null
    $preserved = Join-Path $unmarked 'keep.txt'
    [IO.File]::WriteAllText($preserved, 'keep')
    try { Reset-CabDemoDirectory $unmarked $legacyRoot | Out-Null; throw 'Unmarked external directory was accepted.' }
    catch { if ($_.Exception.Message -eq 'Unmarked external directory was accepted.') { throw } }
    if (-not (Test-Path -LiteralPath $preserved)) { throw 'Rejected external directory was modified.' }

    $driveRoot = [IO.Path]::GetPathRoot($testRoot)
    try { Reset-CabDemoDirectory $driveRoot $legacyRoot | Out-Null; throw 'Filesystem root was accepted.' }
    catch { if ($_.Exception.Message -eq 'Filesystem root was accepted.') { throw } }

    $junctionTarget = Join-Path $testRoot 'junction-target'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    $junctionFile = Join-Path $junctionTarget 'keep.txt'
    [IO.File]::WriteAllText($junctionFile, 'keep')
    $junction = Join-Path $testRoot 'junction'
    New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    try { Reset-CabDemoDirectory $junction $legacyRoot | Out-Null; throw 'Reparse-point target was accepted.' }
    catch { if ($_.Exception.Message -eq 'Reparse-point target was accepted.') { throw } }
    if (-not (Test-Path -LiteralPath $junctionFile)) { throw 'Rejected reparse-point target contents were modified.' }

    'Demo cleanup guard cases passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
