param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$FixtureDirectory = (Join-Path $PSScriptRoot 'artifacts/performance-fixture')
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [System.IO.Path]::GetFullPath($DataDirectory)
$fixturePath = [System.IO.Path]::GetFullPath($FixtureDirectory)
$resultsPath = Join-Path $artifactRoot 'performance-ui-results.json'
$markerName = '.cab-performance-ui-e2e'
$results = [System.Collections.Generic.List[object]]::new()
$currentStep = 'preflight'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Assert-UnderArtifactRoot([string]$Path) {
    $prefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Performance UI data and fixture directories must be under desktop/artifacts.'
    }
}

function Invoke-UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid *> $null
    if ($LASTEXITCODE -ne 0) { throw 'WinApp UI automation failed.' }
}

function Wait-UI([string]$Selector, [string]$Value = '', [int]$TimeoutMs = 30000) {
    $args = @('wait-for', $Selector, '-t', "$TimeoutMs")
    if ($Value.Length -gt 0) { $args += @('--value', $Value, '--contains') }
    Invoke-UI $args
}

function Select-ComboItem([string]$AutomationId, [string]$ItemAutomationId) {
    Invoke-UI @('invoke', $AutomationId)
    Invoke-UI @('invoke', $ItemAutomationId)
}

function Expand-LatestExecutionDetails {
    $window = (Get-Process -Id $AppPid).MainWindowHandle
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($window)
    $listCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ExecutionHistoryList')
    $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
    if ($null -eq $list) { throw 'Execution history list was not available for detail expansion.' }

    $expanderCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Expander')
    $expanders = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $expanderCondition)
    if ($expanders.Count -eq 0) { throw 'No realized execution detail expander was found.' }

    # ExecutionHistoryList is newest first; expand realized rows so usage fields become visible to UIA.
    foreach ($expander in $expanders) {
        $pattern = $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $pattern.Expand()
        }
    }
}

function Add-Pass([string]$Name) { $results.Add(@{ name = $Name; passed = $true }) }

function Read-Json([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

try {
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'AppPid does not identify a running process.' }
    Assert-UnderArtifactRoot $dataPath
    Assert-UnderArtifactRoot $fixturePath
    $fixtureMarker = Join-Path $fixturePath '.cab-offline-performance-fixture'
    $claudeExe = Join-Path $fixturePath 'claude.exe'
    if (-not (Test-Path -LiteralPath $fixtureMarker) -or -not (Test-Path -LiteralPath $claudeExe)) {
        throw 'The guarded offline performance fixture is missing.'
    }

    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    $marker = Join-Path $dataPath $markerName
    if (-not (Test-Path -LiteralPath $marker)) {
        foreach ($file in @('settings.json', 'history.json', 'sessions.json', 'audit.json')) {
            if (Test-Path -LiteralPath (Join-Path $dataPath $file)) {
                throw 'Use a fresh isolated app data directory for the first performance UI E2E run.'
            }
        }
        Set-Content -LiteralPath $marker -Value 'Isolated offline performance UI E2E directory.' -Encoding utf8
    }

    $workspace = Join-Path $dataPath 'workspace'
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null

    $currentStep = 'verify-app-data-directory'
    Invoke-UI @('invoke', 'NavOverview')
    Wait-UI 'SettingsLocation' $dataPath
    Add-Pass 'App uses the isolated data directory'

    $currentStep = 'configure-offline-claude-fixture'
    Invoke-UI @('invoke', 'NavAgentSettings')
    Wait-UI 'AgentProvider'
    Select-ComboItem 'AgentProvider' 'ProviderOptionClaude'
    Invoke-UI @('set-value', 'WorkspaceRoot', $workspace)
    Invoke-UI @('set-value', 'AgentExecutable', $claudeExe)
    Invoke-UI @('set-value', 'AgentModel', '')
    Invoke-UI @('invoke', 'SaveSettings')
    Wait-UI 'Title' '設定を保存しました'

    $settingsPath = Join-Path $dataPath 'settings.json'
    $saved = Read-Json $settingsPath
    if ($saved.AgentProvider -ne 'claude' -or $saved.AgentExecutable -ne $claudeExe -or
        $saved.WorkspaceRoot -ne $workspace -or $saved.AgentModel) {
        throw 'Offline Claude fixture settings were not saved as expected.'
    }
    Add-Pass 'Claude fixture settings persisted with no explicit model'

    $currentStep = 'run-offline-fixture'
    Invoke-UI @('invoke', 'NavWorkbench')
    Wait-UI 'Prompt'
    $prompt = 'cab-performance-fixture-check'
    Invoke-UI @('set-value', 'Prompt', $prompt)
    Invoke-UI @('invoke', 'RunAgent')
    Wait-UI 'RunConfirmation'
    Invoke-UI @('invoke', 'PrimaryButton')
    Wait-UI 'AgentOutput' 'claude-e2e: new' 20000
    Wait-UI 'LocalUsageSummary' '入力トークン合計: 40' 10000
    Wait-UI 'LocalUsageSummary' 'キャッシュ再利用率（入力トークン比）: 75.0%' 5000
    Wait-UI 'LocalUsageSummary' '会話累計（過去の実行を含む）' 5000
    Add-Pass 'Offline Claude output and scoped token usage displayed'

    $currentStep = 'verify-persisted-usage-without-content'
    $historyPath = Join-Path $dataPath 'history.json'
    if (-not (Test-Path -LiteralPath $historyPath)) { throw 'Execution history file was not created.' }
    $historyText = Get-Content -LiteralPath $historyPath -Raw
    $history = $historyText | ConvertFrom-Json
    $record = $history.Records | Where-Object { $_.Source -eq 'local' -and $_.Status -eq 'completed' } | Select-Object -Last 1
    if ($null -eq $record -or $null -eq $record.Usage -or $null -eq $record.Metrics) {
        throw 'Completed execution usage or performance metrics were not persisted.'
    }
    if ($record.Usage.Source -ne 'claude.stream-json' -or $record.Usage.Scope -ne 'thread-total' -or
        $record.Usage.InputTokens -ne 10 -or $record.Usage.CachedInputTokens -ne 30 -or
        $record.Usage.CacheWriteInputTokens -ne 0 -or $record.Usage.OutputTokens -ne 5) {
        throw 'Persisted provider usage values or scope did not match the offline fixture.'
    }
    if ($null -eq $record.Metrics.ProcessStartMilliseconds -or $null -eq $record.Metrics.TotalMilliseconds) {
        throw 'Persisted process timing metrics were missing.'
    }
    if ($historyText.Contains($prompt) -or $historyText.Contains('claude-e2e: new') -or
        $historyText -match '"(Prompt|Output|Error)"\s*:') {
        throw 'History contains prompt or output content.'
    }
    Add-Pass 'Usage and timing persisted while prompt and output stayed out of history'

    $currentStep = 'verify-history-usage-display'
    Invoke-UI @('invoke', 'NavHistory')
    Invoke-UI @('invoke', 'ExecutionHistoryTab')
    Wait-UI 'ExecutionHistoryList'
    Expand-LatestExecutionDetails
    Wait-UI '入力トークン合計: 40' '' 10000
    Wait-UI 'キャッシュ再利用率（入力トークン比）: 75.0%' '' 5000
    Wait-UI '会話累計（過去の実行を含む）' '' 5000
    Add-Pass 'History page displays the same reported usage scope and counts'
}
catch {
    # Error details from UI/provider output are deliberately omitted from the artifact.
    $results.Add(@{ name = $currentStep; passed = $false; error = 'The step failed; inspect the isolated app state locally.' })
}
finally {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultsPath -Encoding utf8
}

$results | ForEach-Object { Write-Output "$(if ($_.passed) {'PASS'} else {'FAIL'}): $($_.name)" }
if ($results.Where({ -not $_.passed }).Count -gt 0) { exit 1 }
