param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [string]$UpdateFixtureDirectory
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$agentPath = [IO.Path]::GetFullPath($TestAgentPath)
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or -not [IO.Path]::IsPathFullyQualified($TestAgentPath)) { throw 'AppDirectory and TestAgentPath must be absolute paths.' }
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) { throw 'Use an isolated unpackaged portable publish under desktop/artifacts.' }
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf)) { throw "Desktop executable was not found in AppDirectory: $AppDirectory" }
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) { throw "Offline TestAgent executable was not found: $TestAgentPath" }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $agentPath) 'coreclr.dll'))) {
    throw 'Use a self-contained win-x64 TestAgent publish so the fixture does not depend on the PC DOTNET_ROOT setting.'
}
if ($UpdateFixtureDirectory) {
    if (-not [IO.Path]::IsPathFullyQualified($UpdateFixtureDirectory)) { throw 'UpdateFixtureDirectory must be an absolute path.' }
    if (-not (Test-Path -LiteralPath $UpdateFixtureDirectory -PathType Container)) { throw "Update fixture directory was not found: $UpdateFixtureDirectory" }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConnectionSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data = Join-Path $script:root 'data'
$script:fixture = $null
$script:app = $null
$script:window = $null
$script:endpoint = $null
$script:results = [Collections.Generic.List[object]]::new()
$script:gate = Join-Path $script:data '.cab-live-steering-release'
$script:observations = Join-Path $script:data 'protocol-observations.jsonl'
New-Item -ItemType Directory -Force -Path $script:data | Out-Null

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Save-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Add-Pass([string]$Name, [string]$Detail = '') {
    $script:results.Add(@{ name = $Name; status = 'PASS'; detail = $Detail })
    Write-Output "PASS $Name"
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($script:app -and $script:app.HasExited) { throw "App exited while waiting for $Description (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
function Results { Invoke-RestMethod -Uri "$script:endpoint/test/results" -TimeoutSec 3 }
function Read-History {
    $path = Join-Path $script:data 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    if (-not $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Element([string]$Id, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:window $Id
        if ($element) { return $element }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for UIA element '$Id'."
}
function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    if (-not $Element.Current.IsEnabled) { throw "UI element is disabled: $Description" }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.InvokePattern]$pattern).Invoke(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return }
    throw "No invoke or selection UIA pattern for $Description."
}
function Wait-ProcessElement([string]$Id, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id))
        foreach ($window in $windows) {
            $element = Find-Element $window $Id
            if ($element) { return $element }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for popup UIA element '$Id'."
}
function Select-Element([string]$Id) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { throw "$Id has no SelectionItemPattern." }
    ([Windows.Automation.SelectionItemPattern]$pattern).Select()
}
function Scroll-ToElement([string]$Id) {
    $target = Wait-Element $Id
    $container = Wait-Element 'MainContentScroll'
    $pattern = $null
    if (-not $container.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { throw 'MainContentScroll lacks ScrollPattern.' }
    $scroll = [Windows.Automation.ScrollPattern]$pattern
    for ($index = 0; $index -lt 12; $index++) {
        if (-not $target.Current.IsOffscreen) { return $target }
        if (-not $scroll.Current.VerticallyScrollable -or $scroll.Current.VerticalScrollPercent -ge 100) { break }
        $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 100
        $target = Wait-Element $Id 2
    }
    throw "Could not scroll '$Id' into view."
}
function Send-Request([string]$Id, [string]$Thread, [string]$Text, [bool]$Plain = $false) {
    $body = @{ id = $Id; thread = $Thread; user = 'alice'; text = $Text; mention = $false; plain = $Plain } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 3
    Assert ($response.delivered -eq 1) 'Fixture request did not reach exactly one connected transport.'
}
function Read-Observations {
    if (-not (Test-Path -LiteralPath $script:observations)) { return @() }
    @((Get-Content -LiteralPath $script:observations | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json }))
}
function Start-IsolatedApp {
    $workspace = Join-Path $script:data 'workspace'
    $agentRoot = Join-Path $script:data 'agent'
    New-Item -ItemType Directory -Force -Path $workspace, $agentRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $script:data '.cab-live-steering-fixture') -Value 'Isolated offline AppServer follow-up fixture' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $workspace '.cab-live-steering-workspace') -Value 'offline smoke workspace' -Encoding utf8
    Set-Content -LiteralPath $script:observations -Value '' -Encoding utf8
    $sourceDirectory = Split-Path -Parent $agentPath
    Get-ChildItem -LiteralPath $sourceDirectory -File | Where-Object { $_.FullName -ne $agentPath } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $agentRoot $_.Name) -Force }
    $agent = Join-Path $agentRoot 'codex.exe'
    Copy-Item -LiteralPath $agentPath -Destination $agent -Force
    Add-Type -AssemblyName System.Security
    $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $portFile = Join-Path $script:root 'fixture-port.txt'
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixtureScript = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $script:fixture = Start-Process -FilePath $node -ArgumentList @('"' + $fixtureScript + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath $portFile } 'loopback fixture startup'
    $script:endpoint = 'http://127.0.0.1:' + (Get-Content -LiteralPath $portFile -Raw).Trim()
    Save-Json (Join-Path $script:data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'codex'; AgentMode = 'app-server'; AgentExecutable = $agent
        AgentArguments = 'app-server'; AgentModel = $null; AgentReasoningEffort = $null; AgentPermissionMode = 'inherit'
        TrustedWorkspaceRoots = $workspace; RequireApproval = $false; AllowAgentNetwork = $false
        MattermostServerUrl = $script:endpoint; MattermostChannelId = 'ui-channel'; MattermostToken = $token
        MattermostAllowedUserIds = 'alice'; MattermostAllowedThreadIds = ''; ChatApprovalMode = 'auto'; ChatResultMode = 'auto'
        ChatFollowUpMode = 'queue'; AutoConnectProvider = 'mattermost'; PostConnectionNotice = $false
    }
    Save-Json (Join-Path $script:data 'agent-connections.json') @{ SchemaVersion = 1; Profiles = @(@{
        Provider = 'codex'; Executable = $agent; Arguments = 'app-server'; Mode = 'app-server'; Model = $null; ReasoningEffort = $null
        PermissionMode = 'inherit'; AllowNetwork = $false; CliVersion = 'codex-cli 1.2.3 (offline fixture)'; CheckedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }) }
    Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    $policy = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read current onboarding consent policy version.'
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = 'en' }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = 'light' }
    Save-Json (Join-Path $script:data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false; LastAttemptUtc = $null; AvailableRelease = $null }
    Save-Json (Join-Path $script:data 'pricing-auto.json') @{ schemaVersion=1; autoUpdateEnabled=$false; catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw) }
    Save-Json (Join-Path $script:data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Save-Json (Join-Path $script:data 'history.json') @{ SchemaVersion = 1; Records = @() }
    Save-Json (Join-Path $script:data 'audit.json') @{ SchemaVersion = 1; Records = @() }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false; $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($script:data)
    $start.Environment['CAB_LIVE_STEERING_FIXTURE'] = '1'
    $start.Environment['CAB_LIVE_STEERING_RELEASE_FILE'] = $script:gate
    $start.Environment['CAB_LIVE_STEERING_OBSERVATIONS_FILE'] = $script:observations
    if ($UpdateFixtureDirectory) {
        $start.Environment['CAB_PORTABLE_UPDATE_TEST_DIRECTORY'] = [IO.Path]::GetFullPath($UpdateFixtureDirectory)
        $start.Environment['CAB_PORTABLE_UPDATE_ABORT_TEST'] = '1'
    }
    $script:app = [Diagnostics.Process]::Start($start)
    Wait-For { (Results).connections -eq 1 } 'initial loopback connection'
    $processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($candidate in $windows) { if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break } }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $script:window) { throw 'No accessible application window appeared.' }
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [ConnectionSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    if (-not [ConnectionSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30, [int](1024 * $scale), [int](768 * $scale), 0x0004 -bor 0x0010)) { throw 'Could not size the app window to 1024 x 768 DIPs.' }
}
function Capture-FailureScreenshot {
    if (-not $script:window) { return }
    $hwnd = $script:window.Current.NativeWindowHandle
    if ($hwnd -eq 0) { throw 'Refusing to capture an invalid window.' }
    & winapp ui screenshot -w $hwnd -o (Join-Path $script:root 'failure.png') --json | Out-Null
    if ($LASTEXITCODE) { throw 'Could not capture the test window.' }
}
function Stop-IsolatedApp {
    if ($script:app -and -not $script:app.HasExited) { try { $script:app.Kill($true); $script:app.WaitForExit(7000) | Out-Null } catch { } }
    if ($script:fixture -and -not $script:fixture.HasExited) { try { $script:fixture.Kill(); $script:fixture.WaitForExit(3000) | Out-Null } catch { } }
}

try {
    Start-IsolatedApp
    $navStatus = Wait-Element 'NavChatConnectionStatus'
    Wait-For { $navStatus.Current.Name -like '*Mattermost*Ready to receive requests*' } 'ready connection status in sidebar'
    Invoke-Element $navStatus 'open chat inbox from connection item'
    $inboxLabel = Wait-Element 'InboxConnectionLabel'
    Assert ($inboxLabel.Current.Name -like '*Ready to receive requests*') 'Clicking the status item did not open the connected chat inbox.'
    Add-Pass 'Sidebar connection item shows Ready and opens the inbox'

    $thread = 'connection-ui-' + [guid]::NewGuid().ToString('N')
    Send-Request ('queue-' + [guid]::NewGuid().ToString('N')) $thread 'LIVE_STEER_QUEUE_1 initial queued thread'
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'running' }).Count -eq 1 } 'initial thread to start'
    Set-Content -LiteralPath $script:gate -Value 'release queue run' -Encoding utf8
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 1 } 'initial thread to complete'
    Assert ((Results).totalConnections -eq 1) 'Initial queued request unexpectedly reconnected the transport.'
    Add-Pass 'Initial queued thread completes without reconnecting'
    Remove-Item -LiteralPath $script:gate -Force

    Select-Element 'NavConnections'
    $approvalCard = Wait-Element 'ChatApprovalCard'
    $cardExpand = $null
    if (-not $approvalCard.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$cardExpand)) { throw 'ChatApprovalCard lacks ExpandCollapsePattern.' }
    if (([Windows.Automation.ExpandCollapsePattern]$cardExpand).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) {
        ([Windows.Automation.ExpandCollapsePattern]$cardExpand).Expand()
    }
    $mode = Scroll-ToElement 'ChatFollowUpMode'
    $expand = $null
    if (-not $mode.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { throw 'ChatFollowUpMode lacks ExpandCollapsePattern.' }
    ([Windows.Automation.ExpandCollapsePattern]$expand).Expand()
    Invoke-Element (Wait-ProcessElement 'ChatFollowUpModeSteer') 'select live follow-up mode'
    $explanation = Wait-ProcessElement 'FollowUpSteerDialog'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'accept live follow-up explanation'
    $confirmation = Wait-ProcessElement 'SensitiveSettingsConfirmation'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm live follow-up setting'
    Wait-For { (Get-Content -LiteralPath (Join-Path $script:data 'settings.json') -Raw | ConvertFrom-Json).ChatFollowUpMode -eq 'steer' } 'steer setting autosave'
    Wait-For { (Results).totalConnections -eq 1 -and (Results).connections -eq 1 } 'settings save without reconnecting'
    Assert ($null -ne $explanation -and $null -ne $confirmation) 'Sensitive follow-up confirmations were not shown.'
    Add-Pass 'UI selection saves steer mode without reconnecting'

    Remove-Item -LiteralPath $script:gate -Force -ErrorAction SilentlyContinue
    Send-Request ('steer-' + [guid]::NewGuid().ToString('N')) $thread 'LIVE_STEER_STEER_1 held turn'
    Wait-For { @((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count -eq 2 } 'held AppServer turn after mode change'
    Send-Request ('plain-reply-' + [guid]::NewGuid().ToString('N')) $thread 'LIVE_STEER_STEER_2 plain same-thread reply' $true
    Wait-For { @((Read-Observations | Where-Object { $_.method -eq 'turn/steer' -and $_.outcome -eq 'accepted' -and $_.inputMarker -eq 'LIVE_STEER_STEER_2' })).Count -eq 1 } 'accepted plain reply delivered as turn/steer'
    Assert (@((Read-Observations | Where-Object { $_.method -eq 'turn/start' })).Count -eq 2) 'Steer mode started an extra turn instead of steering the held turn.'
    Set-Content -LiteralPath $script:gate -Value 'release live steer run' -Encoding utf8
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 2 } 'steered thread to finish'
    Wait-For { ((Results).replies | Where-Object { $_.message -like '*LIVE_STEER_FINAL*' }).Count -ge 2 } 'both fixture replies'
    Assert ((Results).totalConnections -eq 1) 'Changing follow-up mode or accepting its plain reply reconnected the transport.'
    Add-Pass 'Plain reply on the owned thread is accepted and steers the held turn'

    Invoke-RestMethod -Uri "$script:endpoint/test/disconnect" -Method Post -ContentType 'application/json' -Body '{"offlineMilliseconds":6000}' -TimeoutSec 3 | Out-Null
    Wait-For { $navStatus.Current.Name -like '*Connecting*' } 'sidebar reconnecting state' 15
    Wait-For { (Results).connections -eq 1 -and $navStatus.Current.Name -like '*Ready to receive requests*' } 'sidebar ready state after automatic reconnect' 30
    Assert ((Results).totalConnections -ge 2) 'Automatic reconnection did not establish a second fixture connection.'
    Add-Pass 'Sidebar reports reconnecting then ready after a temporary outage'

    if ($UpdateFixtureDirectory) {
        $probe = Wait-Element 'PortableUpdateRecoveryProbe'
        $before = (Results).totalConnections
        Invoke-Element $probe 'connected update preparation recovery probe'
        Wait-For { (Wait-Element 'PortableUpdateRecoveryProbe').Current.Name -like '*Update recovery probe completed*' } 'connected recovery probe completion'
        Wait-For { (Results).totalConnections -eq ($before + 1) -and (Results).connections -eq 1 } 'connected recovery probe to reconnect'
        Add-Pass 'Connected portable update preparation restores the connection after abort'
    }
    Invoke-Element (Wait-Element 'NavChatConnectionStatus') 'return to the chat inbox'
    Wait-For { (Wait-Element 'InboxConnectionLabel').Current.Name -like '*Ready to receive requests*' } 'return to ready chat inbox'
    Invoke-Element (Wait-Element 'DisconnectChat') 'manually disconnect chat'
    Wait-For { (Results).connections -eq 0 -and $navStatus.Current.Name -like '*Disconnected*' } 'offline sidebar state after manual disconnect'
    Add-Pass 'Manual disconnect is reflected as Disconnected in the sidebar'
    if ($UpdateFixtureDirectory) {
        $before = (Results).totalConnections
        Invoke-Element (Wait-Element 'PortableUpdateRecoveryProbe') 'offline update preparation recovery probe'
        Wait-For { (Wait-Element 'PortableUpdateRecoveryProbe').Current.Name -like '*Update recovery probe completed*' } 'offline recovery probe to abort cleanly'
        Start-Sleep -Milliseconds 400
        Assert ((Results).connections -eq 0 -and (Results).totalConnections -eq $before) 'Offline recovery probe unexpectedly reconnected chat.'
        Add-Pass 'Offline update preparation abort leaves chat offline without a reconnect'
    }
    Write-Output "Connection settings UI smoke artifacts: $script:root"
}
catch {
    $script:results.Add(@{ name = 'Connection settings UI smoke'; status = 'FAIL'; detail = $_.Exception.Message })
    try { Capture-FailureScreenshot } catch { Write-Warning "Could not capture failure screenshot: $_" }
    throw
}
finally {
    Stop-IsolatedApp
    try { [IO.File]::WriteAllText((Join-Path $script:root 'results.json'), ($script:results | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false)) }
    catch { Write-Warning "Could not save smoke results: $_" }
}
