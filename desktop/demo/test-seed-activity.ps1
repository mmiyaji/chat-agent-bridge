$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cab-seed-activity-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $data = Join-Path $testRoot 'data'
    $mattermost = Join-Path $testRoot 'mattermost.json'
    $weather = Join-Path $testRoot 'weather'
    $todo = Join-Path $testRoot 'todo'
    foreach ($dir in @($data, $weather, $todo)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $syntheticState = '{"serverUrl":"http://127.0.0.1:9999","channelId":"synthetic-channel","alice":{"id":"synthetic-user"}}'
    [IO.File]::WriteAllText($mattermost, $syntheticState, [Text.UTF8Encoding]::new($false))

    & dotnet run --no-restore (Join-Path $PSScriptRoot 'SeedActivity.cs') -- $data $mattermost ja $weather $todo
    if ($LASTEXITCODE -ne 0) { throw "SeedActivity failed with exit code $LASTEXITCODE." }

    $historyPath = Join-Path $data 'history.json'
    $sessionsPath = Join-Path $data 'sessions.json'
    $auditFiles = @(Get-ChildItem -LiteralPath $data -File | Where-Object Name -match '^audit-(manifest\.json|segment-[0-9]{20}\.jsonl)$')
    if (-not (Test-Path -LiteralPath $historyPath)) { throw 'Execution history file was not created.' }
    if (-not (Test-Path -LiteralPath $sessionsPath)) { throw 'Session bindings file was not created.' }
    if ($auditFiles.Count -eq 0) { throw 'Audit manifest or segments were not created.' }

    $history = Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
    $sessions = Get-Content -LiteralPath $sessionsPath -Raw | ConvertFrom-Json
    if (@($history.records).Count -ne 70) { throw "Expected 70 generated executions; found $(@($history.records).Count)." }
    if (@($sessions.bindings).Count -ne 6) { throw "Expected 6 generated session bindings; found $(@($sessions.bindings).Count)." }
    'Synthetic activity seeding files verified: 70 executions, 6 sessions, audit files present.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
