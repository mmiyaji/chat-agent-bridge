param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [int]$TimeoutSeconds = 30
)

# Isolated UI regression for provider-scoped CLI verification persistence. The app and
# the agy.exe fixture only run when this script is explicitly invoked.
$ErrorActionPreference = 'Stop'
$appDirectoryFull = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $appDirectoryFull 'ChatAgentBridge.Desktop.exe'
$testAgentFull = [IO.Path]::GetFullPath($TestAgentPath)
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Desktop executable not found: $executable" }
if (-not (Test-Path -LiteralPath $testAgentFull -PathType Leaf)) { throw "Test agent executable not found: $testAgentFull" }

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$testRoot = Join-Path $artifactDirectory ('cli-verification-ui-' + [guid]::NewGuid().ToString('N'))
$fixtureBin = Join-Path $testRoot 'fixture-bin'
New-Item -ItemType Directory -Force -Path $fixtureBin | Out-Null
$fixtureExe = Join-Path $fixtureBin 'agy.exe'
Get-ChildItem -LiteralPath (Split-Path $testAgentFull) -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $fixtureBin -Recurse -Force }
Copy-Item -LiteralPath $testAgentFull -Destination $fixtureExe

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$script:startedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:results = [Collections.Generic.List[object]]::new()
$script:activeProcess = $null
$script:activeWindow = $null
$script:fatal = $null

function Add-Pass([string]$Name, [string]$Detail = '') { $script:results.Add(@{ name=$Name; status='PASS'; detail=$Detail }); Write-Host ("PASS "+$Name) }
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Get-MainWindow([Diagnostics.Process]$Process) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)) {
        if ($window.Current.Name -notmatch 'PopupHost') { return $window }
    }
    return $null
}
function Wait-MainWindow([Diagnostics.Process]$Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) { throw "App exited during startup with code $($Process.ExitCode)." }
        $window = Get-MainWindow $Process
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'No accessible app window appeared on the interactive Windows desktop.'
}
function Wait-Element([string]$Id, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:activeWindow $Id
        if ($element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for AutomationId '$Id'."
}
function Invoke-Element([string]$Id) {
    $element = Wait-Element $Id
    if (-not $element.Current.IsEnabled) { throw "UI element is disabled: $Id" }
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return
    }
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke(); return
    }
    throw "No UI Automation selection or invoke pattern is available: $Id"
}
function Get-Text([string]$Id) {
    $element = Wait-Element $Id
    return $element.Current.Name
}
function Wait-Text([string]$Id, [scriptblock]$Predicate, [string]$Description, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $text = Get-Text $Id
        if (& $Predicate $text) { return $text }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Id $Description; last text='$text'."
}
function Write-Json([string]$Path, [object]$Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}
function New-Fixture([string]$DataDirectory, [string]$Workspace, [string]$Language = 'en', [string]$Theme = 'light') {
    New-Item -ItemType Directory -Force -Path $DataDirectory, $Workspace | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion=1; WorkspaceRoot=$Workspace; AgentExecutable=$fixtureExe; AgentArguments='--output-format stream-json'
        AgentMode='exec'; AgentProvider='antigravity'; AgentModel=''; AgentReasoningEffort=''; AutoConnectProvider='none'
        RequireApproval=$true; AgentPermissionMode='inherit'; AllowAgentNetwork=$false; ChatApprovalMode='first'
        AutoApproveSwitchCommands=$false; ChatResultMode='manual'; ChatFollowUpMode='queue'; FileAttachmentApprovalMode='always'
    }
    Write-Json (Join-Path $DataDirectory 'tips.json') @{ Seen=@('chat-connect') }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion=3; step=4; status='completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion.' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{ PolicyVersion=$policyMatch.Groups[1].Value; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language=$Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme=$Theme }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{ schemaVersion=1; automaticChecksEnabled=$false; includePrereleases=$true; lastAttemptUtc=$null; availableRelease=$null }
}
function Set-ElementValue([string]$Id, [string]$Value) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw "$Id does not support ValuePattern." }
    ([Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}
function Start-TestApp([string]$DataDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $appDirectoryFull
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add([IO.Path]::GetFullPath($DataDirectory))
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Desktop process could not be started.' }
    $script:startedProcesses.Add($process); $script:activeProcess = $process
    $script:activeWindow = Wait-MainWindow $process
    try { $script:activeWindow.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 250
}
function Stop-TestApp {
    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        $script:activeProcess.Kill($true)
        if (-not $script:activeProcess.WaitForExit(7000)) { throw 'Isolated app process did not exit during cleanup.' }
    }
    $script:activeProcess = $null; $script:activeWindow = $null
}
function Open-AntigravityEditor {
    Invoke-Element 'NavAgentSettings'
    Start-Sleep -Milliseconds 300
    Invoke-Element 'AgentScopeAntigravity'
    [void](Wait-Element 'AgentExecutable')
    [void](Wait-Element 'CheckCli')
}
function Assert-CheckedEditor([string]$Label) {
    $text = Wait-Text 'AgentEditorStatus' { param($v) $v -match 'CLI verified|CLI確認済み' } 'to show the checked status'
    $models = Wait-Element 'ModelCandidates'
    $expand = [Windows.Automation.ExpandCollapsePattern]$models.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 200
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:activeProcess.Id)
    $children = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    $foundModel = $false
    foreach ($child in $children) { if ($child.Current.Name -match 'fixture-agy-fast|Fixture Agy Fast') { $foundModel = $true; break } }
    $expand.Collapse()
    if (-not $foundModel) { throw "$Label did not expose the cached Antigravity model choice." }
    Add-Pass $Label $text
}

try {
    $dataDirectory = Join-Path $testRoot 'data'
    $workspace = Join-Path $testRoot 'workspace'
    New-Fixture $dataDirectory $workspace
    $settingsPath = Join-Path $dataDirectory 'settings.json'
    $settingsBefore = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settingsHashBefore = (Get-FileHash -LiteralPath $settingsPath).Hash

    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    Invoke-Element 'CheckCli'
    [void](Wait-Text 'AgentEditorStatus' { param($v) $v -match 'CLI verified|CLI確認済み' } 'to complete the CLI check')

    $cachePath = Join-Path $dataDirectory 'agent-cli-cache/antigravity/cli-verification.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (Test-Path -LiteralPath $cachePath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) { throw "Provider verification cache was not written: $cachePath" }
    $cache = Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json
    if (@($cache.Models).Count -lt 2 -or @($cache.Models | Where-Object { $_.Id -eq 'fixture-agy-fast' }).Count -ne 1) { throw 'Provider cache did not persist the Antigravity model catalog.' }
    if (-not $cache.Probe.ProviderRecognized -or [string]::IsNullOrWhiteSpace($cache.Probe.Version)) { throw 'Cache did not contain a successful recognized CLI probe.' }
    Add-Pass 'Successful Antigravity CLI check writes non-authoritative provider cache with models' $cachePath

    $settingsAfter = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($name in @('WorkspaceRoot','AgentExecutable','AgentArguments','AgentProvider','AutoConnectProvider')) {
        if ($settingsAfter.$name -ne $settingsBefore.$name) { throw "CLI check unexpectedly changed saved $name." }
    }
    Add-Pass 'CLI check leaves active provider, executable and workspace settings unchanged'

    # Leave and reopen the provider pane to ensure the current editor instance restores its cache.
    Invoke-Element 'AgentScopeCommon'
    Invoke-Element 'AgentScopeAntigravity'
    Assert-CheckedEditor 'Switching away from and reopening Antigravity editor restores check and models'
    Invoke-Element 'NavOverview'
    $overview = Wait-Text 'OverviewCliTile' { param($v) $v -match 'Check passed|Previously checked|確認済み' } 'to expose a verified General overview CLI state'
    Add-Pass 'General overview exposes the active Antigravity CLI verification state' $overview

    Stop-TestApp
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    Assert-CheckedEditor 'App restart restores persisted Antigravity check and model catalog'
    Invoke-Element 'NavOverview'
    $overview = Wait-Text 'OverviewCliTile' { param($v) $v -match 'Check passed|Previously checked|確認済み' } 'to retain its verified state after app restart'
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHashBefore) { throw 'CLI diagnostics changed the active settings file.' }
    Add-Pass 'General overview remains checked after app restart' $overview
    Stop-TestApp

    # A changed executable stamp invalidates the successful cache without deleting it.
    $newTime = [DateTime]::UtcNow.AddMinutes(2)
    [IO.File]::SetLastWriteTimeUtc($fixtureExe, $newTime)
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    $status = Wait-Text 'AgentEditorStatus' { param($v) $v -match 'CLI not checked|CLI未確認' } 'to invalidate after executable stamp changed'
    Add-Pass 'Changed CLI executable stamp invalidates restored verification' $status
    Stop-TestApp

    # A successful check in another provider's editor must never mark the active CLI
    # checked or switch the saved provider. This fixture uses a fake Claude apphost.
    $inactiveData = Join-Path $testRoot 'inactive-provider-data'
    $inactiveWorkspace = Join-Path $testRoot 'inactive-provider-workspace'
    New-Fixture $inactiveData $inactiveWorkspace 'ja' 'dark'
    $claudeFixture = Join-Path $fixtureBin 'claude.exe'
    Copy-Item -LiteralPath $testAgentFull -Destination $claudeFixture -Force
    $inactiveSettingsPath = Join-Path $inactiveData 'settings.json'
    $inactiveSettings = Get-Content -LiteralPath $inactiveSettingsPath -Raw | ConvertFrom-Json
    $inactiveSettings.AgentProvider = 'claude'
    $inactiveSettings.AgentExecutable = $claudeFixture
    $inactiveSettings.AgentArguments = '--print --output-format stream-json --verbose'
    Write-Json $inactiveSettingsPath $inactiveSettings
    $inactiveBefore = Get-Content -LiteralPath $inactiveSettingsPath -Raw | ConvertFrom-Json
    $inactiveHashBefore = (Get-FileHash -LiteralPath $inactiveSettingsPath).Hash
    Write-Json (Join-Path $inactiveData 'agent-settings-drafts.json') @{ SchemaVersion=1; Drafts=@(@{Provider='antigravity'; Executable=$fixtureExe; Arguments='--output-format stream-json'; Mode='exec'; Model=$null; ReasoningEffort=$null; PermissionMode='inherit'; AllowNetwork=$false}) }
    Start-TestApp $inactiveData
    Open-AntigravityEditor
    Invoke-Element 'CheckCli'
    [void](Wait-Text 'AgentEditorStatus' { param($v) $v -match 'CLI verified|CLI確認済み' } 'to complete the inactive Antigravity CLI check')
    Invoke-Element 'NavOverview'
    $inactiveOverview = Wait-Text 'OverviewCliTile' { param($v) $v -match 'not checked|未確認' } 'to keep the active Claude CLI unchecked'
    $inactiveAfter = Get-Content -LiteralPath $inactiveSettingsPath -Raw | ConvertFrom-Json
    if ($inactiveAfter.AgentProvider -ne $inactiveBefore.AgentProvider -or $inactiveAfter.AgentExecutable -ne $inactiveBefore.AgentExecutable) {
        throw 'Checking the inactive Antigravity profile changed the saved active Claude profile.'
    }
    if ((Get-FileHash -LiteralPath $inactiveSettingsPath).Hash -ne $inactiveHashBefore) { throw 'Inactive CLI diagnostics changed the active settings file.' }
    Add-Pass 'Checking inactive Antigravity profile leaves active Claude overview unchecked and settings unchanged' $inactiveOverview
    Stop-TestApp

    Add-Pass 'CLI verification UI smoke completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name='CLI verification UI regression'; status='FAIL'; detail=$script:fatal })
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(7000) | Out-Null } } catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() } catch { Write-Warning "Could not save UI smoke results: $_" }
}
if ($script:fatal) { throw $script:fatal }
Write-Output "CLI verification UI artifacts: $testRoot"
