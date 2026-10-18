param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$resultsPath = Join-Path $artifactRoot 'execution-policy-review/ui-results.json'
$results = [System.Collections.Generic.List[object]]::new()
$currentStep = 'preflight'
$currentSubstep = 'validate isolated fixture'
$peer = $null

function Assert-TestDirectory {
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $dataPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'DataDirectory must be under desktop/artifacts.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-execution-policy-test'))) { throw 'The isolated execution-policy test marker is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json'))) { throw 'The isolated settings file is missing.' }
    $peerInfo = Get-Content -LiteralPath (Join-Path $dataPath 'peer.json') -Raw | ConvertFrom-Json
    if ($peerInfo.url -notmatch '^http://127\.0\.0\.1:\d+$' -or -not $peerInfo.pid) { throw 'The loopback notification peer metadata is invalid.' }
    if (-not (Get-Process -Id ([int]$peerInfo.pid) -ErrorAction SilentlyContinue)) { throw 'The isolated notification peer is not running.' }
    $script:peer = [string]$peerInfo.url
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
        return @{ ExitCode=$child.ExitCode; Output=$stdout.GetAwaiter().GetResult() }
    } finally { $child.Dispose() }
}

function Invoke-UI([string[]]$Arguments) {
    if ($Arguments[0] -eq 'invoke' -and $Arguments[1] -match '^Nav') {
        $selection = Invoke-UiProcess @('get-property', $Arguments[1], '-p', 'IsSelected', '--json')
        if ($selection.ExitCode -eq 0 -and ($selection.Output | ConvertFrom-Json).properties.IsSelected -eq 'True') { return }
    }
    $result = Invoke-UiProcess $Arguments
    if ($result.ExitCode -ne 0) { throw "UI action failed at $($Arguments[0]) on $($Arguments[1])." }
}

function Wait-UI([string]$AutomationId, [string]$Value = '', [int]$TimeoutMs = 30000) {
    $args = @('wait-for', $AutomationId, '-t', "$TimeoutMs")
    if ($Value.Length -gt 0) { $args += @('--value', $Value, '--contains') }
    Invoke-UI $args
}

function Wait-Enabled([string]$AutomationId, [int]$TimeoutMs = 10000) {
    Invoke-UI @('wait-for', $AutomationId, '-p', 'IsEnabled', '--value', 'True', '-t', "$TimeoutMs")
}

function Wait-RequestVisible([string]$Prompt, [int]$TimeoutSeconds = 25) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        $tree = Invoke-UiProcess @('inspect','ChatRequests','--json','-d','8')
        if ($tree.ExitCode -eq 0) {
            try {
                $document = $tree.Output | ConvertFrom-Json
                $selector = Find-RequestListItemSelector $document $Prompt
                if ($selector) { return [pscustomobject]@{ tree=$document; selector=$selector } }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'The pending request did not appear in the UI tree.'
}

function Find-RequestListItemSelector($Node, [string]$Prompt) {
    if ($null -eq $Node) { return $null }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string] -and $Node -isnot [pscustomobject]) {
        foreach ($child in $Node) {
            $found = Find-RequestListItemSelector $child $Prompt
            if ($found) { return $found }
        }
        return $null
    }
    if ($Node -is [pscustomobject]) {
        if ($Node.type -eq 'ListItem' -and $Node.selector -and
            ($Node | ConvertTo-Json -Depth 48 -Compress).Contains($Prompt, [StringComparison]::Ordinal)) {
            return [string]$Node.selector
        }
        foreach ($property in $Node.PSObject.Properties) {
            $found = Find-RequestListItemSelector $property.Value $Prompt
            if ($found) { return $found }
        }
    }
    return $null
}

function Select-Request([string]$Prompt) {
    $script:currentSubstep = 'inspect pending request ListItem'
    $requestItem = Wait-RequestVisible $Prompt
    $script:currentSubstep = 'select pending request ListItem'
    Invoke-UI @('invoke',$requestItem.selector)
    $script:currentSubstep = 'wait for local approval action to enable'
    Wait-Enabled 'ApproveChat' 10000
}

function Get-Property([string]$AutomationId, [string]$Property) {
    $result = Invoke-UiProcess @('get-property', $AutomationId, '-p', $Property, '--json')
    if ($result.ExitCode -ne 0) { return $null }
    try { return ($result.Output | ConvertFrom-Json).properties.$Property } catch { return $null }
}

function Get-HistoryCount {
    $path = Join-Path $dataPath 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records).Count } catch { return 0 }
}

function Get-Replies { return @(Invoke-RestMethod -Method Get -Uri "$peer/test/replies" -TimeoutSec 5 -ErrorAction Stop) }

function Post-Request([string]$Text, [string]$User = 'test-member', [string]$Thread = '') {
    $body = @{ text=$Text; user=$User }
    if ($Thread) { $body.thread = $Thread }
    return Invoke-RestMethod -Method Post -Uri "$peer/test/post" -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 5 -ErrorAction Stop
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 25) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Add-Pass([string]$Name) { $results.Add([pscustomobject]@{ name=$Name; passed=$true }) }
function Add-Fail([string]$Name, [string]$Detail) { $results.Add([pscustomobject]@{ name=$Name; passed=$false; detail=$Detail }) }

function Set-Policy([string]$Approval, [string]$ResultMode) {
    if ((Get-Property 'DisconnectChat' 'IsEnabled') -eq 'True') { Invoke-UI @('invoke','DisconnectChat') }
    $settingsFile = Join-Path $dataPath 'settings.json'
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $settings.ChatApprovalMode = $Approval
    $settings.ChatResultMode = $ResultMode
    $temp = "$settingsFile.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = ConvertTo-Json -InputObject $settings -Depth 20
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $settingsFile -Force
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
    Invoke-UI @('invoke','NavOverview')
    Invoke-UI @('invoke','ReloadSettings')
    Invoke-UI @('invoke','NavChat')
    Wait-UI 'ConnectMattermost' '' 10000
    if ((Get-Property 'ConnectMattermost' 'IsEnabled') -eq 'True') { Invoke-UI @('invoke','ConnectMattermost') }
    Wait-UI 'ChatStatus' '接続しました' 25000
}

function Wait-Execution([int]$BeforeCount) {
    return Wait-Until {
        if (-not (Test-Path -LiteralPath (Join-Path $dataPath 'history.json'))) { return $false }
        $records = @((Get-Content -LiteralPath (Join-Path $dataPath 'history.json') -Raw | ConvertFrom-Json).Records)
        return $records.Count -gt $BeforeCount -and $records[-1].Status -eq 'completed'
    } 35
}

try {
    $currentStep = 'preflight'
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'AppPid is not running.' }
    Assert-TestDirectory
    $settings = Get-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Raw | ConvertFrom-Json
    $fixtureHost = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'mattermost-ui-agent'))
    $expectedClaude = [IO.Path]::GetFullPath((Join-Path $fixtureHost 'claude.exe'))
    $marker = Join-Path $fixtureHost '.cab-offline-test-agent'
    $hostDll = Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.dll'
    $builtDll = Join-Path $PSScriptRoot 'ChatAgentBridge.TestAgent/bin/Debug/net10.0/ChatAgentBridge.TestAgent.dll'
    if (-not (Test-Path -LiteralPath $marker) -or -not (Test-Path -LiteralPath $expectedClaude) -or
        -not (Test-Path -LiteralPath $hostDll) -or -not (Test-Path -LiteralPath $builtDll) -or
        (Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash -or
        (Get-FileHash -LiteralPath $expectedClaude -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.exe') -Algorithm SHA256).Hash) {
        throw 'The marked offline Claude fixture executable failed its path or SHA256 checks.'
    }
    if ([IO.Path]::GetFullPath([string]$settings.AgentExecutable) -ne $expectedClaude -or $settings.AgentProvider -ne 'claude' -or
        $settings.AgentArguments -ne '' -or $settings.AgentMode -ne 'exec' -or $settings.MattermostServerUrl -ne $peer) {
        throw 'The isolated offline fixture settings do not match the marked Claude executable and peer.'
    }
    Wait-UI 'ChatStatus' '' 10000
    if ((Get-Property 'ConnectMattermost' 'IsEnabled') -eq 'True') {
        Invoke-UI @('invoke','ConnectMattermost')
        Wait-UI 'ChatStatus' '接続しました' 25000
    }
    Set-Policy 'auto' 'auto'

    $currentStep = 'auto-auto baseline'
    $currentSubstep = 'post and await matching fixture output'
    $baseline = Get-HistoryCount
    $autoPrompt = 'policy-auto-fixture-101'
    $autoPost = Post-Request $autoPrompt
    if (-not (Wait-Execution $baseline)) { throw 'Auto policy did not complete an execution history record.' }
    if (-not (Wait-Until { @((Get-Replies) | Where-Object { $_.root_id -eq $autoPost.id -and $_.message -eq "claude-e2e: new; prompt=$autoPrompt" }).Count -gt 0 } 15)) {
        throw 'Auto result reply did not exactly match the offline Claude fixture final text.'
    }
    Add-Pass 'auto approval executes fixture and auto shares matching final text'

    $currentStep = 'unlisted user'
    $currentSubstep = 'verify access rejection without execution'
    $beforeUnlisted = Get-HistoryCount
    $unlistedPost = Post-Request 'policy-unlisted-202' 'unlisted-user'
    Start-Sleep -Seconds 2
    if ((Get-HistoryCount) -ne $beforeUnlisted -or @((Get-Replies) | Where-Object { $_.root_id -eq $unlistedPost.id }).Count -gt 0) {
        throw 'An unlisted user created execution history or a reply.'
    }
    Add-Pass 'unlisted user creates no execution history or replies'

    $currentStep = 'result none'
    $currentSubstep = 'verify state-only reply'
    Set-Policy 'auto' 'none'
    $beforeNone = Get-HistoryCount
    $nonePrompt = 'policy-none-fixture-303'
    $nonePost = Post-Request $nonePrompt
    if (-not (Wait-Execution $beforeNone)) { throw 'Execution did not complete with result sharing disabled.' }
    $noneReplies = Wait-Until { @((Get-Replies) | Where-Object { $_.root_id -eq $nonePost.id }).Count -gt 0 } 8
    $noneMessages = @((Get-Replies) | Where-Object { $_.root_id -eq $nonePost.id } | ForEach-Object message)
    $noneFixtureText = "claude-e2e: new; prompt=$nonePrompt"
    if (-not $noneReplies -or @($noneMessages | Where-Object { $_ -eq $nonePrompt -or $_ -eq $noneFixtureText }).Count -gt 0) {
        throw 'Result none did not send only the normal state reply.'
    }
    Add-Pass 'result none posts state notification without result text'

    $currentStep = 'always approval'
    $currentSubstep = 'post always request and observe UI item'
    Set-Policy 'always' 'manual'
    $beforeAlways = Get-HistoryCount
    $alwaysPrompt = 'policy-always-confirm-404'
    $null = Post-Request $alwaysPrompt
    Wait-RequestVisible $alwaysPrompt 25 | Out-Null
    $currentSubstep = 'verify history remains unchanged before local approval'
    Start-Sleep -Milliseconds 900
    if ((Get-HistoryCount) -ne $beforeAlways) { throw 'Always policy executed before PC confirmation.' }
    Select-Request $alwaysPrompt
    if ((Get-Property 'ApproveChat' 'IsEnabled') -ne 'True') { throw 'The always-policy request did not expose the local approval action.' }
    if ((Get-HistoryCount) -ne $beforeAlways) { throw 'Always policy recorded history before local approval was given.' }
    Add-Pass 'always policy requires PC confirmation before history is recorded'

    $currentStep = 'first approval and same-thread resume'
    $currentSubstep = 'confirm and complete initial turn'
    Set-Policy 'first' 'auto'
    $beforeFirst = Get-HistoryCount
    $firstPrompt = 'policy-first-human-505'
    $firstPost = Post-Request $firstPrompt
    Wait-RequestVisible $firstPrompt 25 | Out-Null
    Start-Sleep -Milliseconds 900
    if ((Get-HistoryCount) -ne $beforeFirst) { throw 'First policy executed its initial turn without PC confirmation.' }
    Select-Request $firstPrompt
    Invoke-UI @('invoke','ApproveChat')
    Wait-UI 'ChatRunConfirmation' '' 10000
    Invoke-UI @('invoke','PrimaryButton')
    if (-not (Wait-Execution $beforeFirst)) { throw 'Human-approved first turn did not complete.' }
    $firstBinding = $null
    $sessionsPath = Join-Path $dataPath 'sessions.json'
    if (Test-Path -LiteralPath $sessionsPath) {
        $sessions = Get-Content -LiteralPath $sessionsPath -Raw | ConvertFrom-Json
        $firstBinding = @($sessions.Bindings | Where-Object { $_.Key.ThreadId -eq $firstPost.id -and $_.Key.UserId -eq 'test-member' } | Select-Object -First 1)[0]
    }
    if ($null -eq $firstBinding -or -not $firstBinding.SessionId -or -not $firstBinding.FirstApprovalFingerprint) { throw 'First approval fingerprint or saved session was not persisted.' }
    $beforeSecond = Get-HistoryCount
    $secondPrompt = 'policy-first-auto-resume-606'
    $secondPost = Post-Request $secondPrompt 'test-member' $firstPost.id
    if (-not (Wait-Execution $beforeSecond)) { throw 'Second same-thread request did not automatically resume.' }
    $sessions = Get-Content -LiteralPath $sessionsPath -Raw | ConvertFrom-Json
    $secondBinding = @($sessions.Bindings | Where-Object { $_.Key.ThreadId -eq $firstPost.id -and $_.Key.UserId -eq 'test-member' } | Select-Object -First 1)[0]
    if ($secondBinding.SessionId -ne $firstBinding.SessionId) { throw 'First policy did not reuse the existing session for the same thread.' }
    if (-not (Wait-Until { @((Get-Replies) | Where-Object { $_.root_id -eq $firstPost.id -and $_.message -eq "claude-e2e: resumed; prompt=$secondPrompt" }).Count -gt 0 } 15)) {
        throw 'The same-thread automatic turn did not share the matching fixture result.'
    }
    Add-Pass 'first policy asks once then auto resumes the same thread session'
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
