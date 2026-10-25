# Offline tests for the scenario approval/response helpers. This parses the runner,
# but never dot-sources it or loads its app, CDP, or desktop automation helpers.
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

$script:marks = [System.Collections.Generic.List[string]]::new()
$script:approvalCalls = 0
$script:approvalResult = $true
$script:responseResult = $true
function Mark([string]$Text) { $script:marks.Add($Text) }
function Approve-Pending([string]$Button, [int]$TimeoutSec = 20) {
    $script:approvalCalls++
    $script:approvalResult
}
function Wait-BotDone([int]$Count) { $script:responseResult }

foreach ($helper in $helpers) { Invoke-Expression $helper.Extent.Text }

Complete-ApprovalStep 'automatic' 'Run' -Automatic
if ($script:approvalCalls -ne 0 -or $script:marks[-1] -ne 'automatic approval: automatic') {
    throw 'Automatic mode should skip approval lookup.'
}

Complete-ApprovalStep 'manual' 'Run'
if ($script:approvalCalls -ne 1 -or $script:marks[-1] -ne 'manual approved') {
    throw 'Manual mode should request and record approval.'
}

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
