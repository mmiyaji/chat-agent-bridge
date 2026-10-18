param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$TestSessionDetach
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$marker = Join-Path $DataDirectory '.cab-ui-smoke'
if (-not (Test-Path -LiteralPath $marker)) {
    foreach ($file in @('settings.json', 'history.json', 'sessions.json', 'audit.json')) {
        if (Test-Path -LiteralPath (Join-Path $DataDirectory $file)) { throw 'Use an empty, isolated --data-dir for the first smoke test.' }
    }
    Set-Content -LiteralPath $marker -Value 'Isolated UI smoke test directory' -Encoding utf8
}
$results = [System.Collections.Generic.List[object]]::new()
function Invoke-UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $($Arguments[0])" }
}
function Select-ComboItem([string]$AutomationId, [string]$ItemAutomationId) {
    Invoke-UI @('invoke', $AutomationId)
    Invoke-UI @('invoke', $ItemAutomationId)
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $results.Add(@{ name = $Name; passed = $true }) }
    catch { $results.Add(@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}

# Refuse to edit settings unless the running app uses the requested test directory.
Invoke-UI @('invoke', 'NavOverview')
Invoke-UI @('wait-for', 'SettingsLocation', '--value', [System.IO.Path]::GetFullPath($DataDirectory), '-t', '5000')

Check 'Navigation and initialized settings' {
    Invoke-UI @('wait-for', 'NavOverview', '-t', '5000')
    Invoke-UI @('invoke', 'NavAgentSettings')
    Invoke-UI @('wait-for', 'AgentProvider', '-t', '3000')
    Invoke-UI @('wait-for', 'WorkspaceRoot', '-t', '3000')
    Invoke-UI @('invoke', 'NavWorkbench')
    Invoke-UI @('wait-for', 'Prompt', '-t', '3000')
    Invoke-UI @('wait-for', 'RunAgent', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
}

Check 'Claude profile and model persist without running a paid model' {
    Invoke-UI @('invoke', 'NavAgentSettings')
    Select-ComboItem 'AgentProvider' 'ProviderOptionClaude'
    Invoke-UI @('wait-for', 'AgentExecutable', '--value', 'claude', '-t', '3000')
    Invoke-UI @('set-value', 'AgentModel', 'claude-smoke-model')
    Invoke-UI @('invoke', 'SaveSettings')
    Invoke-UI @('wait-for', 'Title', '--value', '設定を保存しました', '--contains', '-t', '3000')
    $saved = Get-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Raw | ConvertFrom-Json
    if ($saved.AgentProvider -ne 'claude' -or $saved.AgentExecutable -ne 'claude' -or
        $saved.AgentArguments -ne '--print --output-format stream-json --verbose' -or $saved.AgentModel -ne 'claude-smoke-model') {
        throw 'Claude profile executable, arguments, or model were not persisted.'
    }
}

Check 'Select custom PowerShell fixture and save isolated settings' {
    Invoke-UI @('invoke', 'NavAgentSettings')
    Select-ComboItem 'AgentProvider' 'ProviderOptionCustom'
    Invoke-UI @('set-value', 'WorkspaceRoot', $DataDirectory)
    Invoke-UI @('set-value', 'AgentExecutable', 'powershell.exe')
    Invoke-UI @('set-value', 'AgentArguments', '-NoProfile -NonInteractive -Command "[Console]::Write([Console]::In.ReadToEnd())"')
    Invoke-UI @('invoke', 'SaveSettings')
    Invoke-UI @('wait-for', 'Title', '--value', '設定を保存しました', '--contains', '-t', '3000')
    $saved = Get-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Raw | ConvertFrom-Json
    if ($saved.AgentProvider -ne 'custom' -or $saved.AgentModel -or $saved.WorkspaceRoot -ne $DataDirectory -or
        $saved.AgentExecutable -ne 'powershell.exe' -or $saved.AgentArguments -ne '-NoProfile -NonInteractive -Command "[Console]::Write([Console]::In.ReadToEnd())"') {
        throw 'Custom PowerShell profile or workspace was not persisted.'
    }
    Invoke-UI @('invoke', 'NavWorkbench')
    Invoke-UI @('wait-for', 'RunAgent', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
}
Check 'Confirmation can cancel without running' {
    Invoke-UI @('set-value', 'Prompt', 'winui-smoke-no-model')
    Invoke-UI @('invoke', 'RunAgent')
    Invoke-UI @('wait-for', 'RunConfirmation', '-t', '3000')
    Invoke-UI @('invoke', 'CloseButton')
    Invoke-UI @('wait-for', 'RunConfirmation', '--gone', '-t', '3000')
    Invoke-UI @('wait-for', 'RunAgent', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
}
Check 'Approved local stdin output' {
    Invoke-UI @('invoke', 'RunAgent')
    Invoke-UI @('wait-for', 'RunConfirmation', '-t', '3000')
    Invoke-UI @('invoke', 'PrimaryButton')
    Invoke-UI @('wait-for', 'AgentOutput', '--value', 'winui-smoke-no-model', '--contains', '-t', '10000')
    Invoke-UI @('wait-for', 'RunAgent', '-p', 'IsEnabled', '--value', 'True', '-t', '5000')
}
Check 'Connections and chat navigation' {
    Invoke-UI @('invoke', 'NavConnections')
    Invoke-UI @('wait-for', 'MattermostServerUrl', '-t', '3000')
    Invoke-UI @('invoke', 'NavChat')
    Invoke-UI @('wait-for', 'ConnectMattermost', '-t', '3000')
    Invoke-UI @('wait-for', 'ApproveChat', '-p', 'IsEnabled', '--value', 'False', '-t', '3000')
}
Check 'Durable execution metadata and history navigation' {
    Invoke-UI @('invoke', 'NavHistory')
    Invoke-UI @('invoke', 'ExecutionHistoryTab')
    Invoke-UI @('wait-for', 'ExecutionHistoryList', '-t', '3000')
    Invoke-UI @('wait-for', 'HistoryStatus', '--value', '実行', '--contains', '-t', '3000')
    $path = Join-Path $DataDirectory 'history.json'
    $historyText = Get-Content -LiteralPath $path -Raw
    $history = $historyText | ConvertFrom-Json
    if (-not ($history.Records | Where-Object { $_.Source -eq 'local' -and $_.Status -eq 'completed' })) { throw 'Completed execution metadata was not persisted.' }
    if ($historyText.Contains('winui-smoke-no-model') -or $historyText -match '"(Prompt|Output|Error)"\s*:') { throw 'History leaked prompt or output.' }
    $audit = Get-Content -LiteralPath (Join-Path $DataDirectory 'audit.json') -Raw | ConvertFrom-Json
    if (-not ($audit.Records | Where-Object { $_.Kind -eq 'completed' })) { throw 'Completion audit event was not persisted.' }
}
if ($TestSessionDetach) {
    Check 'Session detach confirmation and persistence' {
        $sessionPath = Join-Path $DataDirectory 'sessions.json'
        if (Test-Path -LiteralPath $sessionPath) { throw 'Session detach fixture requires a fresh smoke directory.' }
        $sessionId = [guid]::NewGuid().ToString()
        $fixture = @{
            SchemaVersion = 1
            Bindings = @(@{
                Key = @{ Provider = 'slack'; ChannelId = 'smoke-channel'; ThreadId = 'smoke-thread' }
                WorkspaceRoot = $DataDirectory; AgentExecutable = 'codex'; AgentArguments = 'exec --json -'
                SessionId = $sessionId; UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o')
            })
        }
        $fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sessionPath -Encoding utf8
        Invoke-UI @('invoke', 'SavedConversationsTab')
        Invoke-UI @('invoke', 'RefreshHistory')
        Invoke-UI @('wait-for', 'SavedSessionsList', '-t', '3000')
        Invoke-UI @('click', 'smoke-thread')
        Invoke-UI @('scroll-into-view', 'EditSessionModel')
        Invoke-UI @('wait-for', 'EditSessionModel', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        Invoke-UI @('invoke', 'EditSessionModel')
        Invoke-UI @('wait-for', 'SessionModelDialog', '-t', '3000')
        Invoke-UI @('set-value', 'SessionModelOverride', 'smoke-session-model')
        Invoke-UI @('invoke', 'PrimaryButton')
        Invoke-UI @('wait-for', 'HistoryStatus', '--value', '次回の実行モデルを保存しました', '--contains', '-t', '5000')
        $saved = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
        if ($saved.Bindings[0].Model -ne 'smoke-session-model') { throw 'Session model override did not persist.' }
        Invoke-UI @('wait-for', 'DetachSession', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        Invoke-UI @('invoke', 'DetachSession')
        Invoke-UI @('wait-for', 'DetachSessionConfirmation', '-t', '3000')
        Invoke-UI @('invoke', 'CloseButton')
        Invoke-UI @('wait-for', 'DetachSessionConfirmation', '--gone', '-t', '3000')
        $saved = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
        if ($saved.Bindings.Count -ne 1) { throw 'Cancelled detach removed the binding.' }
        Invoke-UI @('invoke', 'DetachSession')
        Invoke-UI @('wait-for', 'DetachSessionConfirmation', '-t', '3000')
        Invoke-UI @('invoke', 'PrimaryButton')
        Invoke-UI @('wait-for', 'HistoryStatus', '--value', 'セッションを切り離しました', '--contains', '-t', '5000')
        $saved = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
        if ($saved.Bindings.Count -ne 0) { throw 'Confirmed detach did not remove the binding.' }
        if (-not (Test-Path -LiteralPath (Join-Path $DataDirectory 'history.json'))) { throw 'Detach removed execution history.' }
    }
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'smoke-results.json') -Encoding utf8
$results | ForEach-Object { Write-Output "$(if ($_.passed) {'PASS'} else {'FAIL'}): $($_.name) $($_.error)" }
if ($results.Where({ -not $_.passed }).Count -gt 0) { exit 1 }
