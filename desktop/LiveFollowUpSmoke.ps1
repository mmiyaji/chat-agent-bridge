param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$checks = [Collections.Generic.List[object]]::new()
$script:app = $null
$script:fixture = $null
$script:endpoint = $null
$script:data = $null
$script:runRoot = $null
$script:runRoots = [Collections.Generic.List[string]]::new()
$script:gate = $null
$script:observations = $null

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Save-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($script:app -and $script:app.HasExited) { throw "The isolated application exited while waiting for $Description (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out after $Seconds seconds waiting for $Description."
}
function Read-Results { Invoke-RestMethod -Uri "$script:endpoint/test/results" -TimeoutSec 3 }
function Read-History {
    $path = Join-Path $script:data 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}
function Read-Observations {
    if (-not (Test-Path -LiteralPath $script:observations)) { return @() }
    $records = [Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $script:observations) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $records.Add(($line | ConvertFrom-Json))
    }
    return @($records)
}
function Send-Request([string]$Id, [string]$Thread, [string]$User, [string]$Text) {
    $json = @{ id = $Id; thread = $Thread; user = $User; text = $Text; mention = $false; plain = $false } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 3
    Assert ($response.delivered -eq 1) 'The request did not reach exactly one loopback Mattermost transport.'
}
function Running-Records { @(Read-History | Where-Object { $_.Status -eq 'running' -and $_.StartedAt }) }
function Completed-Records { @(Read-History | Where-Object { $_.Status -eq 'completed' }) }
function Check([string]$Name, [scriptblock]$Body) {
    & $Body
    $checks.Add(@{ name = $Name; passed = $true })
    Write-Output "PASS $Name"
}
function Start-IsolatedApp([string]$FollowUpMode) {
    $id = [guid]::NewGuid().ToString('D')
    $script:runRoot = Join-Path $artifactRoot $id
    $script:runRoots.Add($script:runRoot)
    $script:data = Join-Path $script:runRoot 'data'
    $workspace = Join-Path $script:data 'workspace'
    $agentRoot = Join-Path $script:runRoot 'agent'
    $script:gate = Join-Path $script:data '.cab-live-steering-release'
    $script:observations = Join-Path $script:data 'protocol-observations.jsonl'
    $marker = Join-Path $script:data '.cab-live-steering-fixture'
    New-Item -ItemType Directory -Path $workspace, $agentRoot -Force | Out-Null
    Set-Content -LiteralPath $marker -Value 'Isolated offline AppServer follow-up fixture' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $workspace '.cab-live-steering-workspace') -Value 'offline smoke workspace' -Encoding utf8
    Set-Content -LiteralPath $script:observations -Value '' -Encoding utf8

    $sourceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($TestAgentPath))
    Get-ChildItem -LiteralPath $sourceDirectory -File | Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($TestAgentPath) } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $agentRoot $_.Name) -Force
    }
    $agent = Join-Path $agentRoot 'codex.exe'
    Copy-Item -LiteralPath $TestAgentPath -Destination $agent -Force

    Add-Type -AssemblyName System.Security
    $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $portFile = Join-Path $script:runRoot 'fixture-port.txt'
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixtureScript = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $script:fixture = Start-Process -FilePath $node -ArgumentList @('"' + $fixtureScript + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath $portFile } 'loopback Mattermost server'
    $port = (Get-Content -LiteralPath $portFile -Raw).Trim()
    Assert ($port -match '^\d+$') 'The loopback fixture returned an invalid port.'
    $script:endpoint = "http://127.0.0.1:$port"

    Save-Json (Join-Path $script:data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'codex'; AgentMode = 'app-server'
        AgentExecutable = $agent; AgentArguments = ''; AgentModel = $null; AgentReasoningEffort = $null
        AgentPermissionMode = 'inherit'; TrustedWorkspaceRoots = $workspace; RequireApproval = $false; AllowAgentNetwork = $false
        MattermostServerUrl = $script:endpoint; MattermostChannelId = 'ui-channel'; MattermostToken = $token
        MattermostAllowedUserIds = 'alice,bob'; MattermostAllowedThreadIds = ''; ChatApprovalMode = 'auto'; ChatResultMode = 'auto'
        ChatFollowUpMode = $FollowUpMode; AutoConnectProvider = 'mattermost'; PostConnectionNotice = $false
    }
    Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policy = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not determine the current consent policy version.'
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = 'en' }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = 'light' }
    Save-Json (Join-Path $script:data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false; LastAttemptUtc = $null; AvailableRelease = $null }
    Save-Json (Join-Path $script:data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Save-Json (Join-Path $script:data 'history.json') @{ SchemaVersion = 1; Records = @() }
    Save-Json (Join-Path $script:data 'audit.json') @{ SchemaVersion = 1; Records = @() }

    $appRoot = [IO.Path]::GetFullPath($AppDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($script:data)
    $start.Environment['CAB_LIVE_STEERING_FIXTURE'] = '1'
    $start.Environment['CAB_LIVE_STEERING_RELEASE_FILE'] = $script:gate
    $start.Environment['CAB_LIVE_STEERING_OBSERVATIONS_FILE'] = $script:observations
    $script:app = [Diagnostics.Process]::Start($start)
    Wait-For { try { (Read-Results).connections -eq 1 } catch { $false } } 'Mattermost auto-connect'
    return @{ workspace = $workspace; agent = $agent; runRoot = $script:runRoot }
}
function Stop-IsolatedApp {
    if ($script:app -and -not $script:app.HasExited) {
        try { $script:app.Kill($true); $script:app.WaitForExit(5000) | Out-Null } catch { }
    }
    if ($script:fixture -and -not $script:fixture.HasExited) {
        try { $script:fixture.Kill(); $script:fixture.WaitForExit(3000) | Out-Null } catch { }
    }
    $script:app = $null
    $script:fixture = $null
}
function Assert-FinalReplies([int]$Count) {
    Wait-For { @((Read-Results).replies | Where-Object { $_.message -like '*LIVE_STEER_FINAL single fixture response*' }).Count -ge $Count } 'fixture final replies'
    $final = @(Read-Results).replies | Where-Object { $_.message -like '*LIVE_STEER_FINAL single fixture response*' }
    Assert ($final.Count -eq $Count) "Expected exactly $Count final fixture replies; found $($final.Count)."
}
function Assert-ProgressText([string]$ResourceKey) {
    $resourcePath = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Localization/DesktopStrings.resx'
    [xml]$resource = Get-Content -LiteralPath $resourcePath -Raw
    $text = [string]($resource.root.data | Where-Object { $_.name -eq $ResourceKey } | Select-Object -First 1).value
    Assert (-not [string]::IsNullOrWhiteSpace($text)) "Could not resolve localized progress resource $ResourceKey."
    Wait-For { ((Read-Results).updates | Where-Object { $_.message.Contains($text) }).Count -gt 0 } "localized $ResourceKey progress text"
}

try {
    $appRoot = [IO.Path]::GetFullPath($AppDirectory)
    Assert ([IO.Path]::IsPathFullyQualified($AppDirectory)) 'AppDirectory must be an absolute path.'
    Assert ([IO.Path]::IsPathFullyQualified($TestAgentPath)) 'TestAgentPath must be an absolute path.'
    Assert (Test-Path -LiteralPath $TestAgentPath -PathType Leaf) 'The offline TestAgent executable was not found.'
    Assert ($appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be below desktop/artifacts.'
    Assert (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf) 'Portable ChatAgentBridge.Desktop.exe was not found.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) 'Expected an unpackaged portable application directory.'

    Start-IsolatedApp 'queue' | Out-Null
    $thread = 'follow-queue-' + [guid]::NewGuid().ToString('N')
    Send-Request ('queue-1-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_QUEUE_1 first queued turn'
    Wait-For { @(Running-Records).Count -eq 1 } 'first queued turn'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 1 } 'first queued turn RPC'
    Send-Request ('queue-2-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_QUEUE_2 queued follow-up'
    Wait-For { @((Read-Results).replies | Where-Object { $_.root_id -eq $thread }).Count -ge 2 } 'queued follow-up receipt'
    Start-Sleep -Milliseconds 600
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 1) 'Queued follow-up started before the held turn finished.'
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/steer' }).Count -eq 0) 'Queue mode sent turn/steer.'
    Set-Content -LiteralPath $script:gate -Value 'release queued turns' -Encoding utf8
    Wait-For { (Completed-Records).Count -eq 2 } 'both queued turns to complete'
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 2) 'Queue mode did not start exactly two turns.'
    Assert-FinalReplies 2
    $checks.Add(@{ name = 'Queue mode serializes same-user same-thread follow-ups'; passed = $true })
    Write-Output 'PASS Queue mode serializes same-user same-thread follow-ups'
    Stop-IsolatedApp

    Start-IsolatedApp 'steer' | Out-Null
    $thread = 'follow-steer-' + [guid]::NewGuid().ToString('N')
    $otherThread = 'follow-other-' + [guid]::NewGuid().ToString('N')
    Send-Request ('steer-1-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_STEER_1 active turn'
    Wait-For { @(Running-Records).Count -eq 1 } 'active steer turn'
    Send-Request ('steer-2-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_STEER_2 matching owner follow-up'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/steer' -and $_.outcome -eq 'accepted' }).Count -eq 1 } 'accepted steer RPC'
    Send-Request ('other-user-' + [guid]::NewGuid().ToString('N')) $thread 'bob' 'LIVE_STEER_OTHER_USER_1 different owner'
    Send-Request ('other-thread-' + [guid]::NewGuid().ToString('N')) $otherThread 'alice' 'LIVE_STEER_OTHER_THREAD_1 different thread'
    Wait-For { @((Read-Results).replies).Count -ge 4 } 'receipts for the active request and three follow-ups'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 1 } 'single held turn before queued follow-ups start'
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/steer' }).Count -eq 1) 'An unrelated user or thread was sent to the active turn.'
    Set-Content -LiteralPath $script:gate -Value 'release steered and independent turns' -Encoding utf8
    Wait-For { (Completed-Records).Count -eq 3 } 'steered request and independent requests to complete'
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 3) 'Expected the two unmatched follow-ups to run after the active turn.'
    Assert-FinalReplies 3
    Assert-ProgressText 'ProgressFollowUpApplied'
    $checks.Add(@{ name = 'Steering matches user and thread and publishes one final reply per execution'; passed = $true })
    Write-Output 'PASS Steering matches user and thread and publishes one final reply per execution'
    Stop-IsolatedApp

    Start-IsolatedApp 'steer' | Out-Null
    $thread = 'follow-unsupported-' + [guid]::NewGuid().ToString('N')
    Send-Request ('unsupported-1-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_UNSUPPORTED_1 active turn'
    Wait-For { @(Running-Records).Count -eq 1 } 'unsupported steer active turn'
    Send-Request ('unsupported-2-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_QUEUE_2 queued after rejection'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/steer' -and $_.outcome -eq 'unsupported' }).Count -eq 1 } 'explicit unsupported steer response'
    Set-Content -LiteralPath $script:gate -Value 'release unsupported queue fallback' -Encoding utf8
    Wait-For { (Completed-Records).Count -eq 2 } 'unsupported follow-up queued execution'
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 2) 'Explicit steering rejection did not fall back to a queued second turn.'
    Assert-FinalReplies 2
    $checks.Add(@{ name = 'Explicit steering rejection falls back to queue'; passed = $true })
    Write-Output 'PASS Explicit steering rejection falls back to queue'
    Stop-IsolatedApp

    Start-IsolatedApp 'steer' | Out-Null
    $thread = 'follow-unknown-' + [guid]::NewGuid().ToString('N')
    Send-Request ('unknown-1-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_UNKNOWN_1 active turn'
    Wait-For { @(Running-Records).Count -eq 1 } 'unknown-delivery active turn'
    Send-Request ('unknown-2-' + [guid]::NewGuid().ToString('N')) $thread 'alice' 'LIVE_STEER_STEER_2 uncertain delivery follow-up'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/steer' -and $_.outcome -eq 'delivery-unknown' }).Count -eq 1 } 'unanswered steer RPC'
    Start-Sleep -Seconds 7
    Assert (@(Read-Observations | Where-Object { $_.method -eq 'turn/start' }).Count -eq 1) 'Unknown steering delivery triggered an automatic retry or second turn.'
    Assert (@(Running-Records).Count -eq 1) 'Unknown delivery did not leave the active execution held for resolution.'
    Set-Content -LiteralPath $script:gate -Value 'release unknown delivery active turn' -Encoding utf8
    Wait-For { (Completed-Records).Count -eq 1 } 'uncertain delivery execution to finish'
    Assert-FinalReplies 1
    Assert-ProgressText 'ProgressFollowUpUncertain'
    $checks.Add(@{ name = 'Unknown steering delivery is held without an automatic retry'; passed = $true })
    Write-Output 'PASS Unknown steering delivery is held without an automatic retry'
    Stop-IsolatedApp

    Write-Output "Protocol observations: $script:observations"
}
catch {
    $checks.Add(@{ name = 'Live follow-up smoke'; passed = $false; detail = $_.Exception.Message })
    throw
}
finally {
    Stop-IsolatedApp
    foreach ($root in $script:runRoots) {
        $checks | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
        Write-Output "Artifacts: $root"
    }
}
