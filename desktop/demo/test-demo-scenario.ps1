# Offline tests for scenario approval/response helpers and the runner's main scenario
# body. This parses and extracts selected AST nodes; it never dot-sources the runner
# or loads its app, CDP, or desktop automation helpers.
$ErrorActionPreference = 'Stop'
$runnerPath = Join-Path $PSScriptRoot 'run-scenario.ps1'
$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Could not parse run-scenario.ps1: $($parseErrors[0].Message)" }

$helperNames = @('Complete-ApprovalStep', 'Require-BotResponse')
$helpers = $runnerAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $helperNames
}, $true)
if ($helpers.Count -ne $helperNames.Count) { throw 'Expected scenario helper functions were not found.' }
$scenarioTry = $runnerAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.TryStatementAst] -and
    $node.Body.Extent.Text -match "Complete-ApprovalStep 'initial request'"
}, $true) | Select-Object -First 1
if (-not $scenarioTry) { throw 'Could not find the main scenario body.' }
$scenarioBody = ($scenarioTry.Body.Statements | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine

$script:marks = [System.Collections.Generic.List[string]]::new()
$script:approvalCalls = [System.Collections.Generic.List[string]]::new()
$script:approvalResult = $true
$script:responseResult = $true
function Mark([string]$Text) { $script:marks.Add($Text) }
function Approve-Pending([string]$Button, [int]$TimeoutSec = 20) {
    $script:approvalCalls.Add($Button)
    $script:approvalResult
}
function Wait-BotDone([int]$Count) { $script:responseResult }
function Start-Sleep { }
function Invoke-Cab([string]$Id) { }
function Send-MattermostMessage($ws, [string]$Text, [int]$CharMs = 55, [string]$Box = '#post_textbox') { }
function Select-CabFirstItem([string]$Id, [int]$TimeoutSec) { }
function Move-Cursor([double]$X, [double]$Y, [int]$Ms = 700) { }
function Open-LatestThread($ws) { 'thread opened' }
function Get-BotDoneCount { 1 }
function Move-ToSelector($ws, [string]$Selector) { }

foreach ($helper in $helpers) { Invoke-Expression $helper.Extent.Text }

$scenarioCases = @(
    @{ Scenario = 'project'; Auto = $false; ExpectedButtons = @('実行', '変更', '実行') },
    @{ Scenario = 'agent'; Auto = $false; ExpectedButtons = @('実行', '変更', '実行') },
    @{ Scenario = 'project'; Auto = $true; ExpectedButtons = @() },
    @{ Scenario = 'agent'; Auto = $true; ExpectedButtons = @('実行') }
)
foreach ($case in $scenarioCases) {
    $script:marks.Clear()
    $script:approvalCalls.Clear()
    $script:approvalResult = $true
    $Scenario = $case.Scenario
    $AutoSwitch = [bool]$case.Auto
    $ws = $null
    $steps = @{
        Request = 'request'; Switch = 'switch'; FollowUp = 'follow-up'
    }
    Invoke-Expression $scenarioBody
    $actualButtons = @($script:approvalCalls.ToArray())
    if (($case.ExpectedButtons -join '|') -ne ($actualButtons -join '|')) {
        throw "Unexpected approvals for $($case.Scenario), AutoSwitch=$($case.Auto): $($actualButtons -join ', ')"
    }
    foreach ($step in 'initial request', 'switch') {
        $expectedMark = if ($case.Auto) { "$step approval: automatic" } else { "$step approved" }
        if ($expectedMark -notin $script:marks) { throw "Missing approval result for $step in $($case.Scenario)." }
    }
    $followUpRequiresApproval = -not $case.Auto -or $case.Scenario -eq 'agent'
    $followUpMark = if ($followUpRequiresApproval) { 'follow-up request approved' } else { 'follow-up request approval: automatic' }
    if ($followUpMark -notin $script:marks) { throw "Missing follow-up approval result for $($case.Scenario), AutoSwitch=$($case.Auto)." }
}

$script:marks.Clear()
$script:approvalCalls.Clear()
$script:approvalResult = $true
Complete-ApprovalStep 'manual' 'Run'
if ($script:approvalCalls.Count -ne 1 -or $script:marks[-1] -ne 'manual approved') {
    throw 'Manual mode should request and record approval.'
}

$script:approvalCalls.Clear()
$script:approvalResult = $false
$failed = $false
try { Complete-ApprovalStep 'missing approval' 'Run' }
catch { $failed = $_.Exception.Message -like 'Timed out waiting for approval:*' }
if (-not $failed) { throw 'A missing required approval should fail the scenario.' }

$script:responseResult = $true
Require-BotResponse 'received reply' 1
if ($script:marks[-1] -ne 'received reply reply received') {
    throw 'A received response should be recorded.'
}

$script:responseResult = $false
$failed = $false
try { Require-BotResponse 'missing reply' 1 }
catch { $failed = $_.Exception.Message -like 'Timed out waiting for Mattermost response:*' }
if (-not $failed) { throw 'A missing required response should fail the scenario.' }

'Offline demo scenario helper checks passed.'
