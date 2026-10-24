# Native, offline Discord bridge acceptance smoke. It uses a loopback REST/Gateway
# fixture and the repository's self-contained TestAgent; it never contacts Discord.
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [ValidateSet('en','ja')][string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$agentPath = [IO.Path]::GetFullPath($TestAgentPath)
if (-not [IO.Path]::IsPathRooted($AppDirectory) -or
    -not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
    throw 'Use an unpackaged Discord-fixture app publish under desktop/artifacts.'
}
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf)) { throw "Desktop app not found: $AppDirectory" }
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $agentPath) 'coreclr.dll'))) {
    throw 'Use a self-contained TestAgent publish.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiscordBridgeSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data = Join-Path $script:root 'data'
$script:workspace = Join-Path $script:root 'workspace'
$script:agentTranscript = Join-Path $script:root 'agent-calls.jsonl'
$script:app = $null
$script:fixture = $null
$script:window = $null
$script:endpoint = $null
$script:checks = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $script:data | Out-Null
Set-Content -LiteralPath (Join-Path $script:data '.cab-discord-fixture') -Value 'Offline Discord UI fixture' -Encoding utf8
Set-Content -LiteralPath (Join-Path $script:root '.cab-file-attachments-ui') -Value 'Isolated offline attachment UI test' -Encoding utf8

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Add-Pass([string]$Message) { $script:checks.Add($Message); Write-Output "PASS $Message" }
function Save-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
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
        if ($script:app -and $script:app.HasExited) { throw "App exited waiting for '$Id' (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for UIA element '$Id'."
}
function Wait-ProcessElement([string]$Id, [int]$Seconds = 12) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($popup in $windows) { $element = Find-Element $popup $Id; if ($element) { return $element } }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for popup UIA element '$Id'."
}
function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    Assert ($null -ne $Element -and $Element.Current.IsEnabled) "UI element missing or disabled: $Description"
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke(); return
    }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return
    }
    throw "No invoke or selection pattern for $Description."
}
function Set-Check([Windows.Automation.AutomationElement]$Element, [bool]$Wanted, [string]$Description) {
    Assert ($null -ne $Element) "UI element missing: $Description"
    $pattern = $null
    Assert ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) "$Description has no TogglePattern."
    $toggle = [Windows.Automation.TogglePattern]$pattern
    $isChecked = $toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    if ($isChecked -ne $Wanted) { $toggle.Toggle() }
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($script:app -and $script:app.HasExited) { throw "App exited waiting for $Description (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
function Results { Invoke-RestMethod -Uri "$script:endpoint/test/results" -TimeoutSec 3 }
function Wait-DiscordAction([string]$Thread, [string]$Decision, [int]$Seconds = 20) {
    Wait-For {
        $snapshot = Results
        foreach ($message in $snapshot.messages) {
            if ($message.channel_id -ne $Thread) { continue }
            foreach ($row in $message.components) {
                foreach ($button in $row.components) {
                    if ($button.custom_id -match "^cab1\..+\.$Decision$") { return $true }
                }
            }
        }
        return $false
    } "Discord $Decision action button in thread $Thread" $Seconds
    $snapshot = Results
    foreach ($message in $snapshot.messages) {
        if ($message.channel_id -ne $Thread) { continue }
        foreach ($row in $message.components) {
            foreach ($button in $row.components) {
                if ($button.custom_id -match "^cab1\..+\.$Decision$") { return @{ messageId = $message.id; customId = $button.custom_id } }
            }
        }
    }
    throw "Discord $Decision action button disappeared from thread $Thread."
}
function Click-DiscordAction($Action, [string]$Thread, [string]$User, [string]$Guild = '223456789012345678') {
    $body = @{ messageId = $Action.messageId; customId = $Action.customId; channel = $Thread; user = $User; guild = $Guild } |
        ConvertTo-Json -Compress
    Invoke-RestMethod -Uri "$script:endpoint/test/click" -Method Post -ContentType 'application/json' `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 4
}
function Send-DiscordRequest([string]$Id, [string]$Text, [string]$User = '123456789012345678', [string]$Guild = '223456789012345678', [string]$Thread = '', [string]$Parent = '323456789012345678', [bool]$ResolveParent = $false, $Attachments = @()) {
    $body = @{ id = $Id; text = $Text; user = $User; guild = $Guild; attachments = @($Attachments) }
    if ($Thread) { $body.threadId = $Thread; $body.parent = $Parent }
    if ($ResolveParent) { $body.resolveParent = $true }
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    Invoke-RestMethod -Uri "$script:endpoint/test/request" -Method Post -ContentType 'application/json' `
        -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 4
}
function Read-History {
    $path = Join-Path $script:data 'history.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Records) } catch { return @() }
}
function Read-InboxDocument {
    $path = Join-Path $script:data 'chat-conversations.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Select-InboxConversation([string]$Text) {
    $list = Wait-Element 'InboxConversations'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $rows = $list.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem))
        foreach ($row in $rows) {
            $rowText = [Collections.Generic.List[string]]::new()
            if (-not [string]::IsNullOrWhiteSpace($row.Current.Name)) { $rowText.Add($row.Current.Name) }
            foreach ($child in $row.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
                if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $rowText.Add($child.Current.Name) }
            }
            if (($rowText -join "`n") -like "*$Text*") {
                Invoke-Element $row "select inbox request '$Text'"
                return
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "No inbox conversation contained '$Text'."
}
function Select-InboxRequest([string]$Text) {
    $picker = Wait-Element 'ChatRequests'
    $expandPattern = $null
    if ($picker.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern) -and
        ([Windows.Automation.ExpandCollapsePattern]$expandPattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) {
        ([Windows.Automation.ExpandCollapsePattern]$expandPattern).Expand()
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $options = $picker.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem))
        foreach ($option in $options) {
            if ($option.Current.Name -like "*$Text*") {
                Invoke-Element $option "select inbox request detail '$Text'"
                return
            }
        }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "No request option contained '$Text'."
}
function Start-IsolatedApp {
    New-Item -ItemType Directory -Force -Path $script:workspace | Out-Null
    $portFile = Join-Path $script:root 'discord-fixture-port.txt'
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixtureScript = Join-Path $PSScriptRoot 'fixtures/discord-chat-server.cjs'
    $script:fixture = Start-Process -FilePath $node -ArgumentList @('"' + $fixtureScript + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath $portFile } 'loopback Discord fixture startup' 10
    $script:endpoint = 'http://127.0.0.1:' + (Get-Content -LiteralPath $portFile -Raw).Trim()
    Add-Type -AssemblyName System.Security
    $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-discord-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $settings = @{
        SchemaVersion = 1; WorkspaceRoot = $script:workspace; TrustedWorkspaceRoots = $script:workspace
        AgentProvider = 'claude'; AgentMode = 'exec'; AgentExecutable = $agentPath; AgentArguments = ''
        AgentModel = 'offline-discord-model'; AgentPermissionMode = 'workspace-write'; RequireApproval = $false; AllowAgentNetwork = $false
        MattermostServerUrl = ''; MattermostChannelId = ''; MattermostToken = ''; MattermostAllowedUserIds = ''; MattermostAllowedThreadIds = ''
        SlackBotToken = ''; SlackAppToken = ''; SlackAllowedUserIds = ''; SlackAllowedChannelIds = ''; SlackAllowedThreadIds = ''
        DiscordBotToken = $token; DiscordAllowedUserIds = '123456789012345678,123456789012345679'; DiscordAllowedGuildIds = '223456789012345678'
        DiscordAllowedChannelIds = ''; DiscordAllowedThreadIds = ''; DiscordAllowDirectMessages = $false
        ChatApprovalMode = 'first'; ChatResultMode = 'auto'; ChatFollowUpMode = 'queue'; AutoConnectProvider = 'discord'
        PostConnectionNotice = $false; ShareApprovalsInChat = $true; ShareAgentQuestionsInChat = $true
        FileAttachmentApprovalMode = 'always'; FileAttachmentMaxSizeMb = 10; FileAttachmentMaxCount = 3; FileAttachmentMaxTotalSizeMb = 20
    }
    Save-Json (Join-Path $script:data 'settings.json') $settings
    Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = $Language }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = 'light' }
    $policy = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read the current consent policy version.'
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false; $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Arguments = '--data-dir "' + $script:data + '"'
    $start.Environment['CAB_DISCORD_FIXTURE_URL'] = $script:endpoint
    $start.Environment['CAB_FILE_ATTACHMENTS_WORKSPACE'] = $script:workspace
    $start.Environment['CAB_FILE_ATTACHMENTS_TRANSCRIPT'] = $script:agentTranscript
    $script:app = [Diagnostics.Process]::Start($start)
    Wait-For { (Results).connected -eq 1 } 'Discord Gateway READY' 30
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $windows) { if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break } }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($null -ne $script:window) 'No accessible desktop app window appeared.'
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [DiscordBridgeSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    [void][DiscordBridgeSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 25, 25, [int](1180 * $scale), [int](850 * $scale), 0x0010)
}
function Stop-IsolatedApp {
    if ($script:app -and -not $script:app.HasExited) { try { $script:app.Kill(); $script:app.WaitForExit(7000) | Out-Null } catch { } }
    if ($script:fixture -and -not $script:fixture.HasExited) { try { $script:fixture.Kill(); $script:fixture.WaitForExit(3000) | Out-Null } catch { } }
}
function Restart-ExistingApp {
    if ($script:app -and -not $script:app.HasExited) { $script:app.Kill(); $script:app.WaitForExit(7000) | Out-Null }
    $script:app = $null; $script:window = $null
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false; $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Arguments = '--data-dir "' + $script:data + '"'
    $start.Environment['CAB_DISCORD_FIXTURE_URL'] = $script:endpoint
    $start.Environment['CAB_FILE_ATTACHMENTS_WORKSPACE'] = $script:workspace
    $start.Environment['CAB_FILE_ATTACHMENTS_TRANSCRIPT'] = $script:agentTranscript
    $script:app = [Diagnostics.Process]::Start($start)
    Wait-For { (Results).connected -eq 1 } 'Discord reconnect after desktop restart' 30
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $windows) { if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break } }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($null -ne $script:window) 'No application window appeared after restart.'
}

try {
    Start-IsolatedApp
    Invoke-Element (Wait-Element 'NavChat') 'open chat inbox'
    Wait-Element 'InboxConversations' | Out-Null

    # Unauthorized users and guilds must be ignored before creating sessions/threads.
    $before = @(Read-History).Count
    [void](Send-DiscordRequest ('denied-user-' + [guid]::NewGuid().ToString('N')) '!cab must-not-run' '123456789012345680')
    [void](Send-DiscordRequest ('denied-guild-' + [guid]::NewGuid().ToString('N')) '!cab must-not-run' '123456789012345678' '223456789012345679')
    Start-Sleep -Milliseconds 800
    Assert (@((Results).threads).Count -eq 0) 'Denied user or guild created a public Discord thread.'
    Assert (@(Read-History).Count -eq $before) 'Denied user or guild created agent history.'
    Add-Pass 'Denied user and guild messages are discarded before thread creation or agent history'

    $requestText = 'discord fixture approved request'
    [void](Send-DiscordRequest ('allowed-' + [guid]::NewGuid().ToString('N')) "!cab $requestText")
    Wait-For { @((Results).threads).Count -eq 1 } 'allowed message to create its dedicated Discord thread'
    $firstThread = (Results).threads[0].id
    $approve = Wait-DiscordAction $firstThread 'approve'
    $beforeWrongOwner = @(Read-History).Count
    [void](Click-DiscordAction $approve $firstThread '123456789012345678' '223456789012345679')
    Start-Sleep -Milliseconds 450
    Assert (@(Read-History).Count -eq $beforeWrongOwner) 'An approval interaction from a different guild was accepted.'
    [void](Click-DiscordAction $approve $firstThread '123456789012345679')
    Start-Sleep -Milliseconds 600
    Assert (@(Read-History).Count -eq $beforeWrongOwner) "A different allowed Discord user approved another user's request."
    [void](Click-DiscordAction $approve $firstThread '123456789012345678')
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -ge 1 } 'approved TestAgent run completion'
    Wait-For { @((Results).messages | Where-Object { $_.channel_id -eq $firstThread -and $_.content -match 'No fixture file requested\.' }).Count -ge 1 } 'Discord response in the owned thread'
    [void](Click-DiscordAction $approve $firstThread '123456789012345678')
    Start-Sleep -Milliseconds 450
    Assert (@(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 1) 'A replayed Discord approval started an extra agent run.'
    Add-Pass 'Discord approval buttons reject wrong-guild, wrong-owner, and replayed actions; the owner starts the TestAgent in its thread'

    $completedBeforeReject = @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count
    [void](Send-DiscordRequest ('reject-' + [guid]::NewGuid().ToString('N')) '!cab this request is rejected')
    Wait-For { (Results).threads.Count -eq 2 } 'rejected request thread creation'
    $rejectThread = (Results).threads[1].id
    $reject = Wait-DiscordAction $rejectThread 'reject'
    [void](Click-DiscordAction $reject $rejectThread '123456789012345678')
    Start-Sleep -Milliseconds 900
    Assert (@(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq $completedBeforeReject) 'Rejecting a Discord request started an agent run.'
    Add-Pass 'Rejecting the Discord action leaves the TestAgent run count unchanged'

    $filePayload = 'CAB_DISCORD_INCOMING_FILE offline fixture payload'
    $attachment = @{ id = 'inbound-' + [guid]::NewGuid().ToString('N'); name = 'discord-note.txt'; content = $filePayload; contentType = 'text/plain' }
    [void](Send-DiscordRequest ('incoming-file-' + [guid]::NewGuid().ToString('N')) '!cab inspect the attached offline note' '123456789012345678' '223456789012345678' '' '323456789012345678' $false @($attachment))
    Wait-For { (Results).threads.Count -eq 3 } 'incoming attachment request thread creation'
    $fileThread = (Results).threads[2].id
    $fileApproval = Wait-DiscordAction $fileThread 'approve'
    Assert (@((Results).fileDownloads).Count -eq 0) 'Incoming attachment bytes were fetched before the owner approved the Discord request.'
    [void](Click-DiscordAction $fileApproval $fileThread '123456789012345678')
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -ge 2 } 'incoming attachment TestAgent run completion'
    Wait-For { (Results).fileDownloads.Count -eq 1 } 'consented incoming file download'
    $calls = @(Get-Content -LiteralPath $script:agentTranscript | ForEach-Object { $_ | ConvertFrom-Json })
    $fileCall = $calls | Where-Object { $_.prompt -like '*discord-note.txt*' } | Select-Object -Last 1
    Assert ($null -ne $fileCall) 'The approved attachment filename was not supplied to the TestAgent.'
    $cdnRequest = @((Results).restRequests | Where-Object { $_.path -like '/cdn/*' } | Select-Object -Last 1)[0]
    Assert ($null -ne $cdnRequest -and -not $cdnRequest.hasAuthorization) 'Discord CDN download unexpectedly carried bot authorization.'
    Add-Pass 'Incoming Discord attachments are not downloaded before owner approval and reach the TestAgent only after approval'

    [void](Send-DiscordRequest ('outgoing-file-' + [guid]::NewGuid().ToString('N')) 'CREATE ATTACHMENT first.txt' '123456789012345678' '223456789012345678' $firstThread '323456789012345678' $true)
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -ge 3 } 'same-owner outgoing attachment TestAgent run'
    Select-InboxConversation 'CREATE ATTACHMENT first.txt'
    Select-InboxRequest 'CREATE ATTACHMENT first.txt'
    Wait-For { $button = Find-Element $script:window 'SendChatAttachments'; $null -ne $button -and $button.Current.IsEnabled } 'generated Discord file to become available for sharing'
    Invoke-Element (Wait-Element 'SendChatAttachments') 'open Discord outgoing attachment picker'
    $attachmentCheck = Wait-ProcessElement 'AttachmentFile1'
    Set-Check $attachmentCheck $true 'select generated attachment'
    Assert (@((Results).uploads).Count -eq 0) 'Generated files were uploaded before PC consent.'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm outgoing attachment upload'
    Wait-For { (Results).uploads.Count -eq 1 } 'consented outgoing Discord attachment upload'
    Assert ((Results).uploads[0].channel_id -eq $firstThread) 'Outgoing attachment was uploaded outside its owning thread.'
    Add-Pass 'Generated Discord files are uploaded only after explicit PC selection and confirmation'

    [void](Send-DiscordRequest ('pending-restart-' + [guid]::NewGuid().ToString('N')) '!cab pending request must not resume after restart')
    Wait-For { (Results).threads.Count -eq 4 } 'pending restart-safety thread creation'
    $pendingThread = (Results).threads[3].id
    $staleApproval = Wait-DiscordAction $pendingThread 'approve'
    $historyBeforeRestart = @(Read-History).Count
    $threadPostsBeforeRestart = @((Results).restRequests | Where-Object { $_.method -eq 'POST' -and $_.path -eq "/api/v10/channels/$pendingThread/messages" }).Count
    Restart-ExistingApp
    Invoke-Element (Wait-Element 'NavChat') 'open Discord inbox after restart'
    Wait-Element 'InboxConversations' | Out-Null
    $persisted = Read-InboxDocument
    $firstEntry = @($persisted.Entries | Where-Object { $_.Key.ThreadId -eq $firstThread -and $_.Key.Origin -eq 'discord' }) | Select-Object -First 1
    Assert ($null -ne $firstEntry -and $firstEntry.Key.UserId -eq '123456789012345678' -and $firstEntry.RequestCount -ge 2) 'Discord thread ownership/request metadata did not survive restart.'
    [void](Click-DiscordAction $staleApproval $pendingThread '123456789012345678')
    [void](Send-DiscordRequest ('wrong-owner-after-restart-' + [guid]::NewGuid().ToString('N')) '!cab must remain denied after restart' '123456789012345679' '223456789012345678' $pendingThread '323456789012345678' $true)
    Start-Sleep -Milliseconds 900
    Assert (@(Read-History).Count -eq $historyBeforeRestart) 'A stale button or different owner started a pending request after restart.'
    Assert (@((Results).restRequests | Where-Object { $_.method -eq 'POST' -and $_.path -eq "/api/v10/channels/$pendingThread/messages" }).Count -eq $threadPostsBeforeRestart) 'A pending request was automatically reposted after restart.'
    Add-Pass 'Discord ownership metadata persists; stale actions and other allowed users cannot resume a pending thread after restart'

    # Force parent resolution through GET /channels/{thread}; that endpoint includes guild_id,
    # while source-message retrieval below intentionally omits it like the real API.
    $completedBeforeContinuation = @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count
    $continuationId = 'continue-' + [guid]::NewGuid().ToString('N')
    $continuationText = 'continue-owned-thread-' + [guid]::NewGuid().ToString('N')
    [void](Send-DiscordRequest $continuationId $continuationText '123456789012345678' '223456789012345678' $firstThread '323456789012345678' $true)
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -ge ($completedBeforeContinuation + 1) } 'same-owner Discord thread continuation'
    $continuationCall = @(Get-Content -LiteralPath $script:agentTranscript | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.prompt -like "*$continuationText*" }) | Select-Object -Last 1
    Assert ($null -ne $continuationCall) "TestAgent did not receive the specific same-owner message $continuationId."
    $continuationHistory = @(Read-History | Where-Object { $_.ThreadId -eq $firstThread -and $_.UserId -eq '123456789012345678' -and $_.Status -eq 'completed' })
    Assert ($continuationHistory.Count -ge 1) 'The same-owner continuation was not recorded against its original Discord owner.'
    $edits = @((Results).restRequests | Where-Object { $_.method -eq 'PATCH' -and $_.path -like "/api/v10/channels/$firstThread/messages/*" })
    Assert ($edits.Count -gt 0) 'Discord response update was not routed to the actual thread channel.'
    Add-Pass 'Same owner can continue in the resolved thread; message updates target the thread channel'

    $historyBeforeDeniedOwner = @(Read-History).Count
    $conversationBeforeDeniedOwner = Read-InboxDocument
    $ownedEntryBeforeDeniedOwner = @($conversationBeforeDeniedOwner.Entries | Where-Object { $_.Key.ThreadId -eq $firstThread -and $_.Key.Origin -eq 'discord' }) | Select-Object -First 1
    Assert ($null -ne $ownedEntryBeforeDeniedOwner) 'The owned Discord conversation disappeared before the owner-isolation check.'
    $wrongOwnerId = 'wrong-owner-' + [guid]::NewGuid().ToString('N')
    $wrongOwnerText = 'wrong-owner-must-not-run-' + [guid]::NewGuid().ToString('N')
    [void](Send-DiscordRequest $wrongOwnerId $wrongOwnerText '123456789012345679' '223456789012345678' $firstThread '323456789012345678' $true)
    Start-Sleep -Milliseconds 900
    Assert (@(Read-History).Count -eq $historyBeforeDeniedOwner) "A different Discord user continued another owner's thread."
    $wrongOwnerCall = @(Get-Content -LiteralPath $script:agentTranscript | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.prompt -like "*$wrongOwnerText*" })
    Assert ($wrongOwnerCall.Count -eq 0) "TestAgent received the denied Discord message $wrongOwnerId."
    $conversationAfterDeniedOwner = Read-InboxDocument
    $ownedEntryAfterDeniedOwner = @($conversationAfterDeniedOwner.Entries | Where-Object { $_.Key.ThreadId -eq $firstThread -and $_.Key.Origin -eq 'discord' }) | Select-Object -First 1
    Assert ($ownedEntryAfterDeniedOwner.RequestCount -eq $ownedEntryBeforeDeniedOwner.RequestCount) 'A different user incremented the owned Discord thread request count.'
    Add-Pass 'A different Discord user cannot continue the owned Discord thread'

    $evidence = @{ passed = $true; checks = $script:checks.ToArray(); dataDirectory = $script:data
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); fixture = 'loopback Discord REST/Gateway plus local TestAgent; no external services' }
    Save-Json (Join-Path $script:root 'results.json') $evidence
    Write-Output "Evidence: $script:root"
}
catch {
    try {
        Save-Json (Join-Path $script:root 'failure.json') @{ error = $_.ToString(); checks = $script:checks.ToArray(); dataDirectory = $script:data }
        if ($script:endpoint) { Save-Json (Join-Path $script:root 'fixture-results.json') (Results) }
    } catch { }
    throw
}
finally { Stop-IsolatedApp }
