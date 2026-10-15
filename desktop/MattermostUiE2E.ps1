param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [System.IO.Path]::GetFullPath($DataDirectory)
$fixturePath = Join-Path $artifactRoot 'mattermost-e2e.json'
$resultsPath = Join-Path $artifactRoot 'mattermost-ui-e2e-results.json'
$markerName = '.cab-mattermost-ui-e2e'
$results = [System.Collections.Generic.List[object]]::new()
$currentStep = 'preflight'

function Assert-UnderArtifactRoot([string]$Path) {
    $prefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The UI E2E data directory must be a child of desktop/artifacts.'
    }
}

function Invoke-UI([string[]]$Arguments) {
    & winapp ui @Arguments -a $AppPid *> $null
    if ($LASTEXITCODE -ne 0) { throw "winapp UI action failed at $($Arguments[0])." }
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

function Get-ThreadPosts([string]$RootPostId) {
    $thread = Invoke-MM 'Get' "/api/v4/posts/$([uri]::EscapeDataString($RootPostId))/thread" $state.memberUser.token
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

function Ensure-TestAgentHost {
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

    $result = @{
        completed = $true
        checks = $results
        appPid = $AppPid
        dataDirectory = $dataPath
        server = 'isolated localhost Mattermost'
        sharedResultVerified = $true
        sessionPersisted = $true
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsPath -Encoding utf8
    Write-Output 'PASS: Native Mattermost UI E2E completed; results contain no credentials.'
}
catch {
    $results.Add(@{ name = $currentStep; passed = $false; error = 'Step failed; detailed error withheld.' })
    $result = @{
        completed = $false
        checks = $results
        appPid = $AppPid
        dataDirectory = $dataPath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsPath -Encoding utf8
    Write-Error "Native Mattermost UI E2E failed at '$currentStep'. See the sanitized result artifact." -ErrorAction Continue
    exit 1
}
