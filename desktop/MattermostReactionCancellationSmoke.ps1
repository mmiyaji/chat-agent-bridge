param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $artifactRoot ('reaction-cancel-smoke-' + $runId)
$data = Join-Path $runRoot 'data'
$workspace = Join-Path $data 'workspace'
$agentRoot = Join-Path $runRoot 'agent'
$gate = Join-Path $data '.cab-live-output-release'
$marker = Join-Path $data '.cab-live-output-fixture'
$pidFile = Join-Path $runRoot 'fixture-pids.txt'
$checks = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$fixture = $null
$app = $null
$endpoint = $null

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Save-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($app -and $app.HasExited) { throw "The isolated application exited while waiting for $Description (exit $($app.ExitCode))." }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out after $Seconds seconds waiting for $Description."
}
function Read-Results { Invoke-RestMethod -Uri "$script:endpoint/test/results" -TimeoutSec 3 }
function Read-History {
    $path = Join-Path $script:data 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) }
    catch { return @() }
}
function Send-Request([string]$Id, [string]$Thread, [string]$Text) {
    $json = @{ id = $Id; thread = $Thread; user = 'alice'; text = $Text; mention = $false; plain = $false } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 3
    Assert ($response.delivered -eq 1) 'The request did not reach exactly one connected Mattermost transport.'
}
function Send-Reaction([string]$User, [string]$PostId, [string]$Emoji = 'octagonal_sign') {
    $json = @{ channel_id = 'ui-channel'; user = $User; post_id = $PostId; emoji_name = $Emoji } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$script:endpoint/test/reaction" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 3
    Assert ($response.delivered -eq 1) 'The reaction did not reach exactly one connected Mattermost transport.'
}
function Running-Record([string]$Thread) {
    @(Read-History | Where-Object { $_.ThreadId -eq $Thread -and $_.Status -eq 'running' -and $_.StartedAt })
}
function Find-ProgressPost([string]$Thread) {
    $snapshot = Read-Results
    $running = @($snapshot.updates | Where-Object { $_.root_id -eq $Thread -and $_.message.Contains('🛑') } | Select-Object -Last 1)
    if ($running.Count -eq 0) { return @() }
    @($snapshot.replies | Where-Object { $_.id -eq $running[0].id -and $_.root_id -eq $Thread -and $_.message.Contains('🛑') })
}
function Check([string]$Name, [scriptblock]$Body) {
    & $Body
    $checks.Add(@{ name = $Name; passed = $true })
    Write-Output "PASS $Name"
}

try {
    $appRoot = [IO.Path]::GetFullPath($AppDirectory)
    $testAgent = [IO.Path]::GetFullPath($TestAgentPath)
    Assert ([IO.Path]::IsPathFullyQualified($AppDirectory)) 'AppDirectory must be an absolute path.'
    Assert ([IO.Path]::IsPathFullyQualified($TestAgentPath)) 'TestAgentPath must be an absolute path.'
    Assert (Test-Path -LiteralPath $testAgent -PathType Leaf) 'The offline TestAgent executable was not found.'
    Assert ($appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be below desktop/artifacts.'
    Assert (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf) 'Portable ChatAgentBridge.Desktop.exe was not found.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) 'Expected an unpackaged portable application directory.'

    New-Item -ItemType Directory -Path $workspace, $agentRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $data '.cab-mattermost-reaction-cancel-smoke') -Value 'isolated offline smoke' -Encoding utf8
    Set-Content -LiteralPath $marker -Value 'Isolated offline live-output UI test' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $workspace '.cab-reaction-cancel-smoke') -Value 'isolated offline workspace' -Encoding utf8
    $sourceDirectory = Split-Path -Parent $testAgent
    Get-ChildItem -LiteralPath $sourceDirectory -File | Where-Object { $_.FullName -ne $testAgent } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $agentRoot $_.Name) -Force
    }
    $agent = Join-Path $agentRoot 'claude.exe'
    Copy-Item -LiteralPath $testAgent -Destination $agent -Force

    Add-Type -AssemblyName System.Security
    $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $portFile = Join-Path $runRoot 'fixture-port.txt'
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixtureScript = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $fixture = Start-Process -FilePath $node -ArgumentList @('"' + $fixtureScript + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath $portFile } 'the loopback Mattermost fixture'
    $port = (Get-Content -LiteralPath $portFile -Raw).Trim()
    Assert ($port -match '^\d+$') 'The fixture returned an invalid loopback port.'
    $script:endpoint = "http://127.0.0.1:$port"

    Save-Json (Join-Path $data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'claude'; AgentMode = 'exec'
        AgentExecutable = $agent; AgentArguments = '--print --output-format stream-json --verbose'; UseAppServer = $false
        AgentPermissionMode = 'inherit'; TrustedWorkspaceRoots = $workspace; RequireApproval = $false
        MattermostServerUrl = $endpoint; MattermostChannelId = 'ui-channel'; MattermostToken = $token
        MattermostAllowedUserIds = 'alice,bob'; ChatApprovalMode = 'auto'; ChatResultMode = 'auto'
        AutoConnectProvider = 'mattermost'
    }
    Save-Json (Join-Path $data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policy = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not determine the current consent policy version.'
    Save-Json (Join-Path $data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $data 'language.json') @{ Language = 'en' }
    Save-Json (Join-Path $data 'appearance.json') @{ Theme = 'light' }
    Save-Json (Join-Path $data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false; LastAttemptUtc = $null; AvailableRelease = $null }
    Save-Json (Join-Path $data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Save-Json (Join-Path $data 'history.json') @{ SchemaVersion = 1; Records = @() }
    Save-Json (Join-Path $data 'audit.json') @{ SchemaVersion = 1; Records = @() }

    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
    $start.Environment['CAB_LIVE_OUTPUT_FIXTURE'] = '1'
    $start.Environment['CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS'] = '100'
    $start.Environment['CAB_LIVE_OUTPUT_FIXTURE_PID_FILE'] = $pidFile
    $app = [Diagnostics.Process]::Start($start)
    Wait-For { try { (Read-Results).connections -eq 1 } catch { $false } } 'Mattermost auto-connect'

    $thread = 'reaction-cancel-' + $runId
    $firstId = 'request-first-' + $runId
    $secondId = 'request-queued-' + $runId
    $thirdId = 'request-reuse-' + $runId
    $firstPrompt = 'LIVE_RUN_CHAT_A reaction cancellation first run'
    $secondPrompt = 'LIVE_RUN_CHAT_B queued work survives reaction cancellation'
    $thirdPrompt = 'LIVE_RUN_CHAT_A same thread after completed request'
    Remove-Item -LiteralPath $gate -Force -ErrorAction SilentlyContinue
    Send-Request $firstId $thread $firstPrompt
    Wait-For { @(Running-Record $thread).Count -eq 1 } 'first request running history'
    Wait-For { Test-Path -LiteralPath $pidFile } 'the first offline CLI process'
    $firstFixturePid = [int](Get-Content -LiteralPath $pidFile | Select-Object -First 1)
    Wait-For { (Read-Results).updates | Where-Object { $_.root_id -eq $thread -and $_.message.Contains('🛑') } } 'first request running progress with cancellation hint'
    $firstProgress = Find-ProgressPost $thread
    Assert ($firstProgress.Count -eq 1) 'Could not identify the first request progress post.'
    $firstProgressId = [string]$firstProgress[0].id
    Assert (-not [string]::IsNullOrWhiteSpace($firstProgressId)) 'The first progress post has no id.'
    Check 'Bot seeds a clickable stop reaction and its own WebSocket echo is ignored' {
        $seeded = @((Read-Results).reactions | Where-Object { $_.post_id -eq $firstProgressId -and $_.user_id -eq 'ui-bridge' -and $_.emoji_name -eq 'octagonal_sign' })
        Assert ($seeded.Count -eq 1) 'The running progress post has no Bot stop reaction.'
        Assert (@(Running-Record $thread).Count -eq 1) 'The Bot reaction echo cancelled its own request.'
    }

    Send-Request $secondId $thread $secondPrompt
    Wait-For { @((Read-Results).replies | Where-Object { $_.root_id -eq $thread }).Count -ge 2 } 'queued request receipt progress'
    Assert (@(Read-History | Where-Object { $_.ThreadId -eq $thread -and $_.Status -eq 'running' }).Count -eq 1) 'The second request started before the first request was cancelled.'
    Send-Request ('request-pending-cancel-' + $runId) $thread 'LIVE_RUN_TWO pending request to cancel'
    Wait-For { @((Read-Results).replies | Where-Object { $_.root_id -eq $thread }).Count -ge 3 } 'additional pending request receipt'
    $pendingProgressId = [string](@((Read-Results).replies | Where-Object { $_.root_id -eq $thread } | Select-Object -Last 1)[0].id)
    Check 'Cancelling a pending request leaves the active request running' {
        Send-Reaction 'alice' $pendingProgressId
        Wait-For { (Read-Results).updates | Where-Object { $_.id -eq $pendingProgressId -and $_.message.Contains('Execution stopped') } } 'pending request cancellation'
        Assert (@(Running-Record $thread).Count -eq 1) 'Cancelling a pending request stopped the active request.'
    }
    Check 'Only the owner reaction on the original active progress can cancel it' {
        Send-Reaction 'bob' $firstProgressId
        Send-Reaction 'outsider' $firstProgressId
        Send-Reaction 'alice' ('wrong-' + $firstProgressId)
        Send-Reaction 'alice' $firstProgressId 'heart'
        Start-Sleep -Milliseconds 500
        Assert (@(Running-Record $thread).Count -eq 1) 'A non-owner, unauthorized, wrong-post, or wrong-emoji reaction stopped the active request.'
        Send-Reaction 'alice' $firstProgressId
        Wait-For { @(Read-History | Where-Object { $_.ThreadId -eq $thread -and $_.Status -eq 'cancelled' }).Count -eq 1 } 'original request cancellation history'
        Assert (-not (Get-Process -Id $firstFixturePid -ErrorAction SilentlyContinue)) 'The cancelled offline CLI process is still running.'
        Wait-For { (Read-Results).updates | Where-Object { $_.id -eq $firstProgressId -and -not $_.message.Contains('🛑') -and $_.message.Contains('Execution stopped') } } 'terminal progress without the cancellation hint'
        Wait-For { @(Running-Record $thread).Count -eq 1 } 'queued request starting after cancellation'
        Wait-For { (Read-Results).updates | Where-Object { $_.root_id -eq $thread -and $_.id -ne $firstProgressId -and $_.message.Contains('🛑') } } 'queued request running progress'
        $script:secondProgressId = [string](Find-ProgressPost $thread)[0].id
        Assert ((Test-Path -LiteralPath $gate) -eq $false) 'The cancelled first request unexpectedly released the fake CLI completion gate.'
        Set-Content -LiteralPath $gate -Value 'release queued request' -Encoding utf8
        Wait-For { @(Read-History | Where-Object { $_.ThreadId -eq $thread -and $_.Status -eq 'completed' }).Count -eq 1 } 'queued request completion'
        Wait-For { (Read-Results).replies | Where-Object { $_.root_id -eq $thread -and $_.message -like '*LIVE_FIXTURE_FINAL fixed final response*' } } 'queued request final reply'
    }

    Remove-Item -LiteralPath $gate -Force -ErrorAction SilentlyContinue
    Send-Request $thirdId $thread $thirdPrompt
    Wait-For { @(Running-Record $thread).Count -eq 1 } 'new request in the reused thread'
    Wait-For { (Read-Results).updates | Where-Object { $_.root_id -eq $thread -and $_.message.Contains('🛑') } } 'new request running progress with cancellation hint'
    Check 'A reaction on the completed request progress does not cancel a later same-thread request' {
        Send-Reaction 'alice' $firstProgressId
        Send-Reaction 'alice' $secondProgressId
        Start-Sleep -Milliseconds 500
        Assert (@(Running-Record $thread).Count -eq 1) 'A stale reaction cancelled the later request in the same thread.'
        Set-Content -LiteralPath $gate -Value 'release final request' -Encoding utf8
        Wait-For { @(Read-History | Where-Object { $_.ThreadId -eq $thread -and $_.Status -eq 'completed' }).Count -eq 2 } 'final same-thread request completion'
    }

    Wait-For { @((Read-Results).replies | Where-Object { $_.root_id -eq $thread -and $_.message -like '*LIVE_FIXTURE_FINAL fixed final response*' }).Count -eq 2 } 'all final replies'
    $final = Read-Results
    Check 'Terminal requests remove only the Bot stop reaction and preserve member reactions' {
        Assert (@($final.reactions | Where-Object { $_.user_id -eq 'ui-bridge' -and $_.emoji_name -eq 'octagonal_sign' }).Count -eq 0) 'A terminal request retained its Bot stop marker.'
        Assert (@($final.reactions | Where-Object { $_.post_id -eq $firstProgressId -and $_.user_id -eq 'alice' -and $_.emoji_name -eq 'octagonal_sign' }).Count -eq 1) 'Cleanup removed the requester reaction.'
        Assert (@($final.reactions | Where-Object { $_.post_id -eq $firstProgressId -and $_.user_id -eq 'bob' }).Count -gt 0) 'Cleanup removed another member reaction.'
        Assert (@($final.reactionAdds).Count -eq 4) 'Expected one seeded stop marker for each of the four requests.'
        Assert (@($final.reactionRemoves).Count -eq 4) 'Expected each request to remove its Bot marker once.'
    }
    Save-Json (Join-Path $runRoot 'chat-results.json') $final
    $threadReplies = @($final.replies | Where-Object { $_.root_id -eq $thread })
    Assert (@($threadReplies | Where-Object { $_.message -like '*LIVE_FIXTURE_FINAL fixed final response*' }).Count -eq 2) 'Expected final replies only for the queued and later completed requests.'
    Assert (@(Read-History | Where-Object { $_.ThreadId -eq $thread -and $_.Status -eq 'cancelled' }).Count -eq 1) 'Expected exactly one cancelled execution record.'
    $checks.Add(@{ name = 'Queued and later same-thread work completed with isolated final replies'; passed = $true })
}
catch {
    $checks.Add(@{ name = 'Mattermost reaction cancellation smoke'; passed = $false; detail = $_.Exception.Message })
    throw
}
finally {
    $checks | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $pidFile) {
        foreach ($line in Get-Content -LiteralPath $pidFile) {
            $childId = 0
            if (-not [int]::TryParse($line, [ref]$childId)) { continue }
            $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
            if ($child -and $child.ProcessName -eq 'claude') {
                try {
                    if ([IO.Path]::GetFullPath($child.Path) -eq [IO.Path]::GetFullPath($agent)) { Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue }
                } catch { }
            }
        }
    }
    Write-Output "Artifacts: $runRoot"
}
