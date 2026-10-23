param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('en', 'ja')][string]$Language = 'en',
    [ValidateSet('light', 'dark')][string]$Theme = 'light'
)

$ErrorActionPreference = 'Stop'
$runRoot = Join-Path $PSScriptRoot ('artifacts/audit-failure-ui-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$workspace = Join-Path $runRoot 'workspace'
New-Item -ItemType Directory -Path $data, $workspace -Force | Out-Null
$app = $null
$fixture = $null
$auditLock = $null
$endpoint = $null
$checks = [Collections.Generic.List[string]]::new()

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Save-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($app -and $app.HasExited) { throw "Isolated app exited while waiting for $Description ($($app.ExitCode))." }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out waiting for $Description."
}
function Read-Results { Invoke-RestMethod -Uri "$endpoint/test/results" -TimeoutSec 3 }
function Send-Status([string]$Thread) {
    $body = @{ id = [guid]::NewGuid().ToString('N'); thread = $Thread; user = 'alice'; text = 'status' } | ConvertTo-Json -Compress
    $delivery = Invoke-RestMethod -Uri "$endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 3
    Assert ($delivery.delivered -eq 1) 'Expected exactly one isolated Mattermost receiver.'
    Wait-For { @((Read-Results).replies | Where-Object root_id -eq $Thread).Count -eq 1 } "status reply for $Thread"
}
function Invoke-Ui([string[]]$Command) {
    $output = & winapp ui @Command -a $app.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return ($output -join "`n" | ConvertFrom-Json)
}
function Screenshot([string]$Name) {
    Invoke-Ui @('screenshot', '-o', (Join-Path $runRoot "$Name.png"), '--focus') | Out-Null
}

try {
    # Every run has a new port file: never reuse a stale endpoint from a previous fixture.
    $portFile = Join-Path $runRoot 'fixture-port.txt'
    $fixture = Start-Process -FilePath (Get-Command node -ErrorAction Stop).Source -WindowStyle Hidden -PassThru -ArgumentList @(
        ('"{0}"' -f (Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs')), ('"{0}"' -f $portFile))
    Wait-For { Test-Path -LiteralPath $portFile } 'loopback fixture'
    $port = (Get-Content -LiteralPath $portFile -Raw).Trim()
    Assert ($port -match '^\d+$') 'Invalid fixture port.'
    $endpoint = "http://127.0.0.1:$port"
    Add-Type -AssemblyName System.Security
    $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    Save-Json (Join-Path $data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'codex'; AgentMode = 'app-server'
        # No agent will run; a missing executable also prevents accidental real CLI fallback.
        AgentExecutable = (Join-Path $workspace 'unused-offline-agent.exe'); AgentArguments = ''
        AgentPermissionMode = 'inherit'; TrustedWorkspaceRoots = $workspace; RequireApproval = $true; AllowAgentNetwork = $false
        MattermostServerUrl = $endpoint; MattermostChannelId = 'ui-channel'; MattermostToken = $token
        MattermostAllowedUserIds = 'alice'; MattermostAllowedThreadIds = ''; ChatApprovalMode = 'always'
        ChatResultMode = 'manual'; ChatFollowUpMode = 'queue'; AutoConnectProvider = 'mattermost'; PostConnectionNotice = $false
    }
    Save-Json (Join-Path $data 'agent-connections.json') @{ SchemaVersion = 1; Profiles = @() }
    Save-Json (Join-Path $data 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Save-Json (Join-Path $data 'language.json') @{ Language = $Language }
    Save-Json (Join-Path $data 'appearance.json') @{ Theme = $Theme }
    Save-Json (Join-Path $data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false }
    Save-Json (Join-Path $data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Save-Json (Join-Path $data 'history.json') @{ SchemaVersion = 1; Records = @() }
    $auditPath = Join-Path $data 'audit.json'
    Save-Json $auditPath @{ SchemaVersion = 1; Records = @() }

    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path ([IO.Path]::GetFullPath($AppDirectory)) 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $runRoot
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
    $app = [Diagnostics.Process]::Start($start)
    Save-Json (Join-Path $runRoot 'processes.json') @{ AppPid = $app.Id; FixturePid = $fixture.Id; DataDirectory = $data }
    Wait-For { try { (Read-Results).connections -eq 1 } catch { $false } } 'Mattermost connection'
    $checks.Add('isolated Mattermost connected')
    Invoke-Ui @('inspect', '--interactive') | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot 'initial-ui.json') -Encoding utf8
    Invoke-Ui @('wait-for', 'ChatStatus', '-t', '10000') | Out-Null
    $before = (Invoke-Ui @('get-value', 'ChatStatus')).text
    Screenshot 'before'

    $resourceName = if ($Language -eq 'ja') { 'DesktopStrings.ja.resx' } else { 'DesktopStrings.resx' }
    [xml]$resources = Get-Content -LiteralPath (Join-Path $PSScriptRoot "ChatAgentBridge.Desktop/Localization/$resourceName") -Raw
    $expected = [string]($resources.root.data | Where-Object name -eq 'InboxAuditSaveFailed' | Select-Object -First 1).value
    Assert (-not [string]::IsNullOrWhiteSpace($expected)) 'Missing failure warning resource.'
    Assert (-not ([string]$before).Contains($expected)) 'Warning was present before failure injection.'

    # Keep reads legal but deny replacement of only this isolated file. Unlike malformed
    # JSON, this exercises an actual save failure after a successful cached read.
    $beforeBytes = [IO.File]::ReadAllBytes($auditPath)
    $auditLock = [IO.File]::Open($auditPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Send-Status 'audit-locked'
    Invoke-Ui @('wait-for', 'ChatStatus', '--value', $expected, '--contains', '-t', '10000') | Out-Null
    $after = (Invoke-Ui @('get-value', 'ChatStatus')).text
    Assert ([Linq.Enumerable]::SequenceEqual[byte]($beforeBytes, [IO.File]::ReadAllBytes($auditPath))) 'Locked audit file unexpectedly changed.'
    $checks.Add('actual audit save denied; localized warning displayed in bound UI')
    Screenshot 'audit-save-failed'

    $auditLock.Dispose(); $auditLock = $null
    Send-Status 'audit-recovered'
    Wait-For { try { @((Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json).Records | Where-Object { $_.Kind -eq 'received' -and $_.ThreadId -eq 'audit-recovered' }).Count -eq 1 } catch { $false } } 'audit save after releasing lock'
    Assert (@((Get-Content -LiteralPath (Join-Path $data 'history.json') -Raw | ConvertFrom-Json).Records).Count -eq 0) 'An AI execution was unexpectedly recorded.'
    $checks.Add('receiver stayed connected; audit saves recovered; no AI execution')
    Invoke-Ui @('invoke', 'DisconnectChat') | Out-Null
    Wait-For { (Read-Results).connections -eq 0 } 'clean disconnect'
    $checks.Add('disconnect remained usable after failure')
    Save-Json (Join-Path $runRoot 'result.json') @{ Passed = $true; Language = $Language; Theme = $Theme; StatusBefore = $before; StatusAfter = $after; ExpectedWarning = $expected; Checks = @($checks); ArtifactRoot = $runRoot }
    Write-Output "PASS $Language/$Theme $runRoot"
} catch {
    if ($app -and -not $app.HasExited) {
        try { Invoke-Ui @('inspect', '--interactive') | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui.json') -Encoding utf8; Screenshot 'failure' } catch { }
    }
    Save-Json (Join-Path $runRoot 'result.json') @{ Passed = $false; Error = $_.Exception.Message; Checks = @($checks); ArtifactRoot = $runRoot }
    throw
} finally {
    if ($auditLock) { $auditLock.Dispose() }
    if ($app -and -not $app.HasExited) { $app.Kill($true); $app.WaitForExit(5000) | Out-Null }
    if ($fixture -and -not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit(3000) | Out-Null }
}
