param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$app = Get-Process -Id $AppPid -ErrorAction Stop
if ($app.ProcessName -ne 'ChatAgentBridge.Desktop') { throw "PID $AppPid is not ChatAgentBridge.Desktop." }
$DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$marker = Join-Path $DataDirectory '.cab-setup-stepper-smoke'
if (-not (Test-Path -LiteralPath $marker)) {
    foreach ($file in @('settings.json', 'history.json', 'sessions.json', 'audit.json')) {
        if (Test-Path -LiteralPath (Join-Path $DataDirectory $file)) { throw 'Use a fresh, isolated --data-dir for the first stepper smoke test.' }
    }
    Set-Content -LiteralPath $marker -Value 'Isolated setup stepper UI test directory' -Encoding utf8
}
elseif ((Get-Content -LiteralPath $marker -Raw).Trim() -ne 'Isolated setup stepper UI test directory') {
    throw 'Refusing to use a data directory with an unrecognized smoke-test marker.'
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
$runDirectory = Join-Path $artifactDirectory ("setup-stepper-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$serverProcess = $null
$settingsPath = Join-Path $DataDirectory 'settings.json'
$settingsBackup = $null

function Invoke-UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $($Arguments[0])" }
}
function Wait-UI([string]$AutomationId, [string]$Value, [int]$Timeout = 5000, [switch]$Contains) {
    $arguments = @('wait-for', $AutomationId)
    if (-not [string]::IsNullOrEmpty($Value)) {
        $arguments += @('--value', $Value)
        if ($Contains) { $arguments += '--contains' }
    }
    $arguments += @('-t', "$Timeout")
    Invoke-UI $arguments
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $results.Add(@{ name = $Name; passed = $true }) }
    catch { $results.Add(@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}
function Open-Overview { Invoke-UI @('invoke', 'NavOverview'); Wait-UI 'SetupCompletionSummary' ' / 3 完了' 5000 -Contains }
function Assert-StepCount([string]$Count) { Wait-UI 'SetupCompletionSummary' "$Count / 3 完了" 5000 }
function Save-Connections { Invoke-UI @('invoke', 'SaveConnections'); Wait-UI 'Title' '設定を保存しました' 5000 -Contains }

try {
    # Verify before any input can alter persisted settings.
    Invoke-UI @('invoke', 'NavOverview')
    Wait-UI 'SettingsLocation' $DataDirectory 5000
    Wait-UI 'SetupProgress' '' 5000
    # On later runs, move only the prior smoke test's settings aside and reload
    # defaults so the same progress assertions can be replayed deterministically.
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $priorSettings = Join-Path $runDirectory 'prior-settings.json'
        Move-Item -LiteralPath $settingsPath -Destination $priorSettings
        Invoke-UI @('invoke', 'SetupReloadSettings')
        Assert-StepCount '0'
    }

    Check 'Initial progress and navigation links' {
        Assert-StepCount '0'
        Invoke-UI @('invoke', 'SetupAgentLink')
        Wait-UI 'AgentProvider' ''
        Open-Overview
        Invoke-UI @('invoke', 'SetupConnectionsLink')
        Wait-UI 'MattermostServerUrl' ''
        Open-Overview
        Invoke-UI @('invoke', 'SetupChatLink')
        Wait-UI 'ConnectMattermost' ''
        Open-Overview
    }

    Check 'Unsaved agent edits do not count; saving a valid workspace completes step one' {
        Invoke-UI @('invoke', 'SetupAgentLink')
        Invoke-UI @('set-value', 'WorkspaceRoot', $DataDirectory)
        Open-Overview
        Assert-StepCount '0'
        Wait-UI 'SetupDraftNotice' '未保存の変更' 5000 -Contains
        Invoke-UI @('invoke', 'SetupAgentLink')
        Invoke-UI @('invoke', 'SaveSettings')
        Wait-UI 'Title' '設定を保存しました' 5000 -Contains
        Open-Overview
        Assert-StepCount '1'
        Wait-UI 'SetupStepOneStatus' '完了' 5000 -Contains
    }

    Check 'Partial Mattermost configuration remains incomplete' {
        Invoke-UI @('invoke', 'SetupConnectionsLink')
        Invoke-UI @('set-value', 'MattermostServerUrl', 'http://127.0.0.1:1')
        Save-Connections
        Open-Overview
        Assert-StepCount '1'
        Wait-UI 'SetupStepTwoStatus' '次のステップ' 5000 -Contains
    }

    # Start a real loopback HTTP/WebSocket peer with a fake local token only.
    $serverInfoPath = Join-Path $runDirectory 'server-info.json'
    $serverLog = Join-Path $runDirectory 'server.log'
    $serverError = Join-Path $runDirectory 'server-error.log'
    $serverScript = Join-Path $PSScriptRoot 'test-support/setup-stepper-server.mjs'
    $serverArguments = '"{0}" --info-file "{1}" --auth-delay-ms 8000' -f $serverScript, $serverInfoPath
    $serverProcess = Start-Process -FilePath 'node' -ArgumentList $serverArguments -WindowStyle Hidden -RedirectStandardOutput $serverLog -RedirectStandardError $serverError -PassThru
    $serverDeadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (Test-Path -LiteralPath $serverInfoPath) -and [DateTime]::UtcNow -lt $serverDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $serverInfoPath)) { throw 'Loopback mock server did not report its listening port.' }
    $serverInfo = Get-Content -LiteralPath $serverInfoPath -Raw | ConvertFrom-Json

    Check 'Complete mock connection information reaches two of three steps' {
        Invoke-UI @('invoke', 'SetupConnectionsLink')
        Invoke-UI @('set-value', 'MattermostServerUrl', $serverInfo.url)
        Invoke-UI @('set-value', 'MattermostChannelId', 'stepper-test-channel')
        Invoke-UI @('set-value', 'MattermostToken', 'stepper-fake-token')
        Save-Connections
        Open-Overview
        Assert-StepCount '2'
        Wait-UI 'SetupStepTwoStatus' '完了' 5000 -Contains
    }

    Check 'Save failure preserves the last successfully saved step count' {
        if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw 'Expected saved settings.json file is missing.' }
        $settingsBackup = Join-Path $DataDirectory ('.settings-stepper-backup-' + [guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $settingsPath -Destination $settingsBackup
        [System.IO.Directory]::CreateDirectory($settingsPath) | Out-Null
        try {
            Invoke-UI @('invoke', 'SetupAgentLink')
            Invoke-UI @('set-value', 'WorkspaceRoot', '')
            Invoke-UI @('invoke', 'SaveSettings')
            Wait-UI 'Title' '設定を保存できませんでした' 5000 -Contains
            Open-Overview
            Assert-StepCount '2'
            Wait-UI 'SetupDraftNotice' '未保存の変更' 5000 -Contains
        }
        finally {
            if (Test-Path -LiteralPath $settingsPath -PathType Container) { [System.IO.Directory]::Delete($settingsPath, $false) }
            if (Test-Path -LiteralPath $settingsBackup -PathType Leaf) { Move-Item -LiteralPath $settingsBackup -Destination $settingsPath -Force }
            $settingsBackup = $null
        }
        Invoke-UI @('invoke', 'SetupAgentLink')
        Invoke-UI @('set-value', 'WorkspaceRoot', $DataDirectory)
        Invoke-UI @('invoke', 'SaveSettings')
        Wait-UI 'Title' '設定を保存しました' 5000 -Contains
        Open-Overview
        Assert-StepCount '2'
    }

    Check 'Failed network connection does not count as connected' {
        Invoke-UI @('invoke', 'SetupConnectionsLink')
        Invoke-UI @('set-value', 'MattermostServerUrl', 'http://127.0.0.1:1')
        Save-Connections
        Open-Overview
        Invoke-UI @('invoke', 'SetupChatLink')
        Invoke-UI @('invoke', 'ConnectMattermost')
        & winapp ui wait-for ConnectMattermost -p IsEnabled --value True -t 8000 -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw 'The failed connection did not return to the disconnected state.' }
        Open-Overview
        Assert-StepCount '2'
        Invoke-UI @('invoke', 'SetupConnectionsLink')
        Invoke-UI @('set-value', 'MattermostServerUrl', $serverInfo.url)
        Save-Connections
        Open-Overview
    }

    Check 'Confirmed local handshake completes step three; disconnect removes it' {
        Invoke-UI @('invoke', 'SetupChatLink')
        Invoke-UI @('invoke', 'ConnectMattermost')
        Open-Overview
        Wait-UI 'SetupStepThreeStatus' '接続中' 5000 -Contains
        Assert-StepCount '2'
        Wait-UI 'SetupCompletionSummary' '3 / 3 完了' 15000
        Wait-UI 'SetupStepThreeStatus' '完了' 5000 -Contains
        Invoke-UI @('invoke', 'SetupChatLink')
        Invoke-UI @('invoke', 'DisconnectChat')
        Open-Overview
        Assert-StepCount '2'
    }

    Check 'Reload reflects persisted configuration' {
        Open-Overview
        Invoke-UI @('invoke', 'SetupReloadSettings')
        Assert-StepCount '2'
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($saved.WorkspaceRoot -ne $DataDirectory -or $saved.MattermostServerUrl -ne $serverInfo.url -or
            $saved.MattermostChannelId -ne 'stepper-test-channel' -or -not $saved.MattermostToken.StartsWith('dpapi:')) {
            throw 'Reload fixture settings were not persisted as expected.'
        }
    }
}
finally {
    if ($settingsBackup -and (Test-Path -LiteralPath $settingsBackup -PathType Leaf)) {
        if (Test-Path -LiteralPath $settingsPath -PathType Container) { [System.IO.Directory]::Delete($settingsPath, $false) }
        if (-not (Test-Path -LiteralPath $settingsPath)) { Move-Item -LiteralPath $settingsBackup -Destination $settingsPath -Force }
    }
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $serverProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
    if ($results.Count -gt 0) {
        $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'results.json') -Encoding utf8
        $results | ForEach-Object { Write-Output "$(if ($_.passed) {'PASS'} else {'FAIL'}): $($_.name) $($_.error)" }
    }
}
if ($results.Where({ -not $_.passed }).Count -gt 0) { exit 1 }
