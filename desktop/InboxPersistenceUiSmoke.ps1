# Verifies Inbox metadata restoration across real desktop process restarts.
# Publish the unpackaged desktop app and self-contained TestAgent before running.
param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/inbox-persistence-app'),
    [string]$TestAgentPath = (Join-Path $PSScriptRoot 'artifacts/inbox-persistence-test-agent/ChatAgentBridge.TestAgent.exe'),
    [ValidateSet('en', 'ja')][string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConnectionSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InboxSmokeInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@

$repo = Split-Path $PSScriptRoot -Parent
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AppDirectory).Path)
$agentPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $TestAgentPath).Path)
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or -not [IO.Path]::IsPathFullyQualified($TestAgentPath)) {
    throw 'AppDirectory and TestAgentPath must be absolute paths.'
}
$artifactPrefix = $artifactRoot.TrimEnd('\') + '\'
if (-not $appRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
    throw 'Use an isolated unpackaged portable publish under desktop/artifacts.'
}
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf)) {
    throw "Desktop executable was not found in AppDirectory: $AppDirectory"
}
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) { throw "Offline TestAgent executable was not found: $TestAgentPath" }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $agentPath) 'coreclr.dll'))) {
    throw 'Use a self-contained win-x64 TestAgent publish so the fixture does not depend on DOTNET_ROOT.'
}

# Reuse the isolated loopback/AppServer/UIA helpers maintained by the connection smoke.
# The source is evaluated only through its helper declarations; its test body is never run.
$PSScriptRootForFixture = $PSScriptRoot
$connectionSmoke = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ConnectionSettingsUiSmoke.ps1') -Raw
$functionStart = $connectionSmoke.IndexOf('function Assert(')
$testStart = [regex]::Match($connectionSmoke, '(?m)^try \{\r?\n    Start-IsolatedApp')
if ($functionStart -lt 0 -or -not $testStart.Success) { throw 'Could not extract connection smoke fixture helpers.' }
$helperSource = $connectionSmoke.Substring($functionStart, $testStart.Index - $functionStart)
$helperSource = $helperSource.Replace('$PSScriptRoot', '$PSScriptRootForFixture')
. ([scriptblock]::Create($helperSource))

$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data = Join-Path $script:root 'data'
$script:app = $null
$script:fixture = $null
$script:window = $null
$script:endpoint = $null
$script:results = [Collections.Generic.List[object]]::new()
$script:gate = Join-Path $script:data '.cab-live-steering-release'
$script:observations = Join-Path $script:data 'protocol-observations.jsonl'
$UpdateFixtureDirectory = $null
New-Item -ItemType Directory -Force -Path $script:data | Out-Null

function Write-JsonFile([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

function Stop-AppOnly {
    if ($script:app -and -not $script:app.HasExited) {
        try { $script:app.Kill($true); $script:app.WaitForExit(7000) | Out-Null } catch { }
    }
    $script:app = $null
    $script:window = $null
}

function Start-ExistingIsolatedApp([bool]$WaitForConnection = $true) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($script:data)
    $start.Environment['CAB_LIVE_STEERING_FIXTURE'] = '1'
    $start.Environment['CAB_LIVE_STEERING_RELEASE_FILE'] = $script:gate
    $start.Environment['CAB_LIVE_STEERING_OBSERVATIONS_FILE'] = $script:observations
    $script:app = [Diagnostics.Process]::Start($start)
    if ($WaitForConnection) { Wait-For { (Results).connections -eq 1 } 'loopback connection after process restart' }

    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($candidate in $windows) {
            if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break }
        }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $script:window) { throw 'No accessible application window appeared after restart.' }
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [ConnectionSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    if (-not [ConnectionSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30,
            [int](1280 * $scale), [int](850 * $scale), 0x0004 -bor 0x0010)) {
        throw 'Could not size the restarted app window to 1280 x 850 DIPs.'
    }
}

function Start-Inbox([bool]$WaitForConnection = $true) {
    $nav = Wait-Element 'NavChat' 20
    Invoke-Element $nav 'open chat inbox'
    Wait-Element 'InboxConversations' 20 | Out-Null
    if ($WaitForConnection) { Wait-For { (Results).connections -eq 1 } 'restored inbox connection' }
}

function Read-InboxDocument {
    $path = Join-Path $script:data 'chat-conversations.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Find-InboxEntry([string]$ThreadId) {
    $document = Read-InboxDocument
    if ($null -eq $document) { return $null }
    return @($document.Entries | Where-Object { $_.Key.ThreadId -eq $ThreadId } | Select-Object -First 1)[0]
}

function Wait-InboxEntry([string]$ThreadId, [string]$State, [int]$RequestCount, [int]$Seconds = 30) {
    Wait-For {
        $entry = Find-InboxEntry $ThreadId
        $null -ne $entry -and $entry.State -eq $State -and [int]$entry.RequestCount -eq $RequestCount
    } "Inbox metadata '$ThreadId' state=$State requests=$RequestCount" $Seconds
    return Find-InboxEntry $ThreadId
}

function Read-UiHistory {
    $path = Join-Path $script:data 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}

function Wait-StableReplyCount([int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $lastCount = -1
    $stableSamples = 0
    do {
        $count = @((Results).replies).Count
        if ($count -eq $lastCount) { $stableSamples++ } else { $lastCount = $count; $stableSamples = 0 }
        if ($stableSamples -ge 4) { return $count }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Loopback reply count did not settle within $Seconds seconds."
}

function Save-Evidence {
    Write-JsonFile (Join-Path $script:root 'results.json') @{
        passed = $true
        checks = $script:results.ToArray()
        dataDirectory = $script:data
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        fixture = 'loopback Mattermost server plus self-contained TestAgent; no external services'
    }
}

function Capture-Window([string]$Name) {
    if (-not $script:window) { throw 'Cannot capture a screenshot without the process window.' }
    $hwnd = $script:window.Current.NativeWindowHandle
    if ($hwnd -eq 0) { throw 'Refusing to capture an invalid window.' }
    & winapp ui screenshot -w $hwnd -o (Join-Path $script:root $Name) --json | Out-Null
    if ($LASTEXITCODE) { throw "Could not capture screenshot '$Name'." }
}

function Select-RestoredRow {
    $list = Wait-Element 'InboxConversations'
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $items = $list.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem))
        if ($items.Count -gt 0) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($items.Count -eq 0) { throw 'InboxConversations did not restore any rows.' }
    $pattern = $null
    if ($items[0].TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select()
    } else { Invoke-Element $items[0] 'select first restored inbox row' }
    $bounds = $items[0].Current.BoundingRectangle
    Assert (-not $items[0].Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) 'Restored row is not clickable in the app window.'
    $centerX = [int]($bounds.Left + $bounds.Width / 2)
    $centerY = [int]($bounds.Top + $bounds.Height / 2)
    [void][InboxSmokeInput]::SetForegroundWindow([IntPtr]$script:window.Current.NativeWindowHandle)
    if (-not [InboxSmokeInput]::SetCursorPos($centerX, $centerY)) { throw 'Could not point at the restored row.' }
    [InboxSmokeInput]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [InboxSmokeInput]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    return $items[0]
}

function Assert-RestoredDetail([string]$ExpectedRequestCount) {
    Wait-Element 'InboxRestoredConversationDetails' 12 | Out-Null
    $restoredState = Wait-Element 'InboxRestoredState' 12
    $requestCount = Wait-Element 'InboxRestoredRequestCount' 12
    Assert (-not [string]::IsNullOrWhiteSpace($restoredState.Current.Name)) 'Restored detail has no state label.'
    Assert ($requestCount.Current.Name -match [regex]::Escape($ExpectedRequestCount)) "Restored detail request count did not include '$ExpectedRequestCount'."
    $approve = Find-Element $script:window 'ApproveChat'
    Assert ($null -eq $approve -or -not $approve.Current.IsEnabled) 'Restored conversation unexpectedly exposes an actionable ApproveChat button.'
}

function Assert-NoSecretMetadata([string[]]$Markers) {
    $path = Join-Path $script:data 'chat-conversations.json'
    Assert (Test-Path -LiteralPath $path) 'Inbox metadata file was not written.'
    $raw = Get-Content -LiteralPath $path -Raw
    foreach ($marker in $Markers) {
        Assert (-not $raw.Contains($marker, [StringComparison]::Ordinal)) "Inbox metadata leaked conversation content marker '$marker'."
    }
}

try {
    Start-IsolatedApp

    # Seed the profile and loopback server using the shared helper, then restart with
    # approval required while retaining the same data directory and fixture process.
    $languagePath = Join-Path $script:data 'language.json'
    Write-JsonFile $languagePath @{ Language = $Language }
    Stop-AppOnly
    $settingsPath = Join-Path $script:data 'settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.ChatApprovalMode = 'always'
    $settings.ChatResultMode = 'auto'
    Write-JsonFile $settingsPath $settings
    Start-ExistingIsolatedApp
    Start-Inbox

    $thread = 'inbox-pending-' + [guid]::NewGuid().ToString('N')
    $privatePrompt = 'INBOX_PRIVATE_PROMPT_' + [guid]::NewGuid().ToString('N')
    $privateResult = 'INBOX_PRIVATE_RESULT_' + [guid]::NewGuid().ToString('N')
    Send-Request ('pending-' + [guid]::NewGuid().ToString('N')) $thread "$privatePrompt LIVE_STEER_QUEUE_1 $privateResult"
    $pending = Wait-InboxEntry $thread 'pending' 1
    Assert-NoSecretMetadata @($privatePrompt, $privateResult, 'LIVE_STEER_FINAL')
    $pendingRepliesBeforeRestart = Wait-StableReplyCount
    Add-Pass 'Pending request is represented by content-free inbox metadata before restart'

    Stop-AppOnly
    Start-ExistingIsolatedApp
    Start-Inbox
    $restoredPending = Select-RestoredRow
    Assert-RestoredDetail '1'
    Assert ($null -ne $restoredPending) 'Could not select the restored pending conversation row.'
    $restoredStateText = (Wait-Element 'InboxRestoredState').Current.Name
    $interruptedText = if ($Language -eq 'ja') { '中断' } else { 'Interrupted' }
    Assert ($restoredStateText -like "*$interruptedText*") "Pending request did not restore as interrupted: '$restoredStateText'."
    Assert ((Read-UiHistory).Count -eq 0) 'Restoring a pending request created execution history.'
    $pendingRepliesAfterRestart = Wait-StableReplyCount
    Assert ($pendingRepliesAfterRestart -eq $pendingRepliesBeforeRestart) 'Restoring a pending request posted a new reply after restart.'
    Assert-NoSecretMetadata @($privatePrompt, $privateResult, 'LIVE_STEER_FINAL')
    Capture-Window 'pending-restored-light.png'
    Add-Pass 'Restart restores a metadata-only interrupted row without approval, execution, history, or reply'

    $mergePrompt = 'INBOX_PRIVATE_MERGED_' + [guid]::NewGuid().ToString('N')
    Send-Request ('merged-' + [guid]::NewGuid().ToString('N')) $thread "$mergePrompt LIVE_STEER_QUEUE_2"
    $merged = Wait-InboxEntry $thread 'pending' 2
    Assert (@((Read-InboxDocument).Entries | Where-Object { $_.Key.ThreadId -eq $thread }).Count -eq 1) 'A new same-thread request created a duplicate Inbox conversation row.'
    Assert ($merged.RequestCount -eq 2) 'The restored conversation did not retain its request count when the new message arrived.'
    Assert-NoSecretMetadata @($privatePrompt, $privateResult, $mergePrompt, 'LIVE_STEER_FINAL')
    Wait-For { (Wait-Element 'ApproveChat').Current.IsEnabled } 'fresh same-thread request approval action'
    Capture-Window 'same-thread-follow-up-light.png'
    Set-Content -LiteralPath $script:gate -Value 'release approved same-thread request' -Encoding utf8
    Invoke-Element (Wait-Element 'ApproveChat') 'approve fresh same-thread request'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm execution of fresh same-thread request'
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 1 } 'same-thread request execution to complete'
    Wait-For { @((Results).replies | Where-Object { $_.message -like '*LIVE_STEER_FINAL*' }).Count -ge 1 } 'same-thread fixture result reply'
    $startsBeforeCrash = @((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count
    Assert ($startsBeforeCrash -eq 1) 'Same-thread request did not create exactly one TestAgent turn.'
    Add-Pass 'A new message merges with the restored conversation and can be approved as a fresh request'

    # Remove the gate before the running-state case. The TestAgent starts a real held turn,
    # then the desktop process and its child agent are killed before any completion arrives.
    Remove-Item -LiteralPath $script:gate -Force
    Stop-AppOnly
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.ChatApprovalMode = 'auto'
    Write-JsonFile $settingsPath $settings
    Start-ExistingIsolatedApp
    Start-Inbox
    $runningThread = 'inbox-running-' + [guid]::NewGuid().ToString('N')
    Send-Request ('running-' + [guid]::NewGuid().ToString('N')) $runningThread 'LIVE_STEER_STEER_1'
    Wait-InboxEntry $runningThread 'running' 1
    Wait-For { @((Read-Observations | Where-Object { $_.method -eq 'turn/start' -and $_.scenario -eq 'LIVE_STEER_STEER_1' })).Count -eq 1 } 'gated TestAgent turn start'
    $historyBeforeRunningRestart = @(Read-UiHistory).Count
    $repliesBeforeRunningRestart = Wait-StableReplyCount
    Assert (-not (Test-Path -LiteralPath $script:gate)) 'Gated TestAgent release file unexpectedly exists.'
    Assert-NoSecretMetadata @('LIVE_STEER_STEER_1', 'LIVE_STEER_FINAL')
    Capture-Window 'running-before-restart-light.png'
    Add-Pass 'Running TestAgent request is persisted before abrupt process termination'

    Stop-AppOnly
    Start-ExistingIsolatedApp
    Start-Inbox
    $restoredRunning = Select-RestoredRow
    Assert-RestoredDetail '1'
    Assert ($null -ne $restoredRunning) 'Could not select the restored running conversation row.'
    $restoredRunningState = (Wait-Element 'InboxRestoredState').Current.Name
    Assert ($restoredRunningState -like "*$interruptedText*") "Running request did not restore as interrupted: '$restoredRunningState'."
    Assert (@(Read-UiHistory).Count -eq $historyBeforeRunningRestart) 'Restoring a running request added execution history.'
    Assert ((Wait-StableReplyCount) -eq $repliesBeforeRunningRestart) 'Restoring a running request posted an automatic result reply.'
    $startsAfterRestart = @((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count
    Assert ($startsAfterRestart -eq $startsBeforeCrash + 1) 'The restored running request was automatically executed again.'
    Capture-Window 'running-restored-light.png'
    Add-Pass 'Abrupt restart marks a running request interrupted without replaying or publishing it'

    # With the release gate open, complete a separate request and verify that a
    # completed metadata row remains completed after the next restart.
    Set-Content -LiteralPath $script:gate -Value 'release completed-state request' -Encoding utf8
    $completedThread = 'inbox-completed-' + [guid]::NewGuid().ToString('N')
    Send-Request ('completed-' + [guid]::NewGuid().ToString('N')) $completedThread 'LIVE_STEER_QUEUE_1'
    Wait-InboxEntry $completedThread 'completed' 1
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 2 } 'completed-state fixture turn'
    Invoke-Element (Wait-Element 'NavAbout') 'open About for graceful shutdown'
    Invoke-Element (Wait-Element 'AboutExit') 'request graceful application exit'
    Wait-ProcessElement 'WindowCloseConfirmation' | Out-Null
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm graceful application exit'
    Wait-For { $script:app.HasExited } 'graceful app shutdown'
    $connectionDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Results).connections -ne 0 -and [DateTime]::UtcNow -lt $connectionDeadline) { Start-Sleep -Milliseconds 100 }
    Assert ((Results).connections -eq 0) 'Graceful app shutdown left the loopback connection open.'
    $appearancePath = Join-Path $script:data 'appearance.json'
    $appearance = Get-Content -LiteralPath $appearancePath -Raw | ConvertFrom-Json
    $appearance.Theme = 'dark'
    Write-JsonFile $appearancePath $appearance
    Start-ExistingIsolatedApp
    Start-Inbox
    $restoredCompleted = Select-RestoredRow
    Assert ($null -ne $restoredCompleted) 'Could not select restored completed conversation.'
    $completedEntry = Find-InboxEntry $completedThread
    Assert ($completedEntry.State -eq 'completed') "Completed state changed after restart: '$($completedEntry.State)'."
    Wait-Element 'InboxRestoredConversationDetails' 12 | Out-Null
    Capture-Window 'completed-restored-dark.png'
    Assert-NoSecretMetadata @('LIVE_STEER_FINAL')
    Add-Pass 'Completed conversation remains completed after restart; light and dark evidence captured'

    Stop-AppOnly
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.AutoConnectProvider = 'none'
    Write-JsonFile $settingsPath $settings
    [IO.File]::WriteAllText((Join-Path $script:data 'sessions.json'), '{"SchemaVersion":1,"Bindings":[', [Text.UTF8Encoding]::new($false))
    Start-ExistingIsolatedApp $false
    Start-Inbox $false
    $offlineList = Wait-Element 'InboxConversations'
    $offlineItems = $offlineList.FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem))
    Assert ($offlineItems.Count -ge 3) "Inbox did not restore all three rows without a chat connection (found $($offlineItems.Count))."
    Assert ($null -ne (Find-InboxEntry $thread) -and (Find-InboxEntry $thread).RequestCount -eq 2) 'Offline restore lost the merged conversation metadata.'
    Assert ((Find-InboxEntry $runningThread).State -in @('running', 'interrupted')) 'Offline restore lost the interrupted running conversation metadata.'
    Assert ((Find-InboxEntry $completedThread).State -eq 'completed') 'Offline restore lost the completed conversation metadata.'
    Select-RestoredRow | Out-Null
    Wait-Element 'InboxRestoredConversationDetails' 12 | Out-Null
    Assert ((Results).connections -eq 0) 'Offline restoration unexpectedly connected to the loopback chat fixture.'
    Capture-Window 'offline-restored-dark.png'
    Add-Pass 'Inbox metadata rows restore offline despite malformed legacy session data'

    # A corrupt legacy session file should not prevent inbox recovery, but an invalid
    # configured workspace must block a new connection without mutating saved inbox data.
    Stop-AppOnly
    Write-JsonFile (Join-Path $script:data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.AutoConnectProvider = 'none'
    $settings.WorkspaceRoot = 'relative-folder'
    Write-JsonFile $settingsPath $settings
    Start-ExistingIsolatedApp $false
    Start-Inbox $false
    $metadataPath = Join-Path $script:data 'chat-conversations.json'
    $metadataBeforeInvalidConnect = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($metadataPath)))
    $historyBeforeInvalidConnect = @(Read-UiHistory).Count
    Assert ((Results).connections -eq 0) 'Invalid-workspace check started with an unexpected chat connection.'
    Invoke-Element (Wait-Element 'ConnectMattermost') 'connect with invalid workspace setting'
    $connectionError = Wait-Element 'InboxConnectionError' 12
    $errorText = [Collections.Generic.List[string]]::new()
    $errorText.Add($connectionError.Current.Name)
    $errorChildren = $connectionError.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $errorChildren) { if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $errorText.Add($child.Current.Name) } }
    $expectedError = if ($Language -eq 'ja') { '作業フォルダーの設定が不正です。エージェント設定で、存在するフォルダーの絶対パスを選択してください。' } else { 'The working folder setting is invalid. In Agent settings, select an existing folder using its full absolute path.' }
    Assert (($errorText -join "`n") -like "*$expectedError*") "Connect did not show localized InboxWorkspaceInvalid text: '$($errorText -join ' | ')'"
    Assert ((Results).connections -eq 0) 'Invalid workspace unexpectedly opened a loopback chat connection.'
    Assert (@(Read-UiHistory).Count -eq $historyBeforeInvalidConnect) 'Invalid-workspace connect changed execution history.'
    $metadataAfterInvalidConnect = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($metadataPath)))
    Assert ($metadataAfterInvalidConnect -eq $metadataBeforeInvalidConnect) 'Invalid-workspace connect changed chat conversation metadata bytes.'
    Add-Pass 'Invalid workspace shows localized error without connecting or changing history or Inbox metadata'

    # Exercise save-time row isolation with an existing, trusted workspace path that
    # exceeds the persisted metadata limit. Approval stays required, so no agent turn runs.
    Stop-AppOnly
    $workspaceRoot = Join-Path $script:data 'workspace'
    $longWorkspace = $workspaceRoot
    for ($segment = 0; $segment -lt 13; $segment++) {
        $longWorkspace = Join-Path $longWorkspace ('segment-' + $segment.ToString('D2') + ('x' * 165))
    }
    [IO.Directory]::CreateDirectory($longWorkspace) | Out-Null
    Assert ($longWorkspace.Length -gt 2048) "Could not create a per-thread workspace path over 2048 characters (length $($longWorkspace.Length))."
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.WorkspaceRoot = $workspaceRoot
    $settings.TrustedWorkspaceRoots = $workspaceRoot
    $settings.AutoConnectProvider = 'mattermost'
    $settings.ChatApprovalMode = 'always'
    Write-JsonFile $settingsPath $settings
    $invalidRowThread = 'inbox-invalid-row-' + [guid]::NewGuid().ToString('N')
    $scope = (Find-InboxEntry $thread).Key
    $invalidRowKey = @{
        Provider = 'mattermost'; ChannelId = $scope.ChannelId; ThreadId = $invalidRowThread
        UserId = $scope.UserId; Origin = $scope.Origin
    }
    Write-JsonFile (Join-Path $script:data 'sessions.json') @{
        SchemaVersion = 1; Bindings = @(); Agents = @()
        Workspaces = @(@{ Key = $invalidRowKey; WorkspaceRoot = $longWorkspace })
    }
    Start-ExistingIsolatedApp
    Start-Inbox
    $turnStartsBeforeInvalidRow = @((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count
    $historyBeforeInvalidRow = @(Read-UiHistory).Count
    Send-Request ('invalid-row-' + [guid]::NewGuid().ToString('N')) $invalidRowThread 'invalid metadata workspace row'
    $warningResourcePath = Join-Path $repo ('desktop/ChatAgentBridge.Desktop/Localization/DesktopStrings' + $(if ($Language -eq 'ja') { '.ja' } else { '' }) + '.resx')
    [xml]$warningResources = Get-Content -LiteralPath $warningResourcePath -Raw
    $expectedSomeRejected = $warningResources.SelectSingleNode("/root/data[@name='InboxPersistenceSomeRowsRejected']/value").InnerText.Replace('{0}', '1')
    Wait-For {
        $element = Find-Element $script:window 'InboxConversationPersistenceWarning'
        if (-not $element) { return $false }
        $names = @($element.Current.Name)
        foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) { $names += $child.Current.Name }
        ($names -join "`n") -like "*$expectedSomeRejected*"
    } 'one-row Inbox metadata rejection warning' 20
    $invalidRowWarning = Wait-Element 'InboxConversationPersistenceWarning'
    $warningText = [Collections.Generic.List[string]]::new()
    $warningText.Add($invalidRowWarning.Current.Name)
    $warningChildren = $invalidRowWarning.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $warningChildren) { if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $warningText.Add($child.Current.Name) } }
    Assert (($warningText -join "`n") -like "*$expectedSomeRejected*") "Save did not report one rejected row while preserving other rows: '$($warningText -join ' | ')'"
    $afterInvalidRowDocument = Read-InboxDocument
    Assert (@($afterInvalidRowDocument.Entries | Where-Object { $_.Key.ThreadId -eq $invalidRowThread }).Count -eq 0) 'Invalid overlong-workspace row was persisted.'
    Assert ($afterInvalidRowDocument.Entries.Count -ge 3) 'Rejecting an invalid row erased previously valid conversations.'
    Assert ((Results).connections -eq 1) 'Invalid-row save case lost its loopback connection.'
    Assert (@(Read-UiHistory).Count -eq $historyBeforeInvalidRow) 'Pending invalid-row test request unexpectedly created execution history.'
    Assert (@((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count -eq $turnStartsBeforeInvalidRow) 'Pending invalid-row test request unexpectedly started an agent turn.'

    $validAfterInvalidThread = 'inbox-valid-after-invalid-' + [guid]::NewGuid().ToString('N')
    Send-Request ('valid-after-invalid-' + [guid]::NewGuid().ToString('N')) $validAfterInvalidThread 'valid row after rejected metadata'
    Wait-InboxEntry $validAfterInvalidThread 'pending' 1 | Out-Null
    $validAfterInvalidDoc = Read-InboxDocument
    Assert (@($validAfterInvalidDoc.Entries | Where-Object { $_.Key.ThreadId -eq $invalidRowThread }).Count -eq 0) 'A later save reintroduced the rejected invalid row.'
    Assert (@($validAfterInvalidDoc.Entries | Where-Object { $_.Key.ThreadId -eq $validAfterInvalidThread }).Count -eq 1) 'A valid row was not saved alongside the rejected row.'
    Wait-For {
        $element = Find-Element $script:window 'InboxConversationPersistenceWarning'
        if (-not $element) { return $false }
        $names = @($element.Current.Name)
        foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) { $names += $child.Current.Name }
        ($names -join "`n") -like "*$expectedSomeRejected*"
    } 'persistent one-row Inbox metadata rejection warning' 20
    $warningText = [Collections.Generic.List[string]]::new()
    $warningText.Add((Wait-Element 'InboxConversationPersistenceWarning').Current.Name)
    $warningChildren = (Wait-Element 'InboxConversationPersistenceWarning').FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $warningChildren) { if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $warningText.Add($child.Current.Name) } }
    Assert (($warningText -join "`n") -like "*$expectedSomeRejected*") 'The persistence warning disappeared before the invalid row was repaired.'
    Assert (@(Read-UiHistory).Count -eq $historyBeforeInvalidRow) 'Valid follow-up request unexpectedly created execution history.'
    Assert (@((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count -eq $turnStartsBeforeInvalidRow) 'Valid follow-up request unexpectedly started an agent turn.'
    Add-Pass 'Invalid metadata row is isolated while valid conversation rows continue to save without execution'

    Save-Evidence
    Write-Output "PASS Inbox persistence UI smoke completed: $script:root"
}
catch {
    try { Capture-Window 'failure.png' } catch { try { Capture-FailureScreenshot } catch { } }
    $script:results.Add(@{ name = 'Inbox restart persistence'; status = 'FAIL'; detail = $_.Exception.Message })
    Write-JsonFile (Join-Path $script:root 'results.json') @{
        passed = $false; checks = $script:results.ToArray(); dataDirectory = $script:data
        error = $_.Exception.Message; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    throw
}
finally { Stop-IsolatedApp }
