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
# The settings location sits inside the collapsed technical-details expander.
Invoke-UI @('invoke', 'SavedSettingsDetails')
Invoke-UI @('wait-for', 'SettingsLocation', '--value', [System.IO.Path]::GetFullPath($DataDirectory), '-t', '5000')

# Security-relevant edits ask for confirmation ("確認して保存") when the edited scope is left or checked.
function Confirm-SettingsSave {
    & winapp ui wait-for PrimaryButton -t 3000 -a $AppPid | Out-Null
    if ($LASTEXITCODE -eq 0) { Invoke-UI @('invoke', 'PrimaryButton') }
}
function Select-AgentScope([string]$Scope) {
    Invoke-UI @('invoke', $Scope)
    Confirm-SettingsSave
    # A pending confirmation cancels the first navigation, so select the scope again.
    Invoke-UI @('invoke', $Scope)
}
function Read-Settings { Get-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Raw | ConvertFrom-Json }
function Wait-Settings([scriptblock]$Condition, [string]$Message) {
    for ($i = 0; $i -lt 20; $i++) { if (& $Condition (Read-Settings)) { return }; Start-Sleep -Milliseconds 250 }
    throw $Message
}
# Each changed field is confirmed separately and a confirmation consumes the check request, so keep checking
# (confirming any dialog) until the profile can be used.
function Invoke-CliCheck {
    for ($i = 0; $i -lt 6; $i++) {
        & winapp ui wait-for PrimaryButton -t 1000 -a $AppPid | Out-Null
        if ($LASTEXITCODE -eq 0) { Invoke-UI @('invoke', 'PrimaryButton') }
        Invoke-UI @('invoke', 'CheckCli')
        & winapp ui wait-for UseAgentProfile -p IsEnabled --value True -t 8000 -a $AppPid | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
    }
    throw 'CLI check did not enable the profile.'
}
$fixtureExecutable = (Get-Command pwsh -ErrorAction Stop).Source
$fixtureArguments = '-NoProfile -NonInteractive -Command "[Console]::Write([Console]::In.ReadToEnd())"'

Check 'Navigation and initialized settings' {
    Invoke-UI @('wait-for', 'NavOverview', '-t', '5000')
    Invoke-UI @('invoke', 'NavAgentSettings')
    Invoke-UI @('wait-for', 'AgentScopeCommon', '-t', '3000')
    Select-AgentScope 'AgentScopeCommon'
    Invoke-UI @('wait-for', 'WorkspaceRoot', '-t', '3000')
    Invoke-UI @('invoke', 'NavWorkbench')
    Invoke-UI @('wait-for', 'Prompt', '-t', '3000')
    Invoke-UI @('wait-for', 'RunAgent', '-t', '3000')
}

Check 'Workspace persists after confirmation' {
    Invoke-UI @('invoke', 'NavAgentSettings')
    Select-AgentScope 'AgentScopeCommon'
    Invoke-UI @('set-value', 'WorkspaceRoot', $DataDirectory)
    Select-AgentScope 'AgentScopeClaude'
    Wait-Settings { param($s) $s.WorkspaceRoot -eq $DataDirectory } 'Confirmed workspace was not persisted.'
}

Check 'Claude profile shows its defaults without running a paid model' {
    Invoke-UI @('invoke', 'NavAgentSettings')
    Select-AgentScope 'AgentScopeClaude'
    Invoke-UI @('wait-for', 'AgentExecutable', '--value', 'claude', '-t', '3000')
    Invoke-UI @('wait-for', 'CheckCli', '-t', '3000')
    if ((Read-Settings).AgentProvider -eq 'claude') { throw 'Viewing the Claude profile switched the active agent.' }
}

Check 'Custom PowerShell fixture can be checked and used' {
    Invoke-UI @('invoke', 'NavAgentSettings')
    Select-AgentScope 'AgentScopeCustom'
    Invoke-UI @('set-value', 'AgentExecutable', $fixtureExecutable)
    Invoke-UI @('invoke', 'AgentProfileDetails')
    Invoke-UI @('set-value', 'AgentArguments', $fixtureArguments)
    Invoke-CliCheck
    Invoke-UI @('wait-for', 'UseAgentProfile', '-p', 'IsEnabled', '--value', 'True', '-t', '20000')
    Invoke-UI @('invoke', 'UseAgentProfile')
    Confirm-SettingsSave
    Wait-Settings { param($s) $s.AgentProvider -eq 'custom' -and $s.AgentExecutable -eq $fixtureExecutable -and
        $s.AgentArguments -eq $fixtureArguments -and $s.WorkspaceRoot -eq $DataDirectory } 'Custom PowerShell profile was not activated.'
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
    Invoke-UI @('invoke', 'ConnectionScopeMattermost')
    Invoke-UI @('wait-for', 'MattermostServerUrl', '-t', '3000')
    Invoke-UI @('invoke', 'NavChat')
    Invoke-UI @('wait-for', 'ConnectMattermost', '-t', '3000')
    # Approve/reject actions only exist while a chat request is pending; none is pending here.
    & winapp ui wait-for ApproveChat -t 1000 -a $AppPid | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'An approval action is shown without a pending chat request.' }
}
Check 'Durable execution metadata and history navigation' {
    Invoke-UI @('invoke', 'NavHistory')
    Invoke-UI @('invoke', 'ExecutionHistoryTab')
    Invoke-UI @('wait-for', 'ExecutionHistoryList', '-t', '3000')
    Invoke-UI @('wait-for', 'ExecutionHistoryTab', '-t', '3000')
    $path = Join-Path $DataDirectory 'history.json'
    $historyText = Get-Content -LiteralPath $path -Raw
    $history = $historyText | ConvertFrom-Json
    if (-not ($history.Records | Where-Object { $_.Source -eq 'local' -and $_.Status -eq 'completed' })) { throw 'Completed execution metadata was not persisted.' }
    if ($historyText.Contains('winui-smoke-no-model') -or $historyText -match '"(Prompt|Output|Error)"\s*:') { throw 'History leaked prompt or output.' }
    # The audit log is append-only JSONL segments (one event per line).
    $audit = Get-ChildItem -LiteralPath $DataDirectory -Filter 'audit-segment-*.jsonl' | Get-Content |
        Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }
    if (-not ($audit | Where-Object { $_.Kind -eq 'completed' })) { throw 'Completion audit event was not persisted.' }
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
