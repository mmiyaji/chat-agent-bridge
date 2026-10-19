param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$StatePath = (Join-Path $PSScriptRoot 'artifacts/current-slack-lookup-live.json'),
    [string]$ProbeProject = (Join-Path $PSScriptRoot 'test-support/SlackHistoryNamesProbe/SlackHistoryNamesProbe.csproj'),
    [string]$DotnetExecutable = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$stateFile = [IO.Path]::GetFullPath($StatePath)
$probeProjectFile = [IO.Path]::GetFullPath($ProbeProject)
$dataPath = Join-Path $artifactRoot ('slack-history-names-real-' + [guid]::NewGuid().ToString('N'))
$probeCachePath = Join-Path $dataPath 'probe-cache'
$checks = [Collections.Generic.List[object]]::new()
$appProcess = $null
$currentStep = 'initialization'
$sourceHashesBefore = $null
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
    throw 'Timed out waiting for an expected visible identity label.'
}
function Wait-VisibleCount([int]$Visible, [int]$Total, [int]$Seconds = 10) {
    $wanted = "会話 $Total 件中 $Visible 件を表示"
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element 'SavedSessionsCount'
        if ($element -and $element.Current.Name -eq $wanted) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw 'History search did not show the expected matching conversation count.'
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
function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8
}
function Get-Cache {
    $raw = Get-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Raw
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { return $raw | ConvertFrom-Json -DateKind String }
    return $raw | ConvertFrom-Json
}
function Get-CacheEntry($Cache, [string]$Kind, [string]$Id) {
    return @($Cache.entries | Where-Object { $_.key.provider -eq 'slack' -and $_.key.origin -eq 'slack' -and $_.key.kind -eq $Kind -and $_.key.id -eq $Id } | Select-Object -First 1)[0]
}
function Capture-CacheTimes {
    $cache = Get-Cache
    $user = Get-CacheEntry $cache 'user' $script:userId
    $channel = Get-CacheEntry $cache 'channel' $script:channelId
    Assert-True ($null -ne $user -and $null -ne $channel) 'Expected scoped Slack user and channel cache records.'
    return @{ user = [string]$user.checkedAtUtc; channel = [string]$channel.checkedAtUtc }
}
function Get-RowText {
    $list = Find-Element 'SavedSessionsList'
    Assert-True ($null -ne $list) 'History conversation list was not found.'
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $row = $list.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    Assert-True ($null -ne $row) 'History conversation list contains no row.'
    $texts = $row.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))
    return $row.Current.Name + ' ' + (@($texts | ForEach-Object { $_.Current.Name }) -join ' ')
}
function Capture-UI([string]$Name) {
    Ui @('screenshot', '-w', "$($appProcess.MainWindowHandle)", '-o', (Join-Path $dataPath $Name)) | Out-Null
}
function Get-SourceHashes([string]$Directory) {
    $hashes = @{}
    foreach ($name in @('settings.json', 'sessions.json', 'history.json', 'audit.json', 'chat-identities.json')) {
        $path = Join-Path $Directory $name
        $hashes[$name] = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { '<missing>' }
    }
    return $hashes
}

try {
    $currentStep = 'isolated-path-and-state-validation'
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-True ($appRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be inside desktop/artifacts.'
    Assert-True (-not (Test-Path (Join-Path $appRoot 'AppxManifest.xml'))) 'AppDirectory must be an unpackaged portable payload.'
    Assert-True (Test-Path -LiteralPath $stateFile) 'Real Slack lookup state file is missing.'
    Assert-True (Test-Path -LiteralPath $probeProjectFile) 'Slack live probe project is missing.'
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null
    [IO.Directory]::CreateDirectory($probeCachePath) | Out-Null

    # Read only approved metadata and encrypted settings; never emit the state or credentials.
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $normalDataDirectory = [string]$state.NormalDataDirectory
    $script:userId = [string]$state.ExpectedUserId
    $script:channelId = [string]$state.ChannelId
    $workspaceUrl = [string]$state.WorkspaceUrl
    $teamId = [string]$state.TeamId
    $workspaceUri = [uri]$workspaceUrl
    Assert-True ($normalDataDirectory -and $userId -match '^[UW][A-Z0-9]{8,31}$' -and $channelId -match '^[CGD][A-Z0-9]{8,31}$' -and $teamId -match '^T[A-Z0-9]{8,}$') 'Slack lookup state metadata is incomplete.'
    Assert-True ($workspaceUri.Scheme -eq 'https' -and $workspaceUri.Host -match '(^|\.)slack\.com$') 'Workspace URL must identify a Slack HTTPS workspace.'
    Assert-True (Test-Path -LiteralPath (Join-Path $normalDataDirectory 'settings.json') -PathType Leaf) 'The source settings file is missing.'
    $sourceHashesBefore = Get-SourceHashes $normalDataDirectory

    $currentStep = 'allowlisted-production-directory-probe'
    $probeOutput = & $DotnetExecutable run --project $probeProjectFile --configuration Release --verbosity quiet -- $normalDataDirectory $userId $channelId $probeCachePath 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Slack identity probe failed; sanitized console output was not retained.' }
    $probe = ($probeOutput -join "`n") | ConvertFrom-Json
    Write-Json 'probe-result.json' $probe
    Assert-True ($probe.passed -eq $true) 'Slack production-directory probe did not pass its call/cache checks.'
    Assert-True (@($probe.calls.cold).Count -eq 3 -and @($probe.calls.restart).Count -eq 0 -and @($probe.calls.expiredUser).Count -eq 2) 'Slack API request counts did not match cold, restart, and one-user-expiry expectations.'
    Assert-True ((@($probe.calls.cold | ForEach-Object { "$($_.method) $($_.endpoint)" }) -join ',') -eq 'POST auth.test,GET users.info,GET conversations.info') 'Cold lookup called outside the approved identity sequence.'
    Assert-True ((@($probe.calls.expiredUser | ForEach-Object { "$($_.method) $($_.endpoint)" }) -join ',') -eq 'POST auth.test,GET users.info') 'User-only expiry refreshed another Slack identity.'
    $userEntry = @($probe.labels | Where-Object { $_.kind -eq 'user' } | Select-Object -First 1)[0]
    $channelEntry = @($probe.labels | Where-Object { $_.kind -eq 'channel' } | Select-Object -First 1)[0]
    Assert-True ($userEntry.success -and $channelEntry.success -and $channelEntry.siteName) 'Slack identity lookup returned incomplete labels.'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$channelEntry.siteName)) 'Slack workspace name was not resolved.'
    $expectedUser = [string]$userEntry.userName
    $expectedChannel = [string]$channelEntry.channelName
    $expectedWorkspace = [string]$channelEntry.siteName
    Assert-True ($expectedUser -and $expectedChannel -and $expectedWorkspace) 'Slack real names are unavailable.'

    $currentStep = 'isolated-history-settings-and-fixture'
    $originalSettingsFile = Join-Path $normalDataDirectory 'settings.json'
    $storedSettings = Get-Content -LiteralPath $originalSettingsFile -Raw | ConvertFrom-Json
    $protectedSlackToken = [string]$storedSettings.SlackBotToken
    Assert-True ($protectedSlackToken.StartsWith('dpapi:', [StringComparison]::Ordinal)) 'The source bot token is not protected with DPAPI.'
    $settings = @{
        SchemaVersion = 1; WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; AgentMode = 'exec'; AgentProvider = 'custom'
        AutoConnectProvider = 'none'; SlackBotToken = $protectedSlackToken; SlackAppToken = ''; SlackAllowedUserIds = ''; SlackAllowedChannelIds = ''; SlackAllowedThreadIds = ''
        MattermostServerUrl = ''; MattermostChannelId = ''; MattermostToken = ''; MattermostAllowedUserIds = ''; MattermostAllowedThreadIds = ''; MattermostBypassProxy = $false
        TrustedWorkspaceRoots = $dataPath; AgentPermissionMode = 'inherit'; ChatApprovalMode = 'first'; ChatResultMode = 'manual'; RequireApproval = $true
    }
    $binding = @{
        Key = @{ Provider = 'slack'; ChannelId = $channelId; ThreadId = 'history-name-fixture-thread'; UserId = $userId; Origin = 'slack' }
        WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; SessionId = [guid]::NewGuid().ToString('N')
        UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o'); Model = $null; ReasoningEffort = $null; DisplayName = 'Slack history names real fixture'
    }
    $decoyBinding = @{
        Key = @{ Provider = 'local'; ChannelId = 'local-history'; ThreadId = 'unrelated-local-thread'; UserId = $null; Origin = $null }
        WorkspaceRoot = $dataPath; AgentExecutable = 'not-used'; AgentArguments = ''; SessionId = [guid]::NewGuid().ToString('N')
        UpdatedAt = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o'); DisplayName = 'Unrelated local history'
    }
    Write-Json 'sessions.json' @{ SchemaVersion = 1; Bindings = @($binding, $decoyBinding); Workspaces = @() }
    Write-Json 'settings.json' $settings
    Write-Json 'onboarding.json' @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    Write-Json 'language.json' @{ Language = 'ja' }
    Write-Json 'appearance.json' @{ Theme = 'dark' }

    $currentStep = 'cold-history-names'
    Start-App
    Wait-Text $expectedUser
    Wait-Text $expectedChannel
    Wait-Text $expectedWorkspace
    $rowText = Get-RowText
    Assert-True ($rowText.Contains($expectedUser) -and $rowText.Contains($expectedChannel) -and $rowText.Contains($expectedWorkspace)) 'History row does not show the real Slack user, channel, and workspace names.'
    Assert-True (-not $rowText.Contains($userId) -and -not $rowText.Contains($channelId) -and -not $rowText.Contains($teamId)) 'Opaque Slack IDs leaked into the history row.'
    Capture-UI 'slack-history-names-initial.png'
    $search = Find-Element 'SavedSessionsSearch'
    Assert-True ($null -ne $search) 'History search field was not found.'
    $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($expectedUser)
    Wait-VisibleCount 1 2
    $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($expectedWorkspace)
    Wait-VisibleCount 1 2
    Add-Pass 'Real Slack user, channel, and workspace names appear and are searchable without showing IDs.'

    $currentStep = 'cache-scope-and-secret-isolation'
    $cache = Get-Cache
    $times = Capture-CacheTimes
    $entries = @($cache.entries | Where-Object { $_.key.provider -eq 'slack' -and $_.key.origin -eq 'slack' })
    Assert-True ([string]$cache.version -eq '1' -and $entries.Count -eq 2) 'App cache should contain exactly scoped Slack user and channel entries.'
    Assert-True ((Get-CacheEntry $cache 'user' $userId).userName -eq $expectedUser) 'Cached Slack username differs from the resolved name.'
    Assert-True ((Get-CacheEntry $cache 'channel' $channelId).channelName -eq $expectedChannel) 'Cached Slack channel name differs from the resolved name.'
    Assert-True ((Get-CacheEntry $cache 'channel' $channelId).siteName -eq $expectedWorkspace -and (Get-CacheEntry $cache 'user' $userId).siteName -eq $expectedWorkspace) 'Cached workspace name differs from auth.test.'
    $cacheRaw = Get-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Raw
    Assert-True ($cacheRaw -notmatch '(?i)"(?:password|email|token|body|content)"\s*:') 'Identity cache contains an unrelated private field.'
    $settingsRaw = Get-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Raw
    Assert-True ($settingsRaw.Contains('dpapi:') -and -not $settingsRaw.Contains($expectedUser)) 'Isolated app settings were not protected or contain identity data.'
    Add-Pass 'Cache contains only matching Slack identity records, with names under the Slack origin and the token DPAPI-protected.'

    $currentStep = 'restart-with-token-removed-offline-cache-reuse'
    Stop-TestApp
    $settings.SlackBotToken = ''
    Write-Json 'settings.json' $settings
    Start-App
    Wait-Text $expectedUser
    Wait-Text $expectedChannel
    Wait-Text $expectedWorkspace
    Start-Sleep -Seconds 2
    Ui @('invoke', 'RefreshHistory') | Out-Null
    Start-Sleep -Seconds 2
    Capture-UI 'slack-history-names-cached-restart.png'
    $afterRestart = Capture-CacheTimes
    Assert-True ($afterRestart.user -eq $times.user -and $afterRestart.channel -eq $times.channel) 'Restart changed cached identity timestamps without credentials.'
    Add-Pass 'Restart displayed the cached Slack names with the cloned credential removed and preserved timestamps.'

    $settings.SlackBotToken = $protectedSlackToken
    Write-Json 'settings.json' $settings
    Stop-TestApp
    $sourceHashesAfter = Get-SourceHashes $normalDataDirectory
    foreach ($name in $sourceHashesBefore.Keys) {
        Assert-True ($sourceHashesBefore[$name] -eq $sourceHashesAfter[$name]) "The existing app changed its source $name during the isolated run."
    }
    $result = @{
        passed = $true; checks = @($checks); appProcessId = $appProcess.Id; dataDirectory = $dataPath
        workspace = $expectedWorkspace; user = $expectedUser; channel = $expectedChannel
        probe = @{ coldCalls = @($probe.calls.cold).Count; restartCalls = @($probe.calls.restart).Count; expiredUserCalls = @($probe.calls.expiredUser).Count }
        sourceSettingsAndHistoryUnchanged = $true
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataPath 'result.json') -Encoding utf8
    Write-Output "PASS Slack history names E2E completed: $dataPath"
}
catch {
    $safeResult = @{ passed = $false; step = $currentStep; errorType = $_.Exception.GetType().FullName; checks = @($checks); dataDirectory = $dataPath; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    try { $safeResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataPath 'result.json') -Encoding utf8 } catch { }
    Write-Error "Slack history names E2E failed at '$currentStep' ($($_.Exception.GetType().Name)); see sanitized result.json." -ErrorAction Continue
    exit 1
}
finally {
    Stop-TestApp
}
