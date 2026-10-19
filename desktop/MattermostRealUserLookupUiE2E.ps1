param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$DataDir,
    [switch]$LookupOnly
)

$ErrorActionPreference = 'Stop'
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$fixturePath = Join-Path $artifactRoot 'mattermost-e2e.json'
$marker = Join-Path $dataPath '.cab-activity-test'
$resultDir = Join-Path $dataPath 'mattermost-real-user-lookup'
$resultPath = Join-Path $resultDir 'results.json'
$results = [Collections.Generic.List[object]]::new()
$currentStep = 'preflight'
$fixtureValidated = $false

function RAssert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Add-Pass([string]$Name) { $results.Add(@{ name = $Name; passed = $true }); Write-Output "PASS: $Name" }
function Add-Check([string]$Name, [bool]$Passed) { $results.Add(@{ name = $Name; passed = $Passed }) }
function Invoke-UI([string[]]$Arguments) {
    $null = winapp ui @Arguments -w $script:hwnd --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI action failed: $($Arguments[0]) $($Arguments[1])" }
}
function Ui-Value([string]$Id) { return [string](winapp ui get-value $Id -w $script:hwnd --json 2>$null | ConvertFrom-Json).text }
function RWait-Text([string]$Id, [string]$Text, [int]$Seconds = 8) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do { if ((Ui-Value $Id).Contains($Text)) { return }; Start-Sleep -Milliseconds 150 } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Expected UI text did not appear: $Id"
}
function Wait-Enabled([string]$Id, [bool]$Enabled, [int]$Seconds = 8) {
    $want = if ($Enabled) { 'True' } else { 'False' }
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $null = winapp ui wait-for $Id -p IsEnabled --value $want -t 500 -a $AppProcessId *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Enabled state did not settle for $Id"
}
function Expose-Button([string]$Id) {
    $scroll = Element 'MainContentScroll'
    $pattern = $scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $window = [Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    $minimumHeight = 30 * [ActivityLayoutNative]::GetDpiForWindow($script:hwnd) / 96.0
    for ($i = 0; $i -lt 60; $i++) {
        $button = Element $Id
        $r = $button.Current.BoundingRectangle
        $v = $scroll.Current.BoundingRectangle
        $bottom = [Math]::Min($v.Bottom, $window.Current.BoundingRectangle.Bottom - 12)
        if (-not $button.Current.IsOffscreen -and $r.Height -ge $minimumHeight -and $r.Top -ge $v.Top -and $r.Bottom -le $bottom) { return }
        if (-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100) { break }
        $pattern.SetScrollPercent(-1, [Math]::Min(100, $pattern.Current.VerticalScrollPercent + 2))
        Start-Sleep -Milliseconds 80
    }
    throw "Could not fully expose button $Id in the app viewport."
}
function Invoke-MM([string]$Method, [string]$Path, [string]$Token, $Body = $null) {
    $headers = @{ Authorization = "Bearer $Token" }
    $uri = $script:state.serverUrl.TrimEnd('/') + $Path
    try {
        if ($null -eq $Body) { return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 12 -ErrorAction Stop }
        $json = ConvertTo-Json -InputObject $Body -Depth 8 -Compress
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 12 -ErrorAction Stop
    } catch { throw "Mattermost API request failed for $Method $Path." }
}
function Get-ThreadPosts([string]$RootId, [string]$Token) {
    $thread = Invoke-MM 'Get' "/api/v4/posts/$([uri]::EscapeDataString($RootId))/thread" $Token
    if ($null -eq $thread.posts) { return @() }
    return @($thread.posts.PSObject.Properties | ForEach-Object { $_.Value })
}
function Wait-ThreadReply([string]$RootId, [string]$Token, [string]$MarkerText, [string]$ReplyKind, [int]$Seconds = 40) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        foreach ($post in (Get-ThreadPosts $RootId $Token)) {
            if ([string]$post.message -like "*claude-e2e: $ReplyKind;*" -and [string]$post.message -like "*$MarkerText*") { return $post }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $until)
    return $null
}
function Wait-BridgeReply([string]$RootId, [string]$Token, [string]$MarkerText, [int]$Seconds = 4) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        foreach ($post in (Get-ThreadPosts $RootId $Token)) {
            if ([string]$post.message -like '*claude-e2e:*' -and [string]$post.message -like "*$MarkerText*") { return $post }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $until)
    return $null
}
function Post-Request([string]$Token, [string]$ChannelId, [string]$MarkerText, [string]$RootId = '') {
    $body = @{ channel_id = $ChannelId; message = "!cab $MarkerText" }
    if ($RootId) { $body.root_id = $RootId }
    return Invoke-MM 'Post' '/api/v4/posts' $Token $body
}
function Wait-Conversation([string]$Prompt, [int]$Seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $treeText = winapp ui inspect InboxConversations -w $script:hwnd --json -d 10 2>$null
        if ($LASTEXITCODE -eq 0) {
            try {
                $tree = $treeText | ConvertFrom-Json
                $json = $tree | ConvertTo-Json -Depth 20 -Compress
                if ($json.Contains($Prompt)) { return $true }
            } catch { }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $false
}
function Select-Conversation([string]$Prompt) {
    if (-not (Wait-Conversation $Prompt)) { throw 'Incoming request did not appear in InboxConversations.' }
    $list = Element 'InboxConversations'
    $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
    foreach ($item in $items) {
        $matched = ([string]$item.Current.Name).Contains($Prompt)
        if (-not $matched) {
            $itemDescendants = $item.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            foreach ($descendant in $itemDescendants) {
                if (([string]$descendant.Current.Name).Contains($Prompt)) { $matched = $true; break }
            }
        }
        if ($matched) {
            $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return
        }
    }
    throw 'Could not select the matching InboxConversations ListView item.'
}
function Approve-Conversation([string]$Prompt) {
    Select-Conversation $Prompt
    Wait-Enabled 'ApproveChat' $true 8
    Invoke-UI @('invoke','ApproveChat')
    Invoke-UI @('wait-for','ChatRunConfirmation','-t','10000')
    Invoke-UI @('invoke','PrimaryButton')
}
function Connect-Chat {
    $null = winapp ui wait-for DisconnectChat -p IsEnabled --value True -t 1000 -a $AppProcessId *> $null
    if ($LASTEXITCODE -eq 0) { return }
    Wait-Enabled 'ConnectMattermost' $true 12
    Invoke-UI @('invoke','ConnectMattermost')
    RWait-Text 'ChatStatus' '接続しました' 40
}
function Disconnect-Chat {
    $null = winapp ui wait-for DisconnectChat -p IsEnabled --value True -t 1000 -a $AppProcessId *> $null
    if ($LASTEXITCODE -eq 0) { Invoke-UI @('invoke','DisconnectChat') }
    Wait-Enabled 'ConnectMattermost' $true 15
}
function Post-And-Approve([string]$Token, [string]$Prompt, [string]$ChannelId = $state.channel.id, [string]$RootId = '') {
    $post = Post-Request $Token $ChannelId $Prompt $RootId
    if (-not $post.id) { throw 'Mattermost did not return a post ID.' }
    if (-not $RootId) { $RootId = [string]$post.id }
    Approve-Conversation $Prompt
    return $RootId
}
function Count-Sessions {
    $path = Join-Path $dataPath 'sessions.json'
    if (-not (Test-Path $path)) { return 0 }
    return @((Get-Content $path -Raw | ConvertFrom-Json).Bindings).Count
}
function Get-ExecutionRecords {
    $path = Join-Path $dataPath 'history.json'
    if (-not (Test-Path $path)) { return @() }
    return @((Get-Content $path -Raw | ConvertFrom-Json).Records)
}
function Wait-ExecutionRecord([string]$RootId, [string]$UserId, [string]$ChannelId, [int]$MinimumCount, [int]$Seconds = 12) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $records = @(Get-ExecutionRecords)
        $match = @($records | Where-Object { $_.Source -eq 'mattermost' -and $_.ThreadId -eq $RootId -and $_.UserId -eq $UserId -and $_.ChannelId -eq $ChannelId -and $_.Status -eq 'completed' } | Sort-Object StartedAt | Select-Object -Last 1)
        if ($records.Count -ge $MinimumCount -and $match.Count) { return $match[0] }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw 'No completed Mattermost execution metadata was recorded for the expected thread.'
}
function Count-Audit {
    $path = Join-Path $dataPath 'audit.json'
    if (-not (Test-Path $path)) { return 0 }
    return @((Get-Content $path -Raw | ConvertFrom-Json).Records).Count
}

try {
    $currentStep = 'fixture-preflight'
    $artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    RAssert $dataPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) 'DataDir must stay below desktop/artifacts.'
    RAssert (Test-Path $marker) 'DataDir must be an isolated .cab-activity-test fixture.'
    $appProcess = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    RAssert ($null -ne $appProcess) 'AppProcessId is not running.'
    $layout = Split-Path (Split-Path (Get-Process -Id $AppProcessId).Path)
    [xml]$manifest = Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
    RAssert (([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.', [StringComparison]::Ordinal)) 'App must be an isolated Review package.'
    $windowDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $window = $null
    do {
        $windows = winapp ui list-windows -a $AppProcessId --json 2>$null | ConvertFrom-Json
        $window = $windows | Where-Object title -eq 'Chat Agent Bridge' | Select-Object -First 1
        if (-not $window) { Start-Sleep -Milliseconds 150 }
    } while (-not $window -and [DateTimeOffset]::UtcNow -lt $windowDeadline)
    RAssert ($null -ne $window) 'Review app window did not appear.'
    . "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $dataPath -Language ja -OnlyHelpers
    $script:hwnd = [IntPtr]$window.hwnd
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $script:state = Get-Content $fixturePath -Raw | ConvertFrom-Json
    RAssert ($state.serverUrl -eq 'http://127.0.0.1:8070' -and -not [string]::IsNullOrWhiteSpace([string]$state.channel.id) -and -not [string]::IsNullOrWhiteSpace([string]$state.otherChannel.id)) 'Mattermost state must target the exact isolated Docker fixture and its two channels.'
    RAssert (-not [string]::IsNullOrWhiteSpace([string]$state.memberUser.username) -and -not [string]::IsNullOrWhiteSpace([string]$state.memberUser.id) -and -not [string]::IsNullOrWhiteSpace([string]$state.unauthorizedUser.id) -and -not [string]::IsNullOrWhiteSpace([string]$state.bridgeUser.token)) 'Mattermost fixture participants are incomplete.'
    $settingsPath = Join-Path $dataPath 'settings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $uri = [uri]$settings.MattermostServerUrl
    RAssert ($uri.Scheme -eq 'http' -and $uri.Host -in @('127.0.0.1','localhost') -and $uri.Port -eq 8070 -and $settings.AutoConnectProvider -eq 'none') 'Settings must use localhost:8070 and AutoConnectProvider none.'
    $agentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/mattermost-ui-agent'))
    RAssert ($settings.AgentProvider -eq 'claude' -and $settings.AgentExecutable -eq (Join-Path $agentRoot 'claude.exe')) 'Use the isolated offline Claude fixture.'
    RAssert ((Get-FileHash $settings.AgentExecutable).Hash -eq (Get-FileHash (Join-Path $agentRoot 'ChatAgentBridge.TestAgent.exe')).Hash) 'Offline Claude fixture differs from TestAgent.'
    $fixtureValidated = $true
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    $settingsHashBefore = (Get-FileHash $settingsPath).Hash
    $historyPath = Join-Path $dataPath 'history.json'
    if (-not (Test-Path $historyPath)) { throw 'Isolated history.json is missing.' }

    $currentStep = 'real-username-lookup-add-save'
    Invoke-UI @('invoke','NavConnections')
    $allowedBox = Element 'MattermostAllowedUserIds'
    $readAllowed = [string]$allowedBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    RAssert ([string]::IsNullOrWhiteSpace($readAllowed)) 'Fixture must begin with an empty allowlist.'
    $username = [string]$state.memberUser.username
    $usernameInput = Element 'MattermostUsernameLookupInput'
    $usernameInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("@$username")
    Wait-Enabled 'LookupMattermostUserButton' $true 5
    Expose-Button 'LookupMattermostUserButton'
    Invoke-UI @('invoke','LookupMattermostUserButton')
    RWait-Text 'MattermostUsernameLookupResult' ([string]$state.memberUser.id) 15
    $readAllowed = [string](Element 'MattermostAllowedUserIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    RAssert ([string]::IsNullOrWhiteSpace($readAllowed)) 'Lookup changed the allowlist before explicit Add.'
    Wait-Enabled 'AddMattermostUserButton' $true 5
    Scroll-To 'MattermostUsernameLookupResult'; Capture 'real-user-lookup-result.png'
    Add-Pass 'Real username lookup resolved the expected Mattermost member ID without granting access.'
    $settingsHashAfterLookup = (Get-FileHash $settingsPath).Hash
    RAssert ($settingsHashAfterLookup -eq $settingsHashBefore) 'Lookup unexpectedly saved settings.'
    Expose-Button 'AddMattermostUserButton'; Invoke-UI @('invoke','AddMattermostUserButton')
    $readAllowed = [string](Element 'MattermostAllowedUserIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    RAssert ($readAllowed.Trim() -eq [string]$state.memberUser.id) 'Add did not insert exactly the returned internal ID.'
    Expose-Button 'AddMattermostUserButton'; Invoke-UI @('invoke','AddMattermostUserButton')
    $readAllowed = [string](Element 'MattermostAllowedUserIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    RAssert (@($readAllowed -split '[\s,;]+' | Where-Object { $_ -eq [string]$state.memberUser.id }).Count -eq 1) 'Repeated Add duplicated the resolved ID.'
    RAssert ((Get-FileHash $settingsPath).Hash -eq $settingsHashBefore) 'Add unexpectedly saved settings.'
    Expose-Button 'AddMattermostUserButton'; Capture 'real-user-lookup-added.png'
    Add-Pass 'Explicit Add inserted one internal ID, remained idempotent on repetition, and left persisted settings unchanged.'
    Invoke-UI @('invoke','SaveConnections')
    RWait-Text 'Title' '設定を保存しました' 8
    $saved = Get-Content $settingsPath -Raw | ConvertFrom-Json
    RAssert ($saved.MattermostAllowedUserIds -eq [string]$state.memberUser.id) 'SaveConnections did not persist the resolved ID.'
    RAssert ([string]$saved.MattermostToken -like 'dpapi:*') 'Mattermost token is not DPAPI protected.'
    RAssert ([string]::IsNullOrEmpty($saved.SlackBotToken) -and [string]::IsNullOrEmpty($saved.SlackAppToken)) 'Slack credential fields must remain empty.'
    Add-Pass 'SaveConnections persisted the internal ID and preserved the DPAPI-protected Mattermost token.'

    if ($LookupOnly) {
        $currentStep = 'username-phase-connect-reconnect'
        Invoke-UI @('invoke','NavChat'); Connect-Chat; Disconnect-Chat; Connect-Chat
        Add-Pass 'Saved username allowlist connected, disconnected, and reconnected through the native UI.'
        $result = @{ completed = $true; checks = @($results); appPid = $AppProcessId; dataDirectory = $dataPath; server = 'isolated localhost Mattermost'; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
        Write-Output 'PASS: Native real Mattermost username lookup/add/save/reconnect phase completed.'
        return
    }

    $currentStep = 'initial-connection-and-approval'
    Invoke-UI @('invoke','NavChat')
    Connect-Chat
    Capture 'real-user-connected.png'
    Add-Pass 'Mattermost connected through the native WinUI control.'
    $firstPrompt = 'real-user-lookup-' + [guid]::NewGuid().ToString('N').Substring(0,12)
    $historyCountBeforeFirst = @(Get-ExecutionRecords).Count
    $rootId = Post-And-Approve $state.memberUser.token $firstPrompt
    $firstReply = Wait-ThreadReply $rootId $state.memberUser.token $firstPrompt 'new' 45
    RAssert ($null -ne $firstReply) 'No offline TestAgent new-session reply appeared in the real Mattermost thread.'
    $firstRecord = Wait-ExecutionRecord $rootId $state.memberUser.id $state.channel.id ($historyCountBeforeFirst + 1) 15
    $sessionStorePath = Join-Path $dataPath 'sessions.json'
    RAssert (Test-Path $sessionStorePath) 'Accepted conversation did not persist its session binding.'
    $binding = @((Get-Content $sessionStorePath -Raw | ConvertFrom-Json).Bindings | Where-Object { $_.Key.Provider -eq 'mattermost' -and $_.Key.UserId -eq $state.memberUser.id -and $_.Key.ChannelId -eq $state.channel.id -and $_.Key.ThreadId -eq $rootId } | Select-Object -First 1)[0]
    RAssert ($binding -and $binding.SessionId) 'Accepted thread binding was not found.'
    $firstSessionId = [string]$binding.SessionId
    RAssert ([string]$firstRecord.SessionId -eq $firstSessionId) 'Execution history and saved thread binding do not share a session.'
    Capture 'real-user-first-approved-reply.png'
    Add-Pass 'First allowlisted request required local approval, ran the offline Claude fixture, replied in Mattermost, and persisted a session.'

    $currentStep = 'same-thread-consent-resume'
    $followPrompt = 'real-user-followup-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $historyCountBeforeFollow = @(Get-ExecutionRecords).Count
    $null = Post-Request $state.memberUser.token $state.channel.id $followPrompt $rootId
    $followReply = Wait-ThreadReply $rootId $state.memberUser.token $followPrompt 'resumed' 40
    RAssert ($null -ne $followReply) 'Same-thread follow-up did not resume automatically with first-only consent.'
    $followRecord = Wait-ExecutionRecord $rootId $state.memberUser.id $state.channel.id ($historyCountBeforeFollow + 1) 15
    $followBinding = @((Get-Content $sessionStorePath -Raw | ConvertFrom-Json).Bindings | Where-Object { $_.Key.Provider -eq 'mattermost' -and $_.Key.UserId -eq $state.memberUser.id -and $_.Key.ChannelId -eq $state.channel.id -and $_.Key.ThreadId -eq $rootId } | Select-Object -First 1)[0]
    RAssert ([string]$followBinding.SessionId -eq $firstSessionId) 'Same-thread follow-up created a different session.'
    RAssert ([string]$followRecord.SessionId -eq $firstSessionId) 'Same-thread follow-up history did not preserve the original session.'
    Add-Pass 'Same-thread reply resumed the same offline session without another local approval.'

    $currentStep = 'unauthorized-and-wrong-channel-rejection'
    $sessionsBeforeDenied = Count-Sessions; $historyCountBeforeDenied = @(Get-ExecutionRecords).Count; $auditBeforeDenied = Count-Audit
    $outsiderPrompt = 'real-user-outsider-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $null = Post-Request $state.unauthorizedUser.token $state.channel.id $outsiderPrompt $rootId
    Start-Sleep -Seconds 3
    RAssert ($null -eq (Wait-BridgeReply $rootId $state.memberUser.token $outsiderPrompt)) 'Unauthorized same-channel actor triggered a bridge reply.'
    RAssert (@(Get-ExecutionRecords).Count -eq $historyCountBeforeDenied) 'Unauthorized same-channel actor entered execution history.'
    $wrongChannelPrompt = 'real-user-wrong-channel-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $wrongChannelPost = Post-Request $state.memberUser.token $state.otherChannel.id $wrongChannelPrompt
    Start-Sleep -Seconds 3
    RAssert ($null -eq (Wait-BridgeReply ([string]$wrongChannelPost.id) $state.memberUser.token $wrongChannelPrompt)) 'Wrong-channel post triggered a bridge reply.'
    RAssert ((Count-Sessions) -eq $sessionsBeforeDenied) 'Denied actor or wrong-channel post created a session binding.'
    $historyCountAfterDenied = @(Get-ExecutionRecords).Count
    RAssert ($historyCountAfterDenied -eq $historyCountBeforeDenied) 'Denied actor or wrong-channel post changed execution history.'
    Add-Check 'Denied posts did not add audit activity' ((Count-Audit) -eq $auditBeforeDenied)
    Add-Pass 'Unauthorized same-channel actor and allowlisted wrong-channel request were rejected without history or session creation.'

    $currentStep = 'disconnect-reconnect-keeps-session-consent'
    Disconnect-Chat; Connect-Chat
    $afterReconnectPrompt = 'real-user-reconnect-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $historyCountBeforeReconnect = @(Get-ExecutionRecords).Count
    $null = Post-Request $state.memberUser.token $state.channel.id $afterReconnectPrompt $rootId
    $reconnectReply = Wait-ThreadReply $rootId $state.memberUser.token $afterReconnectPrompt 'resumed' 40
    RAssert ($null -ne $reconnectReply) 'Saved allowlist did not keep same-thread first-only consent through disconnect/reconnect.'
    $reconnectRecord = Wait-ExecutionRecord $rootId $state.memberUser.id $state.channel.id ($historyCountBeforeReconnect + 1) 15
    RAssert ([string]$reconnectRecord.SessionId -eq $firstSessionId) 'Reconnect follow-up created a different execution session.'
    Add-Pass 'Disconnect and reconnect preserved saved authorization and resumed the same approved thread.'

    $currentStep = 'remove-member-reconnect-denies'
    Invoke-UI @('invoke','NavConnections')
    Scroll-To 'MattermostAllowedUserIds'
    (Element 'MattermostAllowedUserIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue([string]$state.unauthorizedUser.id)
    Invoke-UI @('invoke','SaveConnections'); RWait-Text 'Title' '設定を保存しました' 8
    Invoke-UI @('invoke','NavChat'); Disconnect-Chat; Connect-Chat
    $removedPrompt = 'real-user-removed-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $removedSessions = Count-Sessions; $removedHistory = @(Get-ExecutionRecords).Count
    $null = Post-Request $state.memberUser.token $state.channel.id $removedPrompt $rootId
    Start-Sleep -Seconds 3
    RAssert ($null -eq (Wait-BridgeReply $rootId $state.memberUser.token $removedPrompt)) 'Removed member triggered a bridge reply.'
    RAssert (@(Get-ExecutionRecords).Count -eq $removedHistory) 'Removed member continued to execute a thread request.'
    RAssert ((Count-Sessions) -eq $removedSessions) 'Removed member created a new session binding.'
    Add-Pass 'After removal and reconnect, the original member could not continue the saved thread.'

    $currentStep = 'restore-member-through-lookup'
    Invoke-UI @('invoke','NavConnections')
    $input = Element 'MattermostUsernameLookupInput'
    $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("@$username")
    Expose-Button 'LookupMattermostUserButton'; Invoke-UI @('invoke','LookupMattermostUserButton')
    RWait-Text 'MattermostUsernameLookupResult' ([string]$state.memberUser.id) 15
    Expose-Button 'AddMattermostUserButton'; Invoke-UI @('invoke','AddMattermostUserButton')
    RAssert ((Ui-Value 'MattermostAllowedUserIds').Trim() -match [regex]::Escape([string]$state.memberUser.id)) 'Restoring lookup did not add the resolved member ID.'
    Invoke-UI @('invoke','SaveConnections'); RWait-Text 'Title' '設定を保存しました' 8
    Invoke-UI @('invoke','NavChat'); Disconnect-Chat; Connect-Chat
    $restoredPrompt = 'real-user-restored-' + [guid]::NewGuid().ToString('N').Substring(0,10)
    $historyCountBeforeRestore = @(Get-ExecutionRecords).Count
    $restoredRoot = Post-And-Approve $state.memberUser.token $restoredPrompt
    $restoredReply = Wait-ThreadReply $restoredRoot $state.memberUser.token $restoredPrompt 'new' 45
    RAssert ($null -ne $restoredReply) 'Restored member request did not execute and reply.'
    $restoredRecord = Wait-ExecutionRecord $restoredRoot $state.memberUser.id $state.channel.id ($historyCountBeforeRestore + 1) 15
    RAssert ([string]$restoredRecord.SessionId -ne $firstSessionId) 'A new root inherited the previous thread session.'
    Add-Pass 'Username lookup restored the member ID; SaveConnections and reconnect re-enabled requests.'

    $failed = @($results | Where-Object { -not $_.passed })
    $result = @{ completed = ($failed.Count -eq 0); checks = @($results); appPid = $AppProcessId; dataDirectory = $dataPath; server = 'isolated localhost Mattermost'; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
    if ($failed.Count) { throw "E2E completed with $($failed.Count) failed check(s)." }
    Write-Output 'PASS: Native real Mattermost username lookup, allowlist, save, connection, and reconnect E2E completed.'
}
catch {
    $results.Add(@{ name = $currentStep; passed = $false; error = 'Step failed; detailed error withheld.' })
    $result = @{ completed = $false; checks = @($results); errorType = $_.Exception.GetType().FullName; appPid = $AppProcessId; dataDirectory = $dataPath; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Error "Mattermost real username UI E2E failed at '$currentStep' ($($_.Exception.GetType().Name)). See sanitized results." -ErrorAction Continue
    exit 1
}
finally {
    if ($fixtureValidated) {
        try { Disconnect-Chat } catch { Write-Warning 'Could not disconnect the isolated Review app.' }
    }
}
