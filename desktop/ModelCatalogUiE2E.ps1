param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$FixtureDirectory = (Join-Path $PSScriptRoot 'artifacts/performance-fixture')
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$fixturePath = [IO.Path]::GetFullPath($FixtureDirectory)
foreach ($path in @($dataPath, $fixturePath)) {
    if (-not $path.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use isolated directories under desktop/artifacts.'
    }
}
if (-not (Test-Path (Join-Path $fixturePath '.cab-offline-performance-fixture'))) { throw 'Offline fixture marker is missing.' }
$results = [Collections.Generic.List[object]]::new()
function UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid *> $null
    if ($LASTEXITCODE -ne 0) { throw "UI operation failed: $($Arguments[0])" }
}
function Wait([string]$Id, [string]$Value = '') {
    $args = @('wait-for', $Id, '-t', '10000')
    if ($Value) { $args += @('--value', $Value, '--contains') }
    UI $args
}
function Pass([string]$Name) { $results.Add(@{name=$Name; passed=$true}) }
# Settings are now saved automatically (there is no SaveSettings button), so wait for settings.json to reflect the edit.
function Wait-Saved([scriptblock]$Predicate, [string]$What) {
    $path = Join-Path $dataPath 'settings.json'
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $path) {
            try { $saved = Get-Content $path -Raw | ConvertFrom-Json; if (& $Predicate $saved) { return $saved } } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Settings were not saved automatically: $What"
}
# Agent edits are saved as separate drafts when the field loses focus; a changed CLI command asks for confirmation first.
function Commit-Draft {
    UI @('focus','CheckCli')
    try { UI @('wait-for','SensitiveSettingsConfirmation','-t','3000'); UI @('invoke','PrimaryButton') } catch { }
    Start-Sleep -Milliseconds 800
}
# The edited profile only becomes the active settings after a CLI check and an explicit Use.
function Use-Profile {
    UI @('invoke','CheckCli')
    UI @('wait-for','CheckCli','-p','IsEnabled','--value','True','-t','20000')
    UI @('wait-for','UseAgentProfile','-p','IsEnabled','--value','True','-t','20000')
    UI @('invoke','UseAgentProfile')
    # Switching provider or CLI command asks for confirmation before it is applied.
    try { UI @('wait-for','SensitiveSettingsConfirmation','-t','3000'); UI @('invoke','PrimaryButton') } catch { }
}
function Select-Candidate([string]$ComboId, [string]$Label, [switch]$Prefix) {
    UI @('invoke',$ComboId)
    $tree = (& winapp ui inspect $ComboId -a $AppPid --depth 5 --json) | ConvertFrom-Json
    function Find-Item($Nodes) {
        foreach ($node in $Nodes) {
            if ($node.type -eq 'ListItem' -and ($node.name -eq $Label -or ($Prefix -and $node.name.StartsWith($Label + ' ')))) { $node.selector }
            if ($node.children) { Find-Item $node.children }
        }
    }
    $selector = @($tree.windows | ForEach-Object { Find-Item $_.elements }) | Select-Object -First 1
    if (-not $selector) { throw 'Expected offline model candidate is missing.' }
    UI @('invoke',$selector)
}
$step = 'preflight'
try {
    UI @('invoke', 'NavOverview')
    # The settings location now lives inside the collapsed technical-details expander.
    Wait 'SavedSettingsDetails'
    $details = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $AppPid).MainWindowHandle).FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SavedSettingsDetails'))
    $expand = $details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $expand.Expand() }
    Wait 'SettingsLocation' $dataPath
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    $marker = Join-Path $dataPath '.cab-model-catalog-ui'
    if (-not (Test-Path $marker)) {
        foreach ($file in @('settings.json','sessions.json','history.json','audit.json')) {
            if (Test-Path (Join-Path $dataPath $file)) { throw 'Use a fresh isolated data directory.' }
        }
        Set-Content $marker 'Offline CLI model catalog UI test'
    }
    foreach ($provider in @('codex','claude')) {
        $step = "$provider candidates and explicit selection"
        UI @('invoke','NavAgentSettings')
        # The workspace now belongs to the common scope; the CLI path and model belong to the provider scope.
        UI @('invoke','AgentScopeCommon')
        UI @('set-value','WorkspaceRoot',$dataPath)
        UI @('invoke',$(if($provider -eq 'codex'){'AgentScopeCodex'}else{'AgentScopeClaude'}))
        if (-not (Test-Path (Join-Path $dataPath 'settings.json'))) {
            # Leaving the common scope commits the draft; a newly trusted workspace folder asks for confirmation first.
            UI @('wait-for','PrimaryButton','-t','10000')
            UI @('invoke','PrimaryButton')
            Wait-Saved { param($s) $s.WorkspaceRoot -eq $dataPath } 'workspace' | Out-Null
            UI @('invoke',$(if($provider -eq 'codex'){'AgentScopeCodex'}else{'AgentScopeClaude'}))
        }
        UI @('set-value','AgentExecutable',(Join-Path $fixturePath "$provider.exe"))
        UI @('set-value','AgentModel','manual-model-before-fetch')
        Commit-Draft
        UI @('invoke','FetchModels')
        Wait 'ModelCatalogStatus' '件'
        Wait 'AgentModel' 'manual-model-before-fetch'
        # Fetching candidates and drafting never change the active settings; only Use applies them.
        $current = Get-Content (Join-Path $dataPath 'settings.json') -Raw | ConvertFrom-Json
        if ($current.AgentModel -or $current.AgentExecutable -like '*.exe') { throw 'Fetching candidates changed the active settings unexpectedly.' }
        $providerLabel = if ($provider -eq 'codex') { 'Codex' } else { 'Claude' }
        Select-Candidate 'ModelCandidates' "Fixture $providerLabel Small (fixture-$provider-small)"
        Wait 'AgentModel' "fixture-$provider-small"
        Select-Candidate 'ReasoningEffortCandidates' 'low' -Prefix
        Pass "$provider reasoning effort is chosen from the selected model CLI candidates"
        Pass "$provider CLI candidates leave manual text intact until selected"
    }
    $step = 'save selected ID and retain manual entry'
    Use-Profile
    $saved = Wait-Saved { param($s) $s.AgentModel -eq 'fixture-claude-small' -and $s.AgentReasoningEffort -eq 'low' } 'selected claude model and effort'
    if ($saved.AgentModel -ne 'fixture-claude-small') { throw 'Selection saved a label instead of a model ID.' }
    if ($saved.AgentReasoningEffort -ne 'low') { throw 'Selected reasoning effort did not persist.' }
    UI @('set-value','AgentModel','manual-custom-model')
    Commit-Draft
    # A model ID absent from the CLI candidates cannot be verified, so it stays in the saved agent draft
    # (with the previous effort cleared) and the active settings keep the last applied profile.
    $draftsPath = Join-Path $dataPath 'agent-settings-drafts.json'
    $draft = $null
    for ($i = 0; $i -lt 40 -and -not $draft; $i++) {
        $draft = @((Get-Content $draftsPath -Raw | ConvertFrom-Json).Drafts) | Where-Object { $_.Provider -eq 'claude' -and $_.Model -eq 'manual-custom-model' } | Select-Object -First 1
        if (-not $draft) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $draft) { throw 'Manual model entry was not preserved.' }
    if ($draft.ReasoningEffort) { throw 'Manual model edit retained the previous model reasoning effort.' }
    Pass 'Selected IDs persist when applied and manually entered IDs persist as drafts'

    $step = 'session override candidates'
    $sessionId = [guid]::NewGuid().ToString('D')
    $sessions = @{ SchemaVersion=1; Bindings=@(@{
        Key=@{Provider='slack';ChannelId='model-catalog-channel';ThreadId='model-catalog-thread'}
        WorkspaceRoot=$dataPath;AgentExecutable=(Join-Path $fixturePath 'codex.exe');AgentArguments='exec --json -'
        SessionId=$sessionId;UpdatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    }) }
    $sessionPath = Join-Path $dataPath 'sessions.json'
    $sessions | ConvertTo-Json -Depth 8 | Set-Content $sessionPath -Encoding utf8
    UI @('invoke','NavHistory')
    UI @('invoke','SavedConversationsTab')
    UI @('invoke','RefreshHistory')
    Wait 'SavedSessionsList'
    # The row is titled by its workspace folder now, so select the only saved conversation by its list position.
    $list = (& winapp ui inspect SavedSessionsList -a $AppPid --depth 2 --json) | ConvertFrom-Json
    $row = @($list.windows | ForEach-Object { $_.elements } | ForEach-Object { $_.children } | Where-Object { $_.type -eq 'ListItem' }) | Select-Object -First 1
    if (-not $row) { throw 'Saved conversation row is missing.' }
    UI @('invoke',$row.selector)
    UI @('scroll-into-view','EditSessionModel')
    UI @('invoke','EditSessionModel')
    Wait 'SessionModelDialog'
    UI @('invoke','SessionFetchModels')
    Wait 'SessionModelCatalogStatus' '件'
    Select-Candidate 'SessionModelCandidates' 'Fixture Codex Large (fixture-codex-large)'
    Wait 'SessionModelOverride' 'fixture-codex-large'
    Select-Candidate 'SessionReasoningEffortCandidates' 'xhigh' -Prefix
    UI @('invoke','PrimaryButton')
    Wait 'HistoryStatus' '次回の実行モデルを保存しました'
    UI @('wait-for','EditSessionModel','-p','IsEnabled','--value','True','-t','5000')
    $saved = Get-Content $sessionPath -Raw | ConvertFrom-Json
    if ($saved.Bindings[0].Model -ne 'fixture-codex-large') { throw 'Session candidate was not persisted.' }
    if ($saved.Bindings[0].ReasoningEffort -ne 'xhigh') { throw 'Session reasoning effort was not persisted.' }
    Pass 'Session picker uses the saved session CLI independently of global provider'

    $step = 'preserve session effort without refetching'
    UI @('invoke','EditSessionModel')
    Wait 'SessionModelDialog'
    UI @('invoke','PrimaryButton')
    Wait 'HistoryStatus' '次回の実行モデルを保存しました'
    UI @('wait-for','EditSessionModel','-p','IsEnabled','--value','True','-t','5000')
    $saved = Get-Content $sessionPath -Raw | ConvertFrom-Json
    if ($saved.Bindings[0].ReasoningEffort -ne 'xhigh') { throw 'Saving an untouched session erased the saved effort.' }
    Pass 'Opening and saving a session without fetching preserves its effort override'

    $step = 'reset session effort'
    UI @('invoke','EditSessionModel')
    Wait 'SessionModelDialog'
    Select-Candidate 'SessionReasoningEffortCandidates' 'エージェント設定に従う'
    UI @('invoke','PrimaryButton')
    Wait 'HistoryStatus' '次回の実行モデルを保存しました'
    UI @('wait-for','EditSessionModel','-p','IsEnabled','--value','True','-t','5000')
    $saved = Get-Content $sessionPath -Raw | ConvertFrom-Json
    if ($saved.Bindings[0].ReasoningEffort) { throw 'Session effort reset did not persist.' }
    Pass 'Session effort override can be explicitly cleared'

    foreach ($file in @('history.json','audit.json')) {
        if (Test-Path (Join-Path $dataPath $file)) { throw 'Catalog retrieval unexpectedly executed an agent.' }
    }
    Pass 'Catalog queries create no agent execution history'

    $step = 'unavailable CLI preserves manual entry'
    UI @('invoke','NavAgentSettings')
    UI @('invoke','AgentScopeClaude')
    UI @('set-value','AgentExecutable',(Join-Path $dataPath 'missing-claude.exe'))
    UI @('set-value','AgentModel','manual-model-after-failure')
    Commit-Draft
    UI @('invoke','FetchModels')
    Wait 'ModelCatalogStatus' '返りませんでした'
    Wait 'AgentModel' 'manual-model-after-failure'
    UI @('wait-for','FetchModels','-p','IsEnabled','--value','True','-t','5000')
    Pass 'Failed lookup retains manual text and permits another lookup'

    $step = 'record selected effort on an offline execution'
    UI @('set-value','AgentExecutable',(Join-Path $fixturePath 'claude.exe'))
    UI @('set-value','AgentModel','manual-model-after-failure')
    Commit-Draft
    UI @('invoke','FetchModels')
    Wait 'ModelCatalogStatus' '件'
    Select-Candidate 'ModelCandidates' 'Fixture Claude Small (fixture-claude-small)'
    Select-Candidate 'ReasoningEffortCandidates' 'low' -Prefix
    # The reselected model and effort equal the applied profile, so there is nothing further to apply.
    Wait-Saved { param($s) $s.AgentModel -eq 'fixture-claude-small' -and $s.AgentReasoningEffort -eq 'low' -and $s.AgentExecutable -eq (Join-Path $fixturePath 'claude.exe') } 'offline claude selection' | Out-Null
    UI @('invoke','NavWorkbench')
    UI @('set-value','Prompt','offline-effort-check')
    UI @('invoke','RunAgent')
    Wait 'RunConfirmation'
    UI @('invoke','PrimaryButton')
    Wait 'AgentOutput' 'claude-e2e: new'
    Wait 'LocalUsageSummary' '入力トークン合計: 40'
    $history = Get-Content (Join-Path $dataPath 'history.json') -Raw | ConvertFrom-Json
    $record = $history.Records | Where-Object Status -eq 'completed' | Select-Object -First 1
    if ($record.AgentReasoningEffort -ne 'low') { throw 'Execution history did not retain the chosen effort.' }
    Pass 'Offline execution records the selected reasoning effort in history'
}
catch { $results.Add(@{name=$step;passed=$false;error='Inspect the isolated UI test state; raw CLI output is omitted.'}) }
finally { $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $artifactRoot 'model-catalog-ui-results.json') -Encoding utf8 }
$results | ForEach-Object { Write-Output "$(if($_.passed){'PASS'}else{'FAIL'}): $($_.name)" }
if ($results.Where({-not $_.passed}).Count) { exit 1 }
