param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
function Invoke-UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $($Arguments[0])" }
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $results.Add(@{ name = $Name; passed = $true }) }
    catch { $results.Add(@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}

Check 'Navigation and initialized settings' {
    Invoke-UI @('wait-for', 'NavOverview', '-t', '5000')
    Invoke-UI @('invoke', 'NavWorkbench')
    Invoke-UI @('wait-for', 'WorkspaceRoot', '-t', '3000')
    Invoke-UI @('wait-for', 'RunAgent', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
}
Check 'Save isolated settings' {
    Invoke-UI @('set-value', 'WorkspaceRoot', $DataDirectory)
    Invoke-UI @('set-value', 'AgentExecutable', 'powershell.exe')
    Invoke-UI @('set-value', 'AgentArguments', '-NoProfile -NonInteractive -Command "[Console]::Write([Console]::In.ReadToEnd())"')
    Invoke-UI @('invoke', 'SaveSettings')
    Invoke-UI @('wait-for', 'Title', '--value', '設定を保存しました', '--contains', '-t', '3000')
    $saved = Get-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Raw | ConvertFrom-Json
    if ($saved.WorkspaceRoot -ne $DataDirectory) { throw 'Workspace was not persisted.' }
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
}
Check 'Connections and chat navigation' {
    Invoke-UI @('invoke', 'NavConnections')
    Invoke-UI @('wait-for', 'MattermostServerUrl', '-t', '3000')
    Invoke-UI @('invoke', 'NavChat')
    Invoke-UI @('wait-for', 'ConnectMattermost', '-t', '3000')
    Invoke-UI @('wait-for', 'ApproveChat', '-p', 'IsEnabled', '--value', 'False', '-t', '3000')
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'smoke-results.json') -Encoding utf8
$results | ForEach-Object { Write-Output "$(if ($_.passed) {'PASS'} else {'FAIL'}): $($_.name) $($_.error)" }
if ($results.Where({ -not $_.passed }).Count -gt 0) { exit 1 }
