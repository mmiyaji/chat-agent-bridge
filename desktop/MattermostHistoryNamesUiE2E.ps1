param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$FixturePath = (Join-Path $PSScriptRoot 'artifacts/mattermost-e2e.json'),
    [string]$NodeExecutable = 'node'
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$fixtureFile = [IO.Path]::GetFullPath($FixturePath)
$dataPath = Join-Path $artifactRoot ('history-names-real-' + [guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[object]]::new()
$appProcess = $null
$proxyProcess = $null
$currentStep = 'initialization'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction SilentlyContinue

function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Add-Pass([string]$Name) { $checks.Add(@{ name = $Name; passed = $true }); Write-Output "PASS $Name" }
function Find-Element([string]$Id) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Ui([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $appProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed ($($Arguments -join ' '))." }
    return $output
}
function Wait-Element([string]$Id, [int]$Seconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($null -ne (Find-Element $Id)) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for UI element $Id."
}
function Has-Text([string]$Text) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $itemCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $itemCondition)
    foreach ($item in $items) {
        if ($item.Current.Name.Contains($Text)) { return $true }
        $textCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)
        $children = $item.FindAll([Windows.Automation.TreeScope]::Descendants, $textCondition)
        foreach ($child in $children) { if ($child.Current.Name.Contains($Text)) { return $true } }
    }
    return $false
}
function Wait-Text([string]$Text, [int]$Seconds = 20) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Has-Text $Text) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for expected visible name '$Text'."
}
function Wait-VisibleCount([int]$Visible, [int]$Total, [int]$Seconds = 10) {
    $wanted = "会話 $Total 件中 $Visible 件を表示"
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element 'SavedSessionsCount'
        if ($element -and $element.Current.Name -eq $wanted) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "History search count did not become '$wanted'."
}
function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath)
    $script:appProcess = [Diagnostics.Process]::Start($start)
    Ui @('wait-for', 'NavHistory', '-t', '20000') | Out-Null
    Ui @('invoke', 'NavHistory') | Out-Null
    Ui @('wait-for', 'SavedSessionsList', '-t', '10000') | Out-Null
}
function Stop-TestApp {
    if ($script:appProcess -and -not $script:appProcess.HasExited) {
        Stop-Process -Id $script:appProcess.Id -Force
        $script:appProcess.WaitForExit(5000) | Out-Null
    }
}
function Proxy-Control([string]$Path, [string]$Method = 'GET') {
    $uri = "http://127.0.0.1:$script:proxyPort$Path"
    if ($Method -eq 'GET') { return Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 5 }
    return Invoke-WebRequest -Method $Method -Uri $uri -TimeoutSec 5 -UseBasicParsing
}
function Get-ProxyState { return Proxy-Control '/__control/state' }
function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8
}
function Get-Cache {
    $raw = Get-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Raw
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { return $raw | ConvertFrom-Json -DateKind String }
    return $raw | ConvertFrom-Json
}
function Get-CacheEntry($Cache, [string]$Kind, [string]$Id) {
    return @($Cache.entries | Where-Object { $_.key.provider -eq 'mattermost' -and $_.key.origin -eq $script:originHash -and $_.key.kind -eq $Kind -and $_.key.id -eq $Id } | Select-Object -First 1)[0]
}
function Capture-CacheTimes {
    $cache = Get-Cache
    $user = Get-CacheEntry $cache 'user' $script:memberId
    $channel = Get-CacheEntry $cache 'channel' $script:channelId
    $site = Get-CacheEntry $cache 'site' 'site'
    Assert-True ($null -ne $user -and $null -ne $channel -and $null -ne $site) 'Expected scoped user, channel, and site cache records.'
    return @{ user = [string]$user.checkedAtUtc; channel = [string]$channel.checkedAtUtc; site = [string]$site.checkedAtUtc }
}
function Capture-UI([string]$Name) {
    Ui @('screenshot', '-w', "$($appProcess.MainWindowHandle)", '-o', (Join-Path $dataPath $Name)) | Out-Null
}
function Start-Proxy {
    $nodeScript = Join-Path $PSScriptRoot 'fixtures/history-names-proxy.cjs'
    $portFile = Join-Path $dataPath 'proxy-port.txt'
    $start = [Diagnostics.ProcessStartInfo]::new($NodeExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add($nodeScript); $start.ArgumentList.Add($portFile)
    $script:proxyProcess = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (Test-Path -LiteralPath $portFile) { break }
        if ($proxyProcess.HasExited) { throw 'The loopback Mattermost proxy exited during startup.' }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-True (Test-Path -LiteralPath $portFile) 'The loopback Mattermost proxy did not become ready.'
    $script:proxyPort = [int](Get-Content -LiteralPath $portFile -Raw)
    Assert-True ($proxyPort -gt 0 -and $proxyPort -lt 65536) 'The proxy did not bind a valid dynamic port.'
}

try {
    $currentStep = 'isolated-fixture-validation'
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-True ($appRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be inside desktop/artifacts.'
    Assert-True (-not (Test-Path (Join-Path $appRoot 'AppxManifest.xml'))) 'AppDirectory must be an unpackaged portable payload.'
    Assert-True (Test-Path -LiteralPath $fixtureFile) 'Mattermost Docker fixture file is missing.'
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null

    # Read only the needed fields. Never emit the fixture or credential values.
    $state = Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
    $upstream = [uri]$state.serverUrl
    Assert-True ($upstream.Scheme -eq 'http' -and $upstream.Host -in @('127.0.0.1', 'localhost') -and $upstream.Port -eq 8070) 'Fixture server must be localhost:8070.'
    $script:memberId = [string]$state.memberUser.id
    $script:username = [string]$state.memberUser.username
    $script:channelId = [string]$state.channel.id
    $script:botToken = [string]$state.bridgeUser.token
    $protectedFixtureToken = [string]$state.bridgeUser.protectedToken
    if ($protectedFixtureToken.StartsWith('dpapi:', [StringComparison]::Ordinal)) {
        $encryptedBytes = [Convert]::FromBase64String($protectedFixtureToken.Substring(6))
        $script:botToken = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($encryptedBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    }
    Assert-True ($memberId -and $username -and $channelId -and $botToken) 'Mattermost fixture participant data is incomplete.'

    $currentStep = 'direct-user-read-only-preflight'
    $headers = @{ Authorization = "Bearer $botToken"; Accept = 'application/json' }
    $directUser = Invoke-RestMethod -Method Get -Uri ($upstream.AbsoluteUri.TrimEnd('/') + "/api/v4/users/$memberId") -Headers $headers -TimeoutSec 10
    $currentStep = 'direct-channel-read-only-preflight'
    $directChannel = Invoke-RestMethod -Method Get -Uri ($upstream.AbsoluteUri.TrimEnd('/') + "/api/v4/channels/$channelId") -Headers $headers -TimeoutSec 10
    $currentStep = 'direct-config-read-only-preflight'
    $directConfig = Invoke-RestMethod -Method Get -Uri ($upstream.AbsoluteUri.TrimEnd('/') + '/api/v4/config/client?format=old') -Headers $headers -TimeoutSec 10
    $currentStep = 'direct-user-field-check'
    Assert-True ([string]$directUser.username -eq $username) 'Direct Mattermost user lookup did not match the fixture member.'
    $currentStep = 'direct-channel-field-check'
    Assert-True ([string]$directChannel.id -eq $channelId -and -not [string]::IsNullOrWhiteSpace([string]$directChannel.display_name)) 'Direct Mattermost channel lookup did not match the fixture channel.'
    $currentStep = 'direct-site-name-field-check'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$directConfig.SiteName)) 'Direct Mattermost client config has no SiteName.'
    $personName = [string]$directUser.nickname
    if ([string]::IsNullOrWhiteSpace($personName)) {
        $personParts = @([string]$directUser.first_name, [string]$directUser.last_name)
        $personName = (@($personParts | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' ')
    }
    $expectedUser = '@' + $username
    if (-not [string]::IsNullOrWhiteSpace($personName) -and $personName -ne $username) { $expectedUser += " ($personName)" }
    $expectedChannel = [string]$directChannel.display_name
    $expectedSite = [string]$directConfig.SiteName
    $script:upstreamRoot = $upstream.AbsoluteUri.TrimEnd('/')

    $currentStep = 'start-loopback-readonly-proxy'
    Start-Proxy
    $proxyUrl = "http://127.0.0.1:$proxyPort"
    # URL must use same canonical form as ChatSessionScope (scheme://host:port/).
    $script:originHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("http://127.0.0.1:$proxyPort/"))).ToLowerInvariant()
    $protectedToken = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($botToken), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $binding = @{
        Key = @{ Provider = 'mattermost'; ChannelId = $channelId; ThreadId = 'history-name-fixture-thread'; UserId = $memberId; Origin = $originHash }
        WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; SessionId = [guid]::NewGuid().ToString('N')
        UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o'); Model = $null; ReasoningEffort = $null; DisplayName = 'History names real fixture'
    }
    $decoyBinding = @{
        Key = @{ Provider = 'local'; ChannelId = 'local-history'; ThreadId = 'unrelated-local-thread'; UserId = $null; Origin = $null }
        WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; SessionId = [guid]::NewGuid().ToString('N')
        UpdatedAt = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o'); DisplayName = 'Unrelated local history'
    }
    Write-Json 'sessions.json' @{ SchemaVersion = 1; Bindings = @($binding, $decoyBinding); Workspaces = @() }
    Write-Json 'settings.json' @{
        SchemaVersion = 1; WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; AgentMode = 'exec'; AgentProvider = 'custom'
        AutoConnectProvider = 'none'; MattermostServerUrl = $proxyUrl; MattermostChannelId = $channelId; MattermostToken = $protectedToken
        MattermostAllowedUserIds = $memberId; MattermostBypassProxy = $true; SlackBotToken = ''; SlackAppToken = ''
        TrustedWorkspaceRoots = $dataPath; AgentPermissionMode = 'inherit'; ChatApprovalMode = 'first'; ChatResultMode = 'manual'
    }
    Write-Json 'onboarding.json' @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    Write-Json 'language.json' @{ Language = 'ja' }
    Write-Json 'appearance.json' @{ Theme = 'dark' }

    $currentStep = 'first-real-name-resolution'
    Start-App
    Wait-Text $expectedUser
    Wait-Text $expectedChannel
    Wait-Text $expectedSite
    $list = Find-Element 'SavedSessionsList'
    Assert-True ($null -ne $list) 'History conversation list was not found.'
    $itemCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $row = $list.FindFirst([Windows.Automation.TreeScope]::Descendants, $itemCondition)
    Assert-True ($null -ne $row) 'History conversation list contains no row.'
    $texts = $row.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))
    $rowText = $row.Current.Name + ' ' + (@($texts | ForEach-Object { $_.Current.Name }) -join ' ')
    Assert-True ($rowText.Contains($expectedUser) -and $rowText.Contains($expectedChannel) -and $rowText.Contains("$expectedSite")) 'The conversation row does not show the real Mattermost user, channel, and site names.'
    Assert-True (-not $rowText.Contains($memberId) -and -not $rowText.Contains($channelId)) 'Opaque Mattermost IDs leaked into the conversation row.'
    $initialState = Get-ProxyState
    $initialRequests = @($initialState.requests)
    Assert-True ($initialRequests.Count -eq 3) 'Initial name resolution did not make exactly one config, user, and channel GET.'
    Assert-True (@($initialRequests | Where-Object { $_.path -eq "/api/v4/users/$memberId" -and $_.status -eq 200 }).Count -eq 1) 'Initial resolution did not fetch the fixture member successfully.'
    Assert-True (@($initialRequests | Where-Object { $_.path -eq "/api/v4/channels/$channelId" -and $_.status -eq 200 }).Count -eq 1) 'Initial resolution did not fetch the fixture channel successfully.'
    Assert-True (@($initialRequests | Where-Object { $_.path -eq '/api/v4/config/client?format=old' -and $_.status -eq 200 }).Count -eq 1) 'Initial resolution did not fetch Mattermost client config successfully.'
    Capture-UI 'history-names-initial.png'
    $search = Find-Element 'SavedSessionsSearch'
    $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($expectedUser)
    Wait-VisibleCount 1 2
    $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($expectedSite)
    Wait-VisibleCount 1 2
    Add-Pass 'Real Mattermost member, channel, and configured site names appear and are searchable without exposing IDs.'

    $currentStep = 'initial-cache-scope-and-secrets'
    $cache = Get-Cache
    $times = Capture-CacheTimes
    Assert-True ([string]$cache.version -eq '1') 'Unexpected chat identity cache version.'
    $entriesForOrigin = @($cache.entries | Where-Object { $_.key.provider -eq 'mattermost' -and $_.key.origin -eq $originHash })
    Assert-True ($entriesForOrigin.Count -eq 3) 'Cache should contain exactly scoped user, channel, and site entries.'
    Assert-True ((Get-CacheEntry $cache 'user' $memberId).userName -eq $expectedUser) 'Cached user display name differs from real Mattermost data.'
    Assert-True ((Get-CacheEntry $cache 'channel' $channelId).channelName -eq $expectedChannel) 'Cached channel display name differs from real Mattermost data.'
    Assert-True ((Get-CacheEntry $cache 'site' 'site').siteName -eq $expectedSite) 'Cached site display name differs from Mattermost config.SiteName.'
    $cacheRaw = Get-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Raw
    Assert-True (-not $cacheRaw.Contains($botToken) -and $cacheRaw -notmatch '(?i)"(?:password|email|token|body|content)"\s*:') 'Identity cache contains a credential or unrelated private field.'
    $settingsRaw = Get-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Raw
    Assert-True ($settingsRaw.Contains('dpapi:') -and -not $settingsRaw.Contains($botToken)) 'Mattermost token is not DPAPI-protected in isolated settings.'
    Add-Pass 'Cache contains only the matching scoped identity records, stores the actual configured SiteName, and keeps credentials protected.'

    $currentStep = 'restart-offline-cache-reuse'
    Stop-TestApp
    Proxy-Control '/__control/clear' 'POST' | Out-Null
    Proxy-Control '/__control/offline' 'POST' | Out-Null
    Start-App
    Wait-Text $expectedUser
    Wait-Text $expectedChannel
    Wait-Text $expectedSite
    Start-Sleep -Seconds 2
    Ui @('invoke', 'RefreshHistory') | Out-Null
    Start-Sleep -Seconds 2
    Capture-UI 'history-names-offline-restart.png'
    $offlineState = Get-ProxyState
    Assert-True (@($offlineState.requests).Count -eq 0) 'Restart made name API requests instead of using cached labels.'
    $timesAfterRestart = Capture-CacheTimes
    Assert-True ($timesAfterRestart.user -eq $times.user -and $timesAfterRestart.channel -eq $times.channel -and $timesAfterRestart.site -eq $times.site) 'Restart changed cached identity timestamps while offline.'
    Add-Pass 'Restart showed cached names while offline and made zero outbound Mattermost name requests.'

    $currentStep = 'expire-one-user-cache-entry'
    Stop-TestApp
    $cache = Get-Cache
    $userEntry = Get-CacheEntry $cache 'user' $memberId
    $userEntry.checkedAtUtc = [DateTimeOffset]::UtcNow.AddDays(-2).ToString('o')
    $expiredAt = [string]$userEntry.checkedAtUtc
    $cache | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Encoding utf8
    Proxy-Control '/__control/online' 'POST' | Out-Null
    Proxy-Control '/__control/clear' 'POST' | Out-Null
    Start-App
    Wait-Text $expectedUser
    Wait-Text $expectedChannel
    Wait-Text $expectedSite
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $newTimes = Capture-CacheTimes
        if ($newTimes.user -ne $expiredAt) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-True ($newTimes.user -ne $expiredAt) 'Expired user cache timestamp did not change after online refresh.'
    Start-Sleep -Seconds 1
    Capture-UI 'history-names-user-refreshed.png'
    $afterRefresh = Get-ProxyState
    $nameRequests = @($afterRefresh.requests | Where-Object { $_.path -match '^/api/v4/(users|channels)/' })
    Assert-True (@($afterRefresh.requests).Count -eq 1 -and $nameRequests.Count -eq 1 -and $nameRequests[0].path -eq "/api/v4/users/$memberId" -and $nameRequests[0].status -eq 200) 'Expiring only the user entry did not cause exactly one successful user lookup.'
    Assert-True ($newTimes.channel -eq $times.channel -and $newTimes.site -eq $times.site) 'Refreshing the user entry changed channel or site timestamps.'
    Add-Pass 'Expiring one user entry caused exactly one user API lookup and preserved fresh channel/site cache timestamps.'

    $result = @{ passed = $true; checks = @($checks); appProcessId = $appProcess.Id; dataDirectory = $dataPath; upstream = 'isolated localhost Mattermost fixture'; proxy = 'loopback read-only API allowlist'; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataPath 'result.json') -Encoding utf8
    Write-Output "PASS Mattermost history names E2E completed: $dataPath"
}
catch {
    $safeResult = @{ passed = $false; step = $currentStep; errorType = $_.Exception.GetType().FullName; checks = @($checks); dataDirectory = $dataPath; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    try { $safeResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataPath 'result.json') -Encoding utf8 } catch { }
    Write-Error "Mattermost history names E2E failed at '$currentStep' ($($_.Exception.GetType().Name)); see sanitized result.json." -ErrorAction Continue
    exit 1
}
finally {
    Stop-TestApp
    if ($proxyProcess -and -not $proxyProcess.HasExited) {
        Stop-Process -Id $proxyProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
