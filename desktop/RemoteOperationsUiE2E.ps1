param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$TestAgentDirectory,
    [ValidateSet('ja','en')][string]$Language = 'ja',
    [switch]$ControlsOnly
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $artifactRoot ('remote-operations-ui-' + $runId)
$stageOneData = Join-Path $runRoot 'status-stop'
$stageTwoData = Join-Path $runRoot 'chat-input'
$stageThreeData = Join-Path $runRoot 'cancel-confirmation'
$script:checks = [Collections.Generic.List[object]]::new()
$script:server = $null
$script:app = $null
$script:endpoint = $null
$script:stageData = $null
$script:lastFixtureSnapshot = $null
$script:language = $Language

Add-Type -AssemblyName System.Security

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

function Assert-UnderRoot([string]$Path, [string]$Root, [string]$Label) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be below desktop/artifacts."
    }
    return $full
}

function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $script:stageData $Name) -Encoding utf8
}

function Check([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:checks.Add(@{ name = $Name; passed = $true })
    Write-Output "PASS $Name"
}

function UI([string[]]$Arguments, [int]$TimeoutSeconds = 20) {
    if (-not $script:app -or $script:app.HasExited) { throw 'The isolated app process exited unexpectedly.' }
    $output = & winapp ui @Arguments -a $script:app.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed ($LASTEXITCODE): $($Arguments -join ' ')" }
    return $output
}

function Wait-UI([string]$AutomationId, [string]$Value = '', [int]$TimeoutSeconds = 15, [switch]$Gone) {
    $arguments = @('wait-for', $AutomationId, '-t', [string]($TimeoutSeconds * 1000))
    if ($Gone) { $arguments += '--gone' }
    elseif ($Value.Length -gt 0) { $arguments += @('--value', $Value, '--contains') }
    UI $arguments | Out-Null
}

function New-OfflineServer {
    $portFile = Join-Path $script:stageData 'fixture-port.txt'
    $fixture = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $node = (Get-Command node -ErrorAction Stop).Source
    $script:server = Start-Process -FilePath $node -ArgumentList @('"' + $fixture + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while (-not (Test-Path -LiteralPath $portFile) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 150
    }
    Assert-True (Test-Path -LiteralPath $portFile) 'The offline loopback fixture did not start.'
    $port = (Get-Content -LiteralPath $portFile -Raw).Trim()
    Assert-True ($port -match '^\d+$') 'The fixture returned an invalid loopback port.'
    $script:endpoint = "http://127.0.0.1:$port"
}

function New-App([string]$DataDirectory, [string]$AgentProvider, [string]$Executable, [string]$Arguments, [string]$ApprovalMode, [bool]$ShareApprovals = $false, [bool]$ShareQuestions = $false) {
    $script:stageData = $DataDirectory
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $DataDirectory '.cab-remote-operations-ui') -Value 'Isolated remote operations UI E2E data.' -Encoding utf8
    $workspace = Join-Path $DataDirectory 'workspace'
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    $protected = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $agentMode = if ($AgentProvider -eq 'codex') { 'app-server' } else { 'exec' }
    Write-Json 'settings.json' @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = $AgentProvider; AgentMode = $agentMode
        AgentExecutable = $Executable; AgentArguments = $Arguments; UseAppServer = ($AgentProvider -eq 'codex')
        AgentPermissionMode = 'inherit'; TrustedWorkspaceRoots = $workspace
        MattermostServerUrl = $script:endpoint; MattermostChannelId = 'ui-channel'; MattermostToken = $protected
        MattermostAllowedUserIds = 'alice,bob'; ChatApprovalMode = $ApprovalMode; ChatResultMode = 'auto'
        ShareApprovalsInChat = $ShareApprovals; ShareAgentQuestionsInChat = $ShareQuestions
        AutoConnectProvider = 'none'
    }
    Write-Json 'onboarding.json' @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    Write-Json 'language.json' @{ Language = $script:language }
    Write-Json 'appearance.json' @{ Theme = $(if ($script:language -eq 'ja') { 'dark' } else { 'light' }) }
    Write-Json 'sessions.json' @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Write-Json 'history.json' @{ SchemaVersion = 1; Records = @() }
    Write-Json 'audit.json' @{ SchemaVersion = 1; Records = @() }
    $portableRoot = Assert-UnderRoot $AppDirectory $artifactRoot 'Portable application directory'
    if (Test-Path -LiteralPath (Join-Path $portableRoot 'AppxManifest.xml')) { throw 'The application payload must be unpackaged.' }
    $exe = Join-Path $portableRoot 'ChatAgentBridge.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Portable ChatAgentBridge.Desktop.exe was not found.' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($DataDirectory)
    $script:app = [Diagnostics.Process]::Start($start)
    Wait-UI 'NavChat' '' 30
    UI @('invoke', 'NavChat') | Out-Null
    Wait-UI 'ConnectMattermost' '' 15
    UI @('invoke', 'ConnectMattermost') | Out-Null
    $null = Wait-Fixture { param($snapshot) $snapshot.connections -eq 1 } 20
}

function Stop-Stage {
    if ($script:app -and -not $script:app.HasExited) {
        try { UI @('invoke', 'DisconnectChat') | Out-Null } catch { }
        try { $script:app.CloseMainWindow() | Out-Null } catch { }
        try {
            if (-not $script:app.WaitForExit(5000)) { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
    if ($script:server -and -not $script:server.HasExited) {
        Stop-Process -Id $script:server.Id -Force -ErrorAction SilentlyContinue
    }
    $script:app = $null
    $script:server = $null
    $script:endpoint = $null
}

function Post([string]$Id, [string]$Thread, [string]$User, [string]$Text, [bool]$Plain = $false) {
    $body = @{ id = $Id; thread = $Thread; user = $User; text = $Text; mention = $false; plain = $Plain } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Assert-True ($response.delivered -eq 1) 'Expected exactly one connected offline transport.'
}

function Wait-Fixture([scriptblock]$Condition, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $snapshot = Invoke-RestMethod "$script:endpoint/test/results"
        $script:lastFixtureSnapshot = $snapshot
        if (& $Condition $snapshot) { return $snapshot }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    $caller = @(Get-PSCallStack)[1]
    throw "The loopback Mattermost fixture did not reach the expected state (called from $($caller.Command), line $($caller.ScriptLineNumber))."
}

function Read-Fixture {
    Invoke-RestMethod "$script:endpoint/test/results"
}

function Capture([string]$Name) {
    $path = Join-Path $script:stageData ($Name + '.png')
    UI @('screenshot', '--focus', '-w', "$($script:app.MainWindowHandle)", '-o', $path) | Out-Null
}

function Reply([object]$Snapshot, [string]$Thread, [scriptblock]$Predicate) {
    @($Snapshot.replies | Where-Object { $_.root_id -eq $Thread -and (& $Predicate $_) })
}

function Expected-Starts([string]$Kind) {
    if ($script:language -eq 'ja') {
        switch ($Kind) {
            'status' { return '状態:' }
            'none' { return 'このスレッドに、あなたの依頼はありません。' }
            'stop' { return '停止を要求しました。実行中:' }
            'stop-none' { return 'このスレッドに、あなたの実行中・待機中の依頼はありません。' }
        }
    }
    switch ($Kind) {
        'status' { return 'Status:' }
        'none' { return 'No requests from you in this thread.' }
        'stop' { return 'Stop requested. Running:' }
        'stop-none' { return 'No running or pending requests from you in this thread.' }
    }
}

function Check-StatusStopStage {
    $root = 'remote-ops-root-' + $runId
    $otherRoot = 'remote-ops-other-' + $runId
    $unboundRoot = 'remote-ops-unbound-' + $runId
    Post ('run-' + $runId) $root 'alice' ('remote-operations-slow-' + $runId)
    $null = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root }).Count -ge 1 } 20
    Post ('queue-' + $runId) $root 'alice' ('remote-operations-queued-' + $runId)
    $null = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root }).Count -ge 2 } 20

    Check 'Status, ownership isolation, and plain reply filtering' {
        Post ('status-alice-' + $runId) $root 'alice' '!cab status' $true
        $status = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message.StartsWith((Expected-Starts 'status')) }).Count -ge 1 } 10
        Assert-True ((Reply $status $root { param($p) $p.message.StartsWith((Expected-Starts 'status')) }).Count -eq 1) 'Alice status did not return one scoped status response.'

        Post ('status-bob-' + $runId) $root 'bob' '!cab status' $true
        $bob = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message -eq (Expected-Starts 'none') }).Count -ge 1 } 10
        Assert-True (@(Reply $bob $root { param($p) $p.message -eq (Expected-Starts 'none') }).Count -eq 1) 'Bob learned about Alice work or did not receive the generic no-work status.'

        Post ('stop-wrong-root-' + $runId) $otherRoot 'alice' '!cab stop' $true
        $wrongRoot = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $otherRoot -and $_.message -eq (Expected-Starts 'stop-none') }).Count -ge 1 } 10
        Assert-True (@(Reply $wrongRoot $otherRoot { param($p) $p.message -eq (Expected-Starts 'stop-none') }).Count -eq 1) 'A stop from another thread exposed or stopped the active request.'

        Post ('ordinary-top-level-' + $runId) '' 'alice' ('ordinary top level ' + $runId) $true
        Post ('ordinary-other-thread-' + $runId) $unboundRoot 'alice' ('ordinary unrelated reply ' + $runId) $true
        Post ('ordinary-unauthorized-' + $runId) $root 'mallory' ('unauthorized ' + $runId) $true
        Start-Sleep -Milliseconds 900
        $snapshot = Read-Fixture
        Assert-True (@($snapshot.replies | Where-Object { ($_.root_id -eq '' -and $_.message -like '*ordinary*') -or $_.root_id -eq $unboundRoot }).Count -eq 0) 'A plain top-level message or unrelated thread created a reply.'
        Assert-True (@($snapshot.replies | Where-Object { $_.root_id -eq $root -and $_.message -like '*unauthorized*' }).Count -eq 0) 'An unauthorized user received an execution response.'
    }

    Check 'Owner stop cancels active and queued work and stale answers stay consumed' {
        Post ('stale-answer-' + $runId) $root 'alice' '!cab answer 000000000000 1 1' $true
        Post ('stop-alice-' + $runId) $root 'alice' '!cab stop' $true
        $stopped = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message.StartsWith((Expected-Starts 'stop')) }).Count -ge 1 } 12
        $stopReply = @(Reply $stopped $root { param($p) $p.message.StartsWith((Expected-Starts 'stop')) })[-1]
        if ($script:language -eq 'ja') {
            Assert-True ($stopReply.message -match '^停止を要求しました。実行中: 1件、待機中の取り消し: 1件。$') 'Owner stop did not report one active and one queued cancellation.'
        } else {
            Assert-True ($stopReply.message -eq 'Stop requested. Running: 1; pending cancelled: 1.') 'Owner stop did not report one active and one queued cancellation.'
        }
        $settled = Wait-Fixture { param($s) @($s.updates | Where-Object { $_.root_id -eq $root -and $_.message -match 'Cancelled|Canceled|Execution stopped|実行を停止|中断' }).Count -ge 1 } 20
        Post ('stop-again-' + $runId) $root 'alice' '!cab stop' $true
        $none = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message -eq (Expected-Starts 'stop-none') }).Count -ge 1 } 10
        Assert-True (@(Reply $none $root { param($p) $p.message -eq (Expected-Starts 'stop-none') }).Count -eq 1) 'Stop left owner work active or pending.'
        Assert-True (@($settled.replies | Where-Object { $_.root_id -eq $root -and $_.message -like '*remote-operations-slow-*' }).Count -eq 0) 'A command or prompt was forwarded as an AI reply.'
        $history = Get-Content -LiteralPath (Join-Path $script:stageData 'history.json') -Raw | ConvertFrom-Json
        Assert-True (@($history.Records | Where-Object { $_.ThreadId -eq $root }).Count -eq 1) 'Control commands started extra execution history records.'
    }
}

function Check-ChatInputStage([string]$TestAgentPath) {
    $root = 'chat-input-root-' + $runId
    $prompt = 'remote-input-' + $runId
    Post ('input-run-' + $runId) $root 'alice' $prompt
    Wait-UI 'AppServerApprovalDialog' '' 35

    Check 'Approval code remains scoped to the requester and thread' {
        $approval = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message -match '!cab approve ([0-9a-fA-F]{12})' }).Count -ge 1 } 25
        $approvalPost = @(Reply $approval $root { param($p) $p.message -match '!cab approve ([0-9a-fA-F]{12})' })[-1]
        $match = [regex]::Match([string]$approvalPost.message, '!cab approve ([0-9a-fA-F]{12})')
        Assert-True $match.Success 'Shared approval message omitted its one-time code.'
        $approvalCode = $match.Groups[1].Value
        Post ('approval-wrong-user-' + $runId) $root 'bob' ("!cab approve $approvalCode") $true
        Post ('approval-wrong-thread-' + $runId) ('wrong-' + $root) 'alice' ("!cab approve $approvalCode") $true
        Start-Sleep -Milliseconds 800
        Wait-UI 'AppServerApprovalDialog' '' 3
        $null = Read-Fixture
        Post ('approval-right-user-' + $runId) $root 'alice' ("!cab approve $approvalCode") $true
        Wait-UI 'AppServerApprovalDialog' '' 5 -Gone
        Wait-UI 'AppServerInputDialog' '' 20
    }

    Check 'Shared question answer completes one request without publishing provider IDs' {
        $question = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message -match '!cab answer [0-9a-fA-F]{12} 1' }).Count -ge 1 } 20
        $questionPost = @(Reply $question $root { param($p) $p.message -match '!cab answer ([0-9a-fA-F]{12}) 1' })[-1]
        $answerMatch = [regex]::Match([string]$questionPost.message, '!cab answer ([0-9a-fA-F]{12}) 1')
        Assert-True $answerMatch.Success 'Shared question did not include a one-time answer code.'
        $answerCode = $answerMatch.Groups[1].Value
        Assert-True ([string]$questionPost.message -notmatch '\bchoice\b|fixture-question|fixture-input') 'The shared question exposed an app-server question identifier.'

        $disconnectBody = @{ offlineMilliseconds = 1000 } | ConvertTo-Json -Compress
        Invoke-RestMethod "$script:endpoint/test/disconnect" -Method Post -ContentType 'application/json' -Body $disconnectBody | Out-Null
        $null = Wait-Fixture { param($s) $s.connections -eq 0 } 12
        $null = Wait-Fixture { param($s) $s.connections -eq 1 } 30
        Wait-UI 'AppServerInputDialog' '' 10
        Capture 'question-pending'

        Post ('answer-wrong-user-' + $runId) $root 'bob' ("!cab answer $answerCode 1 2") $true
        Post ('answer-wrong-thread-' + $runId) ('wrong-' + $root) 'alice' ("!cab answer $answerCode 1 2") $true
        Post ('answer-stale-code-' + $runId) $root 'alice' '!cab answer 000000000000 1 2' $true
        Post ('answer-malformed-' + $runId) $root 'alice' '!cab answer invalid 1 2' $true
        Start-Sleep -Milliseconds 800
        Wait-UI 'AppServerInputDialog' '' 3

        Post ('answer-right-user-' + $runId) $root 'alice' ("!cab answer $answerCode 1 2") $true
        Wait-UI 'AppServerInputDialog' '' 30 -Gone
        $expectedResult = "app-server-e2e: approved; answer=beta; prompt=$prompt"
        $finished = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message -eq $expectedResult }).Count -eq 1 } 40
        $result = @(Reply $finished $root { param($p) $p.message -eq $expectedResult })
        Assert-True ($result.Count -eq 1) 'The answer did not produce exactly one successful agent result.'
        $progressIds = @($finished.updates | Where-Object { $_.root_id -eq $root } | Select-Object -ExpandProperty id -Unique)
        $receipt = @($finished.replies | Where-Object { $_.root_id -eq $root -and $_.id -in $progressIds })
        Assert-True ($receipt.Count -eq 1) 'The final result did not remain separate from the progress receipt.'
        Assert-True (@($finished.updates | Where-Object { $_.id -eq $receipt[0].id -and $_.message -match 'Completed|完了' }).Count -ge 1) 'The same progress post was not edited to completed.'
        $history = Get-Content -LiteralPath (Join-Path $script:stageData 'history.json') -Raw | ConvertFrom-Json
        Assert-True (@($history.Records | Where-Object { $_.ThreadId -eq $root }).Count -eq 1) 'Answer commands started extra history runs.'
    }
}

function Check-PendingConfirmationStopStage([string]$NodePath, [string]$SlowAgentPath) {
    $root = 'pending-confirmation-root-' + $runId
    $prompt = 'pending-confirmation-' + $runId
    Post ('confirmation-run-' + $runId) $root 'alice' $prompt
    Wait-UI $prompt '' 20
    UI @('click', $prompt) | Out-Null
    Wait-UI 'ApproveChat' '' 15
    UI @('invoke', 'ApproveChat') | Out-Null
    Wait-UI 'ChatRunConfirmation' '' 10

    Check 'Stop cancels the local run confirmation before the agent starts' {
        Post ('confirmation-stop-' + $runId) $root 'alice' '!cab stop' $true
        Wait-UI 'ChatRunConfirmation' '' 15 -Gone
        $snapshot = Wait-Fixture { param($s) @($s.replies | Where-Object { $_.root_id -eq $root -and $_.message.StartsWith((Expected-Starts 'stop')) }).Count -eq 1 } 12
        if ($script:language -eq 'ja') {
            Assert-True (@($snapshot.replies | Where-Object { $_.root_id -eq $root -and $_.message -eq '停止を要求しました。実行中: 1件、待機中の取り消し: 0件。' }).Count -eq 1) 'Confirmation stop did not report the active confirmation.'
        } else {
            Assert-True (@($snapshot.replies | Where-Object { $_.root_id -eq $root -and $_.message -eq 'Stop requested. Running: 1; pending cancelled: 0.' }).Count -eq 1) 'Confirmation stop did not report the active confirmation.'
        }
        $history = Get-Content -LiteralPath (Join-Path $script:stageData 'history.json') -Raw | ConvertFrom-Json
        Assert-True (@($history.Records | Where-Object { $_.ThreadId -eq $root }).Count -eq 0) 'A cancelled local confirmation launched the agent.'
        $stopPost = @(Reply $snapshot $root { param($p) $p.message.StartsWith((Expected-Starts 'stop')) })[-1]
        $receipt = @(Reply $snapshot $root { param($p) $p.id -ne $stopPost.id })
        Assert-True ($receipt.Count -eq 1) 'Expected exactly one initial request receipt.'
        $cancelledPrefix = if ($script:language -eq 'ja') { '⏹ 実行を停止しました。経過 ' } else { '⏹ Execution stopped. Elapsed ' }
        $terminal = Wait-Fixture { param($s) @($s.updates | Where-Object { $_.id -eq $receipt[0].id -and $_.message.StartsWith($cancelledPrefix) }).Count -ge 1 } 15
        Assert-True (@($terminal.updates | Where-Object { $_.id -eq $receipt[0].id -and $_.message.StartsWith($cancelledPrefix) }).Count -ge 1) 'The request receipt was not edited to the localized cancelled state.'
    }
}

try {
    $null = Assert-UnderRoot $AppDirectory $artifactRoot 'Portable application directory'
    $codex = $null
    if (-not $ControlsOnly) {
        if ([string]::IsNullOrWhiteSpace($TestAgentDirectory)) { throw 'TestAgentDirectory is required unless -ControlsOnly is selected.' }
        $agentHost = Assert-UnderRoot $TestAgentDirectory $artifactRoot 'Offline TestAgent directory'
        $agentMarker = Join-Path $agentHost '.cab-offline-test-agent'
        $codex = Join-Path $agentHost 'codex.exe'
        $fixtureExe = Join-Path $agentHost 'ChatAgentBridge.TestAgent.exe'
        $fixtureDll = Join-Path $agentHost 'ChatAgentBridge.TestAgent.dll'
        foreach ($required in @($agentMarker, $codex, $fixtureExe, $fixtureDll)) {
            if (-not (Test-Path -LiteralPath $required)) { throw 'The TestAgent directory is not a complete marked offline fixture.' }
        }
        if ((Get-FileHash -LiteralPath $codex -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $fixtureExe -Algorithm SHA256).Hash) {
            throw 'codex.exe is not the marked offline TestAgent fixture.'
        }
    }
    $node = (Get-Command node -ErrorAction Stop).Source
    New-Item -ItemType Directory -Path $stageOneData -Force | Out-Null
    $slowAgent = Join-Path $stageOneData 'slow-agent.cjs'
@'
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', part => input += part);
process.stdin.on('end', () => setTimeout(() => {
  process.stdout.write('offline-slow-result: ' + input);
}, 120000));
'@ | Set-Content -LiteralPath $slowAgent -Encoding utf8

    $script:stageData = $stageOneData
    New-OfflineServer
    New-App $stageOneData 'custom' $node ('"' + $slowAgent + '"') 'auto'
    Check-StatusStopStage
    Stop-Stage

    if (-not $ControlsOnly) {
        New-Item -ItemType Directory -Path $stageTwoData -Force | Out-Null
        $script:stageData = $stageTwoData
        New-OfflineServer
        New-App $stageTwoData 'codex' $codex 'app-server' 'auto' $true $true
        Check-ChatInputStage $codex
        Stop-Stage

        New-Item -ItemType Directory -Path $stageThreeData -Force | Out-Null
        $script:stageData = $stageThreeData
        New-OfflineServer
        New-App $stageThreeData 'custom' $node ('"' + $slowAgent + '"') 'always'
        Check-PendingConfirmationStopStage $node $slowAgent
        Stop-Stage
    }
} catch {
    $replySummary = @($script:lastFixtureSnapshot.replies | ForEach-Object { [pscustomobject]@{ root_id = $_.root_id; message = $_.message } })
    $updateSummary = @($script:lastFixtureSnapshot.updates | ForEach-Object { [pscustomobject]@{ id = $_.id; root_id = $_.root_id; message = $_.message } })
    $script:checks.Add(@{ name = 'Remote operations UI E2E'; passed = $false; detail = "$($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line)"; fixtureReplies = $replySummary; fixtureUpdates = $updateSummary })
    Write-Output "FAIL $($_.Exception.Message)"
} finally {
    Stop-Stage
    if (Test-Path -LiteralPath $runRoot) {
        $failed = @($script:checks | Where-Object { -not $_.passed }).Count -gt 0
        $script:stageData = $runRoot
        Write-Json 'results.json' @{ passed = ($script:checks.Count -gt 0 -and -not $failed); checks = @($script:checks) }
    }
    Write-Output "Remote operations UI results: $runRoot"
}

if ($script:checks.Count -eq 0 -or @($script:checks | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
