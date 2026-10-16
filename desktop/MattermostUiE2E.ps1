param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$TestAgentPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [System.IO.Path]::GetFullPath($DataDirectory)
$fixturePath = Join-Path $artifactRoot 'mattermost-e2e.json'
$resultsPath = Join-Path $artifactRoot 'mattermost-ui-e2e-results.json'
$markerName = '.cab-mattermost-ui-e2e'
$results = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[string]]::new()
$currentStep = 'preflight'

function Assert-UnderArtifactRoot([string]$Path) {
    $prefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The UI E2E data directory must be a child of desktop/artifacts.'
    }
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
        if (-not $child.WaitForExit(45000)) {
            $child.Kill()
            throw "winapp UI action failed at verb '$($Arguments[0])', selector '$($Arguments[1])'."
        }
        return @{ ExitCode=$child.ExitCode; Output=$stdout.GetAwaiter().GetResult() }
    } finally { $child.Dispose() }
}

function Invoke-UI([string[]]$Arguments) {
    if ($Arguments[0] -eq 'invoke' -and $Arguments[1] -match '^Nav') {
        $selection = Invoke-UiProcess @('get-property', $Arguments[1], '-p', 'IsSelected', '--json')
        if ($selection.ExitCode -eq 0 -and ($selection.Output | ConvertFrom-Json).properties.IsSelected -eq 'True') { return }
    }
    $result = Invoke-UiProcess $Arguments
    if ($result.ExitCode -ne 0) { throw "winapp UI action failed at verb '$($Arguments[0])', selector '$($Arguments[1])'." }
}

function Select-ComboItem([string]$AutomationId, [string]$ItemAutomationId) {
    Invoke-UI @('invoke', $AutomationId)
    Invoke-UI @('invoke', $ItemAutomationId)
}

function Wait-UI([string]$AutomationId, [string]$Value = '', [int]$TimeoutMs = 30000) {
    $args = @('wait-for', $AutomationId, '-t', "$TimeoutMs")
    if ($Value.Length -gt 0) { $args += @('--value', $Value, '--contains') }
    Invoke-UI $args
}

function Invoke-MM([string]$Method, [string]$Path, [string]$Token, $Body = $null) {
    $headers = @{ Authorization = "Bearer $Token" }
    $uri = $script:state.serverUrl.TrimEnd('/') + $Path
    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 10 -ErrorAction Stop
        }
        $json = ConvertTo-Json -InputObject $Body -Depth 8 -Compress
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 10 -ErrorAction Stop
    }
    catch { throw "Mattermost API request failed for $Method $Path." }
}

function Get-ThreadPosts([string]$RootPostId, [string]$Token = $state.memberUser.token) {
    $thread = Invoke-MM 'Get' "/api/v4/posts/$([uri]::EscapeDataString($RootPostId))/thread" $Token
    if ($null -eq $thread.posts) { return @() }
    return @($thread.posts.PSObject.Properties | ForEach-Object { $_.Value })
}

function Wait-ThreadPost([string]$RootPostId, [scriptblock]$Predicate, [int]$TimeoutSeconds = 35) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        try {
            foreach ($post in (Get-ThreadPosts $RootPostId)) {
                if (& $Predicate $post) { return $post }
            }
        }
        catch { }
        Start-Sleep -Milliseconds 300
    }
    throw 'Timed out waiting for an expected Mattermost thread message.'
}

function Add-Pass([string]$Name) { $results.Add(@{ name = $Name; passed = $true }) }
function Add-Check([string]$Name, [bool]$Passed) { $results.Add(@{ name = $Name; passed = $Passed }) }

function Get-SessionBinding([string]$RootPostId) {
    $sessionPath = Join-Path $dataPath 'sessions.json'
    if (-not (Test-Path -LiteralPath $sessionPath)) { return $null }
    $store = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
    return @($store.Bindings | Where-Object {
        $_.Key.Provider -eq 'mattermost' -and $_.Key.ChannelId -eq $state.channel.id -and $_.Key.ThreadId -eq $RootPostId
    } | Select-Object -First 1)[0]
}

function Get-HistoryCount {
    $path = Join-Path $dataPath 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $history = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    return @($history.Records).Count
}

function Scroll-PromptIntoView([string]$AutomationId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        & winapp ui scroll-into-view $AutomationId -a $AppPid *> $null
        if ($LASTEXITCODE -eq 0) { return }
        # A new WebSocket event may not have arrived yet. A short list has no
        # scroll range; only finding the actual request completes this helper.
        & winapp ui scroll ChatRequests --to bottom -a $AppPid *> $null
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The incoming workflow request did not become selectable.'
}

function Invoke-WorkflowTurn([string]$AuthorToken, [string]$Prompt, [string]$RootPostId, [string]$ExpectedSessionId, [bool]$TryOtherMemberApproval, [bool]$CancelSharing) {
    $postBody = @{
        channel_id = $state.channel.id
        message = "!cab $Prompt"
    }
    if (-not [string]::IsNullOrWhiteSpace($RootPostId)) { $postBody.root_id = $RootPostId }
    $created = Invoke-MM 'Post' '/api/v4/posts' $AuthorToken $postBody
    $turnRootId = if ([string]::IsNullOrWhiteSpace($RootPostId)) { [string]$created.id } else { $RootPostId }
    if ([string]::IsNullOrWhiteSpace($turnRootId)) { throw 'Mattermost did not return the workflow root post ID.' }
    $historyCountBeforeApproval = Get-HistoryCount
    Scroll-PromptIntoView $Prompt
    Wait-UI $Prompt '' 30000
    Start-Sleep -Milliseconds 700
    if ((Get-HistoryCount) -ne $historyCountBeforeApproval) {
        throw 'A request entered execution history before local approval.'
    }
    Add-Pass 'Request did not enter execution history before local approval'
    Invoke-UI @('click', $Prompt)
    Wait-UI 'ApproveChat'
    Invoke-UI @('invoke', 'ApproveChat')
    Wait-UI 'ChatRunConfirmation'
    Invoke-UI @('invoke', 'PrimaryButton')
    Wait-UI 'AppServerApprovalDialog' '' 30000

    $approvalPost = Wait-ThreadPost $turnRootId {
        param($post)
        ([long]$post.create_at -ge [long]$created.create_at) -and [string]$post.message -match '!cab approve ([0-9a-fA-F]{12})'
    } 35
    $approvalMatch = [regex]::Match([string]$approvalPost.message, '!cab approve ([0-9a-fA-F]{12})')
    if (-not $approvalMatch.Success) { throw 'App Server approval message did not contain a one-time code.' }
    $approvalCode = $approvalMatch.Groups[1].Value

    $visibleToOtherMember = $false
    $otherMemberPosts = Get-ThreadPosts $turnRootId $state.unauthorizedUser.token
    $visibleToOtherMember = @($otherMemberPosts | Where-Object { [string]$_.message -match [regex]::Escape($approvalCode) }).Count -gt 0
    if (-not $visibleToOtherMember) { throw 'The channel participant could not read the pending thread approval metadata.' }
    $findings.Add('A third channel participant could read the pending approval code and metadata before result sharing.')

    $wrongMemberAccepted = $false
    if ($TryOtherMemberApproval) {
        $wrongApproverToken = if ($AuthorToken -eq $state.memberUser.token) { $state.unauthorizedUser.token } else { $state.memberUser.token }
        $null = Invoke-MM 'Post' '/api/v4/posts' $wrongApproverToken @{
            channel_id = $state.channel.id
            root_id = $turnRootId
            message = "!cab approve $approvalCode"
        }
        & winapp ui wait-for AppServerApprovalDialog --gone -t 1800 -a $AppPid *> $null
        $wrongMemberAccepted = $LASTEXITCODE -eq 0
    }

    if (-not $wrongMemberAccepted) {
        $approverToken = $AuthorToken
        $null = Invoke-MM 'Post' '/api/v4/posts' $approverToken @{
            channel_id = $state.channel.id
            root_id = $turnRootId
            message = "!cab approve $approvalCode"
        }
    }
    Invoke-UI @('wait-for', 'AppServerApprovalDialog', '--gone', '-t', '30000')
    Wait-UI 'AppServerInputDialog' '' 30000
    Invoke-UI @('invoke', 'AppServerInputChoice1_1')
    Invoke-UI @('invoke', 'PrimaryButton')
    Wait-UI 'ChatOutput' "app-server-e2e: approved; answer=alpha; prompt=$Prompt" 35000

    $binding = Get-SessionBinding $turnRootId
    if ($null -eq $binding -or -not $binding.SessionId) { throw 'Workflow session binding was not persisted.' }
    if ($ExpectedSessionId -and $binding.SessionId -ne $ExpectedSessionId) { throw 'Same-thread workflow did not reuse its saved session.' }

    if ($CancelSharing) {
        Invoke-UI @('invoke', 'SendChatResult')
        Wait-UI 'ReplyConfirmation'
        Invoke-UI @('invoke', 'CloseButton')
        Invoke-UI @('wait-for', 'ReplyConfirmation', '--gone', '-t', '10000')
        Start-Sleep -Milliseconds 900
        $postsAfterCancel = Get-ThreadPosts $turnRootId
        if (@($postsAfterCancel | Where-Object { [string]$_.message -like "*prompt=$Prompt*" }).Count -gt 0) {
            throw 'A cancelled result was posted to Mattermost.'
        }
    }

    $null = Invoke-MM 'Post' '/api/v4/posts' $state.unauthorizedUser.token @{
        channel_id = $state.channel.id
        root_id = $turnRootId
        message = "!cab approve $approvalCode"
    }
    Invoke-UI @('wait-for', 'AppServerInputDialog', '--gone', '-t', '5000')
    & winapp ui wait-for AppServerApprovalDialog -t 1500 -a $AppPid *> $null
    $replayStartedNewTurn = $LASTEXITCODE -eq 0
    if ($wrongMemberAccepted) { $findings.Add('A non-request author could approve the same-root one-time code.') }
    if ($replayStartedNewTurn) { $findings.Add('Replaying a consumed one-time code opened another approval request.') }

    return [pscustomobject]@{
        RootPostId = $turnRootId
        SessionId = [string]$binding.SessionId
        OtherMemberCouldReadApproval = $visibleToOtherMember
        OtherMemberApprovalAccepted = $wrongMemberAccepted
        ReplayStartedNewTurn = $replayStartedNewTurn
    }
}

function Ensure-TestAgentHost {
    if (-not [string]::IsNullOrWhiteSpace($TestAgentPath)) {
        $candidate = [System.IO.Path]::GetFullPath($TestAgentPath)
        $hostDirectory = Join-Path $artifactRoot 'mattermost-ui-agent'
        $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $hostDirectory 'codex.exe'))
        $marker = Join-Path $hostDirectory '.cab-offline-test-agent'
        $fixtureExe = Join-Path $hostDirectory 'ChatAgentBridge.TestAgent.exe'
        $fixtureDll = Join-Path $hostDirectory 'ChatAgentBridge.TestAgent.dll'
        $builtFixtureDll = Join-Path $PSScriptRoot 'ChatAgentBridge.TestAgent/bin/Debug/net10.0/ChatAgentBridge.TestAgent.dll'
        if (-not $candidate.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $marker) -or -not (Test-Path -LiteralPath $fixtureExe) -or
            -not (Test-Path -LiteralPath $fixtureDll) -or -not (Test-Path -LiteralPath $builtFixtureDll)) {
            throw 'The requested offline TestAgent path is not the marked Mattermost fixture host.'
        }
        $codexHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        $fixtureHash = (Get-FileHash -LiteralPath $fixtureExe -Algorithm SHA256).Hash
        $hostDllHash = (Get-FileHash -LiteralPath $fixtureDll -Algorithm SHA256).Hash
        $builtDllHash = (Get-FileHash -LiteralPath $builtFixtureDll -Algorithm SHA256).Hash
        if ($codexHash -ne $fixtureHash -or $hostDllHash -ne $builtDllHash) {
            throw 'The offline TestAgent host or fixture assembly failed its SHA256 check.'
        }
        return $candidate
    }
    $project = Join-Path $PSScriptRoot 'ChatAgentBridge.TestAgent/ChatAgentBridge.TestAgent.csproj'
    $publishOutput = Join-Path $artifactRoot 'mattermost-ui-agent-publish'
    & dotnet publish $project --configuration Debug --runtime win-x64 --self-contained true --output $publishOutput --nologo *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the offline test agent fixture.' }
    $publishedExe = Join-Path $publishOutput 'ChatAgentBridge.TestAgent.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) { throw 'The offline test agent executable was not produced.' }
    $hostDirectory = Join-Path $artifactRoot 'mattermost-ui-agent'
    New-Item -ItemType Directory -Path $hostDirectory -Force | Out-Null
    $agentMarker = Join-Path $hostDirectory '.cab-offline-test-agent'
    if ((Get-ChildItem -LiteralPath $hostDirectory -Force | Where-Object Name -ne '.cab-offline-test-agent' | Measure-Object).Count -gt 0 -and -not (Test-Path -LiteralPath $agentMarker)) {
        throw 'Refusing to overwrite an unmarked test-agent directory.'
    }
    Copy-Item -Path (Join-Path $publishOutput '*') -Destination $hostDirectory -Recurse -Force
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $hostDirectory 'codex.exe') -Force
    Set-Content -LiteralPath $agentMarker -Value 'Offline test agent fixture; no model or command execution.' -Encoding utf8
    return (Join-Path $hostDirectory 'codex.exe')
}

try {
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'AppPid does not identify a running process.' }
    Assert-UnderArtifactRoot $dataPath
    if (-not (Test-Path -LiteralPath $fixturePath)) { throw 'The ignored Mattermost E2E fixture is missing.' }
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    $marker = Join-Path $dataPath $markerName
    if (-not (Test-Path -LiteralPath $marker)) {
        $knownFiles = @('settings.json', 'history.json', 'sessions.json', 'audit.json')
        foreach ($file in $knownFiles) {
            if (Test-Path -LiteralPath (Join-Path $dataPath $file)) { throw 'Use a fresh, isolated data directory for the first Mattermost UI E2E run.' }
        }
        Set-Content -LiteralPath $marker -Value 'Isolated Mattermost UI E2E data directory.' -Encoding utf8
    }
    $script:state = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
    if (-not $state.serverUrl -or -not $state.channel.id -or -not $state.bridgeUser.token -or -not $state.memberUser.token) {
        throw 'Mattermost fixture is incomplete.'
    }
    if (-not $state.unauthorizedUser.token -or -not $state.unauthorizedUser.id) {
        throw 'Run PrepareMattermostWorkflowE2E.ps1 to add the third test participant.'
    }
    if ($state.serverUrl -notmatch '^http://(127\.0\.0\.1|localhost):8070/?$') {
        throw 'Mattermost UI E2E is restricted to the isolated localhost:8070 fixture.'
    }
    $null = Get-Process -Id $AppPid
    $testAgentPath = Ensure-TestAgentHost
    $workspace = Join-Path $dataPath 'workspace'
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null

    $currentStep = 'verify-app-data-directory'
    Invoke-UI @('invoke', 'NavOverview')
    Wait-UI 'SettingsLocation' $dataPath
    Add-Pass 'App is using isolated data directory'

    $currentStep = 'configure-app-server'
    Invoke-UI @('invoke', 'NavAgentSettings')
    Wait-UI 'AgentProvider'
    Select-ComboItem 'AgentProvider' 'ProviderOptionCodex'
    Wait-UI 'UseAppServer'
    Invoke-UI @('set-value', 'WorkspaceRoot', $workspace)
    Invoke-UI @('set-value', 'AgentExecutable', $testAgentPath)
    Invoke-UI @('set-value', 'AgentModel', '')
    $persistedSettingsPath = Join-Path $dataPath 'settings.json'
    $persistedMode = if (Test-Path -LiteralPath $persistedSettingsPath) {
        (Get-Content -LiteralPath $persistedSettingsPath -Raw | ConvertFrom-Json).AgentMode
    } else { 'exec' }
    if ($persistedMode -ne 'app-server') { Invoke-UI @('invoke', 'UseAppServer') }
    Invoke-UI @('wait-for', 'AgentArguments', '-p', 'IsEnabled', '--value', 'False', '-t', '3000')
    Invoke-UI @('invoke', 'SaveSettings')
    Wait-UI 'Title' '設定を保存しました'
    $savedAgent = Get-Content -LiteralPath $persistedSettingsPath -Raw | ConvertFrom-Json
    if ($savedAgent.AgentProvider -ne 'codex' -or $savedAgent.AgentExecutable -ne $testAgentPath -or
        $savedAgent.AgentMode -ne 'app-server' -or $savedAgent.AgentModel) {
        throw 'Codex test-agent profile was not saved as expected.'
    }

    $currentStep = 'configure-mattermost'
    Invoke-UI @('invoke', 'NavConnections')
    Wait-UI 'MattermostServerUrl'
    Invoke-UI @('set-value', 'MattermostServerUrl', $state.serverUrl)
    Invoke-UI @('set-value', 'MattermostChannelId', $state.channel.id)
    # Keep the credential inside this process; suppress all native command output.
    Invoke-UI @('set-value', 'MattermostToken', $state.bridgeUser.token)
    Invoke-UI @('invoke', 'SaveConnections')
    Wait-UI 'Title' '設定を保存しました'
    $savedSettings = Get-Content -LiteralPath $persistedSettingsPath -Raw | ConvertFrom-Json
    if (-not ([string]$savedSettings.MattermostToken).StartsWith('dpapi:', [System.StringComparison]::Ordinal)) {
        throw 'Mattermost token was not stored using Windows DPAPI.'
    }
    Add-Pass 'Mattermost credential is DPAPI-protected'

    $currentStep = 'connect-chat'
    Invoke-UI @('invoke', 'NavChat')
    Wait-UI 'ConnectMattermost'
    & winapp ui wait-for DisconnectChat -p IsEnabled --value True -t 1000 -a $AppPid *> $null
    if ($LASTEXITCODE -eq 0) {
        Invoke-UI @('invoke', 'DisconnectChat')
        Invoke-UI @('wait-for', 'ConnectMattermost', '-p', 'IsEnabled', '--value', 'True', '-t', '10000')
    }
    Invoke-UI @('invoke', 'ConnectMattermost')
    Wait-UI 'ChatStatus' '接続しました' 35000
    Add-Pass 'Mattermost transport connected'

    $currentStep = 'receive-and-select-request'
    $prompt = 'desktop-ui-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $created = Invoke-MM 'Post' '/api/v4/posts' $state.memberUser.token @{
        channel_id = $state.channel.id
        message = "!cab $prompt"
    }
    $rootPostId = [string]$created.id
    if ([string]::IsNullOrWhiteSpace($rootPostId)) { throw 'Mattermost did not return the root post ID.' }
    Wait-UI $prompt '' 30000
    Invoke-UI @('click', $prompt)
    Wait-UI 'ApproveChat'
    Invoke-UI @('invoke', 'ApproveChat')
    Wait-UI 'ChatRunConfirmation'
    Invoke-UI @('invoke', 'PrimaryButton')
    Wait-UI 'AppServerApprovalDialog' '' 30000
    Add-Pass 'Incoming request locally approved'

    $currentStep = 'remote-app-server-approval'
    $approvalPost = Wait-ThreadPost $rootPostId {
        param($post)
        [string]$post.message -match '!cab approve ([0-9a-fA-F]{12})'
    } 35
    $approvalMatch = [regex]::Match([string]$approvalPost.message, '!cab approve ([0-9a-fA-F]{12})')
    if (-not $approvalMatch.Success) { throw 'App Server approval message did not contain a one-time code.' }
    $approvalCode = $approvalMatch.Groups[1].Value
    $null = Invoke-MM 'Post' '/api/v4/posts' $state.memberUser.token @{
        channel_id = $state.channel.id
        root_id = $rootPostId
        message = "!cab approve $approvalCode"
    }
    Invoke-UI @('wait-for', 'AppServerApprovalDialog', '--gone', '-t', '30000')
    Wait-UI 'AppServerInputDialog' '' 30000
    Add-Pass 'Remote scoped approval accepted'

    $currentStep = 'answer-app-server-input'
    Invoke-UI @('invoke', 'AppServerInputChoice1_1')
    Invoke-UI @('invoke', 'PrimaryButton')
    Wait-UI 'ChatOutput' "app-server-e2e: approved; answer=alpha; prompt=$prompt" 35000
    Wait-UI 'SendChatResult' '' 10000
    Add-Pass 'App Server input and result displayed'

    $currentStep = 'confirm-result-sharing'
    Invoke-UI @('invoke', 'SendChatResult')
    Wait-UI 'ReplyConfirmation'
    Invoke-UI @('invoke', 'PrimaryButton')
    $shared = Wait-ThreadPost $rootPostId {
        param($post)
        [string]$post.message -match 'app-server-e2e: approved; answer=alpha;'
    } 35
    Add-Pass 'Result sharing confirmed and visible in Mattermost thread'

    $currentStep = 'same-thread-follow-up'
    $initialBinding = Get-SessionBinding $rootPostId
    if ($null -eq $initialBinding -or -not $initialBinding.SessionId) { throw 'Initial Mattermost session binding was not found.' }
    $followupPrompt = 'workflow-followup-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $followup = Invoke-WorkflowTurn $state.memberUser.token $followupPrompt $rootPostId ([string]$initialBinding.SessionId) $true $true
    Add-Check 'Same-author same-root follow-up reused its session' ($followup.SessionId -eq $initialBinding.SessionId)
    Add-Check 'Other channel member approval-code attempt was rejected' (-not $followup.OtherMemberApprovalAccepted)
    Add-Check 'Consumed approval code did not start another turn' (-not $followup.ReplayStartedNewTurn)
    Add-Pass 'Cancelled result stayed out of the Mattermost thread'

    $currentStep = 'new-root-session'
    $newRootPrompt = 'workflow-new-root-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $newRoot = Invoke-WorkflowTurn $state.memberUser.token $newRootPrompt '' '' $false $true
    Add-Check 'A new Mattermost root received a distinct session' ($newRoot.SessionId -ne $initialBinding.SessionId)
    Add-Check 'Consumed approval code did not start another turn on the new root' (-not $newRoot.ReplayStartedNewTurn)

    $currentStep = 'transport-reconnect'
    Invoke-UI @('invoke', 'DisconnectChat')
    Invoke-UI @('wait-for', 'ConnectMattermost', '-p', 'IsEnabled', '--value', 'True', '-t', '10000')
    Invoke-UI @('invoke', 'ConnectMattermost')
    Wait-UI 'ChatStatus' '接続しました' 35000
    $reconnectPrompt = 'workflow-reconnect-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $afterReconnect = Invoke-WorkflowTurn $state.memberUser.token $reconnectPrompt $rootPostId ([string]$initialBinding.SessionId) $false $true
    Add-Check 'Reconnect continued the original thread session' ($afterReconnect.SessionId -eq $initialBinding.SessionId)

    $currentStep = 'third-user-same-thread'
    $thirdUserPrompt = 'workflow-third-user-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $thirdUserTurn = Invoke-WorkflowTurn $state.unauthorizedUser.token $thirdUserPrompt $rootPostId ([string]$initialBinding.SessionId) $true $true
    if ($thirdUserTurn.RootPostId -eq $rootPostId -and $thirdUserTurn.SessionId -eq $initialBinding.SessionId) {
        $findings.Add('A different channel participant could initiate a request on the existing root and continue its saved agent session after local approval.')
    }
    Add-Check 'Third-user consumed approval code did not start another turn' (-not $thirdUserTurn.ReplayStartedNewTurn)

    $currentStep = 'verify-session-persistence'
    $sessionPath = Join-Path $dataPath 'sessions.json'
    if (-not (Test-Path -LiteralPath $sessionPath)) { throw 'App Server session binding was not persisted.' }
    $sessionStore = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
    $binding = @($sessionStore.Bindings | Where-Object { $_.Key.Provider -eq 'mattermost' -and $_.Key.ChannelId -eq $state.channel.id -and $_.Key.ThreadId -eq $rootPostId }) | Select-Object -First 1
    if ($null -eq $binding -or $binding.AgentArguments -ne 'app-server' -or -not $binding.SessionId) {
        throw 'Expected the conversation to persist an App Server session binding.'
    }
    try { $null = [guid]$binding.SessionId }
    catch { throw 'Persisted session ID is invalid.' }
    Add-Pass 'App Server conversation session persisted'

    $historyPath = Join-Path $dataPath 'history.json'
    $auditPath = Join-Path $dataPath 'audit.json'
    if (-not (Test-Path -LiteralPath $historyPath) -or -not (Test-Path -LiteralPath $auditPath)) {
        throw 'Execution history or audit metadata was not persisted.'
    }
    $historyText = Get-Content -LiteralPath $historyPath -Raw
    $auditText = Get-Content -LiteralPath $auditPath -Raw
    foreach ($sensitiveValue in @($prompt, 'app-server-e2e: approved; answer=alpha;', [string]$state.bridgeUser.token, [string]$state.memberUser.token)) {
        if ($sensitiveValue.Length -gt 0 -and ($historyText.Contains($sensitiveValue) -or $auditText.Contains($sensitiveValue))) {
            throw 'History or audit metadata contains prompt, output, or token content.'
        }
    }
    Add-Pass 'History and audit exclude prompt, output, and token content'

    $failedChecks = @($results | Where-Object { -not $_.passed })
    $result = @{
        completed = ($failedChecks.Count -eq 0)
        checks = $results
        findings = @($findings | Select-Object -Unique)
        appPid = $AppPid
        dataDirectory = $dataPath
        server = 'isolated localhost Mattermost'
        sharedResultVerified = $true
        sessionPersisted = $true
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsPath -Encoding utf8
    if ($failedChecks.Count -gt 0) {
        $currentStep = 'check-results'
        throw "Native Mattermost UI E2E completed with $($failedChecks.Count) failed check(s)."
    }
    Write-Output 'PASS: Native Mattermost UI E2E completed; results contain no credentials.'
}
catch {
    $results.Add(@{ name = $currentStep; passed = $false; error = 'Step failed; detailed error withheld.' })
    $safeError = if ($_.Exception.Message -match "^winapp UI action failed at verb '[^']+', selector '[^']+'\.$") { $_.Exception.Message } else { 'Step failed; detailed error withheld.' }
    $result = @{
        completed = $false
        checks = $results
        findings = @($findings | Select-Object -Unique)
        errorType = $_.Exception.GetType().FullName
        errorSummary = $safeError
        appPid = $AppPid
        dataDirectory = $dataPath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsPath -Encoding utf8
    Write-Error "Native Mattermost UI E2E failed at '$currentStep' ($($_.Exception.GetType().Name)). See the sanitized result artifact." -ErrorAction Continue
    exit 1
}
