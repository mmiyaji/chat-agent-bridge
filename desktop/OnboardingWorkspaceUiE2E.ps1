param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$resultsPath = Join-Path $artifactRoot 'onboarding-workspace-review/ui-results.json'
$results = [System.Collections.Generic.List[object]]::new()
$currentStep = 'preflight'
$currentSubstep = 'validate isolated first-install fixture'
$peer = $null

function Assert-TestDirectory {
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $dataPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'DataDirectory must be under desktop/artifacts.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-onboarding-workspace-test'))) { throw 'The isolated onboarding test marker is missing.' }
    if (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json')) { throw 'First-install UI test must start without settings.json.' }
    foreach ($file in @('fixture-settings.dpapi.json', 'peer.json', 'metadata.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dataPath $file))) { throw "Required isolated fixture file is missing: $file" }
    }
    $peerInfo = Get-Content -LiteralPath (Join-Path $dataPath 'peer.json') -Raw | ConvertFrom-Json
    if ($peerInfo.url -notmatch '^http://127\.0\.0\.1:\d+$' -or -not $peerInfo.pid) { throw 'The loopback notification peer metadata is invalid.' }
    if (-not (Get-Process -Id ([int]$peerInfo.pid) -ErrorAction SilentlyContinue)) { throw 'The isolated notification peer is not running.' }
    $script:peer = [string]$peerInfo.url
    $script:metadata = Get-Content -LiteralPath (Join-Path $dataPath 'metadata.json') -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$script:metadata.outsidePath) -or -not (Test-Path -LiteralPath ([string]$script:metadata.outsidePath) -PathType Container)) {
        throw 'Metadata must provide an existing outsidePath directory.'
    }
    $script:childPath = [IO.Path]::GetFullPath((Join-Path $dataPath 'work'))
    if (-not (Test-Path -LiteralPath $script:childPath -PathType Container)) { throw 'The trusted work child directory is missing.' }
}

function Invoke-UiProcess([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command winapp -ErrorAction Stop).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in (@('ui') + $Arguments + @('-a', "$AppPid"))) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(45000)) { $child.Kill(); throw "UI action timed out at $($Arguments[0])." }
        return @{ ExitCode=$child.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally { $child.Dispose() }
}

function Invoke-UI([string[]]$Arguments) {
    if ($Arguments[0] -eq 'invoke' -and $Arguments[1] -match '^Nav') {
        $selection = Invoke-UiProcess @('get-property', $Arguments[1], '-p', 'IsSelected', '--json')
        if ($selection.ExitCode -eq 0 -and ($selection.Output | ConvertFrom-Json).properties.IsSelected -eq 'True') { return }
    }
    $result = Invoke-UiProcess $Arguments
    if ($result.ExitCode -ne 0) { throw "UI action failed at $($Arguments[0]) on $($Arguments[1]). $($result.Error)" }
}

function Wait-UI([string]$AutomationId, [string]$Value = '', [int]$TimeoutMs = 30000) {
    $args = @('wait-for', $AutomationId, '-t', "$TimeoutMs")
    if ($Value.Length -gt 0) { $args += @('--value', $Value, '--contains') }
    Invoke-UI $args
}

function Wait-Enabled([string]$AutomationId, [int]$TimeoutMs = 10000) {
    Invoke-UI @('wait-for', $AutomationId, '-p', 'IsEnabled', '--value', 'True', '-t', "$TimeoutMs")
}

function Get-Property([string]$AutomationId, [string]$Property) {
    $result = Invoke-UiProcess @('get-property', $AutomationId, '-p', $Property, '--json')
    if ($result.ExitCode -ne 0) { return $null }
    try { return ($result.Output | ConvertFrom-Json).properties.$Property } catch { return $null }
}

function Get-Value([string]$AutomationId) {
    $result = Invoke-UiProcess @('get-value', $AutomationId, '--json')
    if ($result.ExitCode -ne 0) { return $null }
    try { return ($result.Output | ConvertFrom-Json).text } catch { return $null }
}

function Wait-Value([string]$AutomationId, [string]$Value, [int]$TimeoutSeconds = 15) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        if ([string](Get-Value $AutomationId) -like "*$Value*") { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Find-RequestListItemSelector($Node, [string]$Text) {
    if ($null -eq $Node) { return $null }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string] -and $Node -isnot [pscustomobject]) {
        foreach ($child in $Node) { $found = Find-RequestListItemSelector $child $Text; if ($found) { return $found } }
        return $null
    }
    if ($Node -is [pscustomobject]) {
        if ($Node.type -eq 'ListItem' -and $Node.selector -and (Node-ContainsText $Node $Text)) { return [string]$Node.selector }
        foreach ($property in $Node.PSObject.Properties) { $found = Find-RequestListItemSelector $property.Value $Text; if ($found) { return $found } }
    }
    return $null
}

function Node-ContainsText($Node, [string]$Text) {
    if ($null -eq $Node) { return $false }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string] -and $Node -isnot [pscustomobject]) {
        foreach ($child in $Node) { if (Node-ContainsText $child $Text) { return $true } }
        return $false
    }
    if ($Node -is [pscustomobject]) {
        foreach ($propertyName in @('name','value','text')) {
            $candidate = $Node.$propertyName
            if ($null -ne $candidate -and [string]::Equals([string]$candidate, $Text, [StringComparison]::Ordinal)) { return $true }
        }
        foreach ($property in $Node.PSObject.Properties) { if (Node-ContainsText $property.Value $Text) { return $true } }
    }
    return $false
}

function Wait-RequestVisible([string]$Text, [int]$TimeoutSeconds = 25) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        $tree = Invoke-UiProcess @('inspect','ChatRequests','--json','-d','8')
        if ($tree.ExitCode -eq 0) {
            try { $document = $tree.Output | ConvertFrom-Json; $selector = Find-RequestListItemSelector $document $Text; if ($selector) { return $selector } } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'The pending request did not appear in the UI tree.'
}

function Select-Request([string]$Text) {
    $selector = Wait-RequestVisible $Text
    Invoke-UI @('invoke', $selector)
}

function Get-HistoryRecords {
    $path = Join-Path $dataPath 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}

function Get-AuditRecords {
    $path = Join-Path $dataPath 'audit.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}

function Get-Sessions {
    $path = Join-Path $dataPath 'sessions.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-Replies { return @(Invoke-RestMethod -Method Get -Uri "$peer/test/replies" -TimeoutSec 5 -ErrorAction Stop) }

function Post-Request([string]$Text, [string]$User = 'test-member', [string]$Thread = '') {
    $body = @{ text=$Text; user=$User }
    if ($Thread) { $body.thread = $Thread }
    return Invoke-RestMethod -Method Post -Uri "$peer/test/post" -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 5 -ErrorAction Stop
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 25) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}

function Get-Workspaces($Document) { if ($null -eq $Document) { return @() }; return @($Document.Workspaces) }
function Add-Pass([string]$Name) { $results.Add([pscustomobject]@{ name=$Name; passed=$true }) }
function Add-Fail([string]$Name, [string]$Detail) { $results.Add([pscustomobject]@{ name=$Name; passed=$false; detail=$Detail }) }

try {
    $currentStep = 'preflight'
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'AppPid is not running.' }
    Assert-TestDirectory

    $fixtureHost = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'mattermost-ui-agent'))
    $expectedClaude = [IO.Path]::GetFullPath((Join-Path $fixtureHost 'claude.exe'))
    $marker = Join-Path $fixtureHost '.cab-offline-test-agent'
    $hostDll = Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.dll'
    $fixtureRuntimeConfig = Get-Content -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworkDependent = $null -eq $fixtureRuntimeConfig.runtimeOptions.includedFrameworks
    $builtDllRelativePath = if ($frameworkDependent) {
        'ChatAgentBridge.TestAgent/bin/Debug/net10.0/ChatAgentBridge.TestAgent.dll'
    } else {
        'ChatAgentBridge.TestAgent/bin/Debug/net10.0/win-x64/ChatAgentBridge.TestAgent.dll'
    }
    $builtDll = Join-Path $PSScriptRoot $builtDllRelativePath
    if (-not (Test-Path -LiteralPath $marker) -or -not (Test-Path -LiteralPath $expectedClaude) -or
        -not (Test-Path -LiteralPath $hostDll) -or -not (Test-Path -LiteralPath $builtDll) -or
        (Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash -or
        (Get-FileHash -LiteralPath $expectedClaude -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.exe') -Algorithm SHA256).Hash) {
        throw 'The marked offline Claude fixture executable failed its path or SHA256 checks.'
    }

    $fixtureFile = Join-Path $dataPath 'fixture-settings.dpapi.json'
    $fixture = Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string]$fixture.AgentExecutable) -ne $expectedClaude -or $fixture.AgentProvider -ne 'claude' -or
        $fixture.AgentArguments -ne '' -or $fixture.AgentMode -ne 'exec' -or $fixture.MattermostServerUrl -ne $peer -or
        $fixture.ChatApprovalMode -ne 'auto' -or $fixture.ChatResultMode -ne 'auto' -or
        [string]$fixture.MattermostToken -notmatch '^dpapi:' -or $fixture.MattermostAllowedUserIds -notmatch 'test-member') {
        throw 'Fixture settings must point to the verified offline Claude executable and loopback peer, with DPAPI token and auto/auto policy.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath 'work') -PathType Container)) { throw 'The trusted child workspace does not exist.' }

    $currentStep = 'first-install onboarding and invalid workspace'
    $currentSubstep = 'verify first-install guide'
    if (-not (Wait-Value 'OnboardingProgress' '1 / 3' 20) -or
        -not (Wait-Value 'OnboardingTitle' 'エージェントと作業フォルダー' 10)) {
        throw 'First-install onboarding did not start at step 1 of 3.'
    }
    $invalidPath = Join-Path $dataPath 'missing-workspace'
    Invoke-UI @('set-value','WorkspaceRoot',$invalidPath)
    Invoke-UI @('invoke','OnboardingNext')
    Start-Sleep -Milliseconds 500
    if ((Get-Value 'OnboardingProgress') -notmatch '1\s*/\s*3' -or (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json'))) {
        throw 'Invalid workspace advanced onboarding or created settings.json.'
    }
    Add-Pass 'first install starts onboarding and rejects an invalid workspace without saving'

    $currentSubstep = 'load the isolated DPAPI Mattermost fixture'
    Copy-Item -LiteralPath $fixtureFile -Destination (Join-Path $dataPath 'settings.json') -Force
    Invoke-UI @('invoke','NavOverview')
    Invoke-UI @('invoke','SetupReloadSettings')
    Wait-UI 'OnboardingTitle' 'エージェントと作業フォルダー' 15000
    Invoke-UI @('invoke','NavAgent')
    Invoke-UI @('invoke','OnboardingNext')
    if (-not (Wait-Value 'OnboardingProgress' '2 / 3' 15) -or
        -not (Wait-Value 'OnboardingTitle' 'チャット接続と許可ユーザー' 10)) { throw 'Step 0 did not save settings and advance to the chat setup step.' }
    Invoke-UI @('invoke','OnboardingNext')
    if (-not (Wait-Value 'OnboardingProgress' '3 / 3' 15)) { throw 'Step 1 did not save chat configuration and advance to connection verification.' }
    Add-Pass 'guide survives settings reload and advances only after saved agent and allowed-user settings'

    $currentStep = 'connect and complete onboarding'
    $currentSubstep = 'connect loopback Mattermost peer'
    Invoke-UI @('invoke','NavChat')
    Wait-UI 'ChatStatus' '' 10000
    if ((Get-Property 'ConnectMattermost' 'IsEnabled') -eq 'True') { Invoke-UI @('invoke','ConnectMattermost') }
    if (-not (Wait-Value 'ChatStatus' '接続しました' 30)) { throw 'Mattermost did not connect to the loopback peer.' }
    Invoke-UI @('invoke','OnboardingNext')
    if (-not (Wait-Until { (Get-Content -LiteralPath (Join-Path $dataPath 'onboarding.json') -Raw | ConvertFrom-Json).Status -eq 'completed' } 10)) {
        throw 'The completed onboarding status was not persisted.'
    }
    Add-Pass 'connected chat completes and persists onboarding'

    $currentStep = 'trusted workspace command'
    $currentSubstep = 'post child folder request and require local confirmation'
    $folderText = "folder $childPath"
    $historyBeforeFolder = (Get-HistoryRecords).Count
    $folderPost = Post-Request $folderText
    Select-Request $folderText
    if ((Get-HistoryRecords).Count -ne $historyBeforeFolder) { throw 'Folder request changed execution history before local confirmation.' }
    $sessionFile = Join-Path $dataPath 'sessions.json'
    $sessionsBeforeFolder = Get-Sessions
    $matchingBeforeFolder = @(Get-Workspaces $sessionsBeforeFolder | Where-Object { $_.Key.ThreadId -eq $folderPost.id -and $_.Key.UserId -eq 'test-member' })
    if ($matchingBeforeFolder.Count -gt 0) { throw 'Folder override was persisted before local confirmation.' }
    Invoke-UI @('invoke','ApproveChat')
    Wait-UI 'ChatRunConfirmation' '' 10000
    Invoke-UI @('invoke','PrimaryButton')
    if (-not (Wait-Until { @(Get-Workspaces (Get-Sessions) | Where-Object { $_.Key.ThreadId -eq $folderPost.id -and $_.Key.UserId -eq 'test-member' -and [IO.Path]::GetFullPath([string]$_.WorkspaceRoot) -eq $childPath }).Count -eq 1 } 15)) {
        throw 'The approved child workspace was not persisted for the request thread.'
    }
    if ((Get-HistoryRecords).Count -ne $historyBeforeFolder) { throw 'Changing a conversation workspace created execution history.' }
    if (-not (Wait-Until {
        $acknowledged = @((Get-Replies) | Where-Object {
            $_.root_id -eq $folderPost.id -and
            ([string]$_.message).Contains($childPath, [StringComparison]::Ordinal) -and
            ([string]$_.message).Contains('作業フォルダー', [StringComparison]::Ordinal)
        }).Count -gt 0
        $audited = @(Get-AuditRecords | Where-Object { $_.Kind -eq 'workspace_changed' -and $_.ThreadId -eq $folderPost.id }).Count -gt 0
        return $acknowledged -and $audited
    } 15)) {
        throw 'The confirmed workspace change did not persist both its Mattermost acknowledgement and workspace_changed audit event for the same thread.'
    }
    Add-Pass 'trusted folder change waits for PC confirmation, persists its acknowledgement and audit event, and writes only the conversation override'

    $currentSubstep = 'run an ordinary request in the changed workspace'
    $beforeRun = (Get-HistoryRecords).Count
    $normalText = 'onboarding-workspace-offline-fixture'
    $normalPost = Post-Request $normalText 'test-member' $folderPost.id
    if (-not (Wait-Until { $records = Get-HistoryRecords; $records.Count -gt $beforeRun -and $records[-1].Status -eq 'completed' } 40)) {
        throw 'The ordinary same-thread request did not complete with the offline fixture.'
    }
    $records = Get-HistoryRecords
    $record = $records[-1]
    $sessions = Get-Sessions
    $binding = @($sessions.Bindings | Where-Object { $_.Key.ThreadId -eq $folderPost.id -and $_.Key.UserId -eq 'test-member' } | Select-Object -First 1)[0]
    if ($record.ThreadId -ne $folderPost.id -or [IO.Path]::GetFullPath([string]$record.WorkspaceRoot) -ne $childPath -or
        $null -eq $binding -or [IO.Path]::GetFullPath([string]$binding.WorkspaceRoot) -ne $childPath -or -not $binding.SessionId) {
        throw 'Completed execution history or session binding does not use the child workspace CWD.'
    }
    if (-not (Wait-Until { @((Get-Replies) | Where-Object { $_.root_id -eq $folderPost.id -and $_.message -eq "claude-e2e: new; prompt=$normalText" }).Count -gt 0 } 15)) {
        throw 'The ordinary request did not return the exact offline fixture output.'
    }
    Add-Pass 'same-thread ordinary request runs offline fixture with child workspace in history and binding'

    $currentStep = 'reject out-of-trust workspace'
    $currentSubstep = 'verify rejection before confirmation and without state changes'
    $outsidePath = [IO.Path]::GetFullPath([string]$metadata.outsidePath)
    $outsideText = "folder $outsidePath"
    $historyBeforeOutside = (Get-HistoryRecords).Count
    $outsidePost = Post-Request $outsideText
    Select-Request $outsideText
    Invoke-UI @('invoke','ApproveChat')
    if (-not (Wait-Value 'ChatOutput' '信頼フォルダーの範囲外' 12)) { throw 'The out-of-trust folder request did not show the expected local validation error.' }
    if ((Get-HistoryRecords).Count -ne $historyBeforeOutside) { throw 'Out-of-trust folder request changed execution history.' }
    $outsideSessions = Get-Sessions
    if (@(Get-Workspaces $outsideSessions | Where-Object { $_.Key.ThreadId -eq $outsidePost.id }).Count -gt 0 -or
        @($outsideSessions.Bindings | Where-Object { $_.Key.ThreadId -eq $outsidePost.id }).Count -gt 0) {
        throw 'Out-of-trust folder request persisted an override or binding.'
    }
    if ((Get-Property 'ChatRunConfirmation' 'IsOpen') -eq 'True') { throw 'Out-of-trust folder request incorrectly opened the local approval dialog.' }
    Add-Pass 'out-of-trust folder is rejected before local confirmation with no history or session state'

    $currentStep = 'unlisted workspace request'
    $currentSubstep = 'verify unauthorized sender creates no request or state'
    $unlistedText = "folder `"$childPath`""
    $historyBeforeUnlisted = (Get-HistoryRecords).Count
    $unlistedPost = Post-Request $unlistedText 'unlisted-user'
    Start-Sleep -Seconds 2
    $unlistedSessions = Get-Sessions
    $unlistedVisible = $false
    try { $null = Wait-RequestVisible $unlistedText 2; $unlistedVisible = $true } catch { }
    if ((Get-HistoryRecords).Count -ne $historyBeforeUnlisted -or
        @(Get-Workspaces $unlistedSessions | Where-Object { $_.Key.ThreadId -eq $unlistedPost.id }).Count -gt 0 -or
        @($unlistedSessions.Bindings | Where-Object { $_.Key.ThreadId -eq $unlistedPost.id }).Count -gt 0 -or
        $unlistedVisible) {
        throw 'An unlisted user created an inbox item, execution history, or session state.'
    }
    Add-Pass 'unlisted sender creates no request, execution history, or workspace/session state'
}
catch {
    Add-Fail $currentStep "Failed at line $([int]$_.InvocationInfo.ScriptLineNumber) during $currentSubstep."
}
finally {
    $result = [ordered]@{
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        appPid = $AppPid
        checks = @($results)
        passed = (@($results | Where-Object { -not $_.passed }).Count -eq 0 -and $results.Count -gt 0)
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $resultsPath) -Force | Out-Null
    [IO.File]::WriteAllText($resultsPath, (ConvertTo-Json -InputObject $result -Depth 8), [Text.UTF8Encoding]::new($false))
}

Get-Content -LiteralPath $resultsPath -Raw
if (-not $result.passed) { exit 1 }
