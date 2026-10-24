# Offline native smoke for inbound chat attachments and expanded !cab status.
# Publish an unpackaged desktop app and a self-contained TestAgent before running.
param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/incoming-files-final'),
    [string]$TestAgentPath = (Join-Path $PSScriptRoot 'artifacts/settings-scope-test-agent/ChatAgentBridge.TestAgent.exe'),
    [ValidateSet('en', 'ja')][string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AppDirectory).Path)
$agentPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $TestAgentPath).Path)
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or -not [IO.Path]::IsPathFullyQualified($TestAgentPath)) { throw 'AppDirectory and TestAgentPath must be absolute paths.' }
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) { throw 'Use an isolated unpackaged portable publish under desktop/artifacts.' }
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf)) { throw "Desktop executable was not found: $AppDirectory" }
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $agentPath) 'coreclr.dll'))) { throw "Use a self-contained TestAgent publish: $TestAgentPath" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
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
public static class IncomingFilesSmokeInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
$script:PSScriptRootForFixture = $PSScriptRoot
$connectionSmoke = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ConnectionSettingsUiSmoke.ps1') -Raw
$functionStart = $connectionSmoke.IndexOf('function Assert(')
$testStart = [regex]::Match($connectionSmoke, '(?m)^try \{\r?\n    Start-IsolatedApp')
if ($functionStart -lt 0 -or -not $testStart.Success) { throw 'Could not extract isolated connection smoke helpers.' }
$helperSource = $connectionSmoke.Substring($functionStart, $testStart.Index - $functionStart).Replace('$PSScriptRoot', '$PSScriptRootForFixture')
$helperSource = $helperSource.Replace("Save-Json (Join-Path `$script:data 'language.json') @{ Language = 'en' }", "Save-Json (Join-Path `$script:data 'language.json') @{ Language = '$Language' }")
$theme = if ($Language -eq 'ja') { 'dark' } else { 'light' }
$helperSource = $helperSource.Replace("Save-Json (Join-Path `$script:data 'appearance.json') @{ Theme = 'light' }", "Save-Json (Join-Path `$script:data 'appearance.json') @{ Theme = '$theme' }")
$helperSource = $helperSource.Replace("MattermostAllowedUserIds = 'alice';", "MattermostAllowedUserIds = 'alice,bob';")
. ([scriptblock]::Create($helperSource))

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

function Save-Evidence {
    Save-Json (Join-Path $script:root 'results.json') @{
        passed = $true; checks = $script:results.ToArray(); dataDirectory = $script:data
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        fixture = 'loopback Mattermost plus local TestAgent; no external chat or AI service'
    }
}

function Send-SmokeRequest([string]$Id, [string]$Thread, [string]$Text, [string]$User = 'alice', $Files = @(), [bool]$InvalidFileIds = $false) {
    $body = @{ id = $Id; thread = $Thread; user = $User; text = $Text; mention = $false; plain = $false; files = @($Files); invalidFileIds = $InvalidFileIds } |
        ConvertTo-Json -Depth 8 -Compress
    $response = Invoke-RestMethod -Uri "$script:endpoint/test/request" -Method Post -ContentType 'application/json' `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 3
    Assert ($response.delivered -eq 1) 'Fixture request did not reach exactly one connected transport.'
}

function Read-InboxDocument {
    $path = Join-Path $script:data 'chat-conversations.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Wait-Reply([string]$Thread, [scriptblock]$ReplyPredicate, [string]$Description, [int]$Seconds = 25) {
    Wait-For { @((Results).replies | Where-Object { $_.root_id -eq $Thread -and (& $ReplyPredicate $_) }).Count -gt 0 } $Description $Seconds
    @((Results).replies | Where-Object { $_.root_id -eq $Thread -and (& $ReplyPredicate $_) } | Select-Object -Last 1)[0]
}

function Get-WindowText([Windows.Automation.AutomationElement]$Element) {
    $texts = [Collections.Generic.List[string]]::new()
    if ($Element -and -not [string]::IsNullOrWhiteSpace($Element.Current.Name)) { $texts.Add($Element.Current.Name) }
    if ($Element) {
        foreach ($child in $Element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $texts.Add($child.Current.Name) }
        }
    }
    $texts -join "`n"
}

function Select-InboxConversation([string]$Prompt) {
    $list = Wait-Element 'InboxConversations'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem))
        foreach ($item in $items) {
            if ((Get-WindowText $item).Contains($Prompt, [StringComparison]::Ordinal)) {
                $pattern = $null
                if ($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                    ([Windows.Automation.SelectionItemPattern]$pattern).Select()
                } else { Invoke-Element $item "select inbox request '$Prompt'" }
                $bounds = $item.Current.BoundingRectangle
                if (-not $item.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) {
                    [void][IncomingFilesSmokeInput]::SetForegroundWindow([IntPtr]$script:window.Current.NativeWindowHandle)
                    if (-not [IncomingFilesSmokeInput]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))) {
                        throw "Could not point at inbox row '$Prompt'."
                    }
                    [IncomingFilesSmokeInput]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
                    [IncomingFilesSmokeInput]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
                    Start-Sleep -Milliseconds 300
                }
                return
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Could not find an inbox row containing '$Prompt'."
}

function Capture-Window([string]$Name) {
    if (-not $script:window) { return }
    $hwnd = $script:window.Current.NativeWindowHandle
    if ($hwnd -eq 0) { throw 'Refusing to capture an invalid app window.' }
    & winapp ui screenshot -w $hwnd -o (Join-Path $script:root $Name) --json | Out-Null
    if ($LASTEXITCODE) { throw "Could not capture screenshot '$Name'." }
}

try {
    Start-IsolatedApp
    $settingsPath = Join-Path $script:data 'settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.AgentMode = 'exec'
    $settings.AgentArguments = 'exec --json -'
    $settings.AgentModel = 'incoming-files-fixture-model'
    $settings.ChatApprovalMode = 'always'
    $settings.MattermostAllowedUserIds = 'alice,bob'
    Save-Json $settingsPath $settings
    Invoke-Element (Wait-Element 'NavChat') 'open Chat Inbox'
    Wait-Element 'InboxConversations' | Out-Null
    Wait-For { (Results).connections -eq 1 } 'connected loopback Mattermost transport'

    $workspace = Join-Path $script:data 'workspace'
    $thread = 'incoming-files-' + [guid]::NewGuid().ToString('N')
    $fileId = 'inbound-' + [guid]::NewGuid().ToString('N')
    $fileName = 'approval-payload.txt'
    $filePayload = 'CAB_INCOMING_FILE_CONTENT fixture attachment text'
    $file = @{ id = $fileId; name = $fileName; content = $filePayload; mimeType = 'text/plain' }
    $filePostId = 'file-approval-' + [guid]::NewGuid().ToString('N')
    Send-SmokeRequest $filePostId $thread 'attachment needs approval' 'alice' @($file)
    Wait-For { (Read-InboxDocument).Entries.Count -ge 1 } 'pending attachment inbox item'
    Select-InboxConversation 'attachment needs approval'
    Assert (@((Results).fileInfoRequests).Count -eq 0 -and @((Results).fileDownloads).Count -eq 0) 'Attachment metadata or bytes were fetched before approval.'
    Wait-For { (Wait-Element 'ApproveChat').Current.IsEnabled } 'attachment approval action'
    Invoke-Element (Wait-Element 'ApproveChat') 'approve attachment request'
    $confirmation = Wait-ProcessElement 'PrimaryButton'
    $processWindows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id))
    $confirmationText = (@($processWindows | ForEach-Object { Get-WindowText $_ }) -join "`n")
    $resourceSuffix = if ($Language -eq 'ja') { '.ja' } else { '' }
    [xml]$resources = Get-Content -LiteralPath (Join-Path $PSScriptRoot ("ChatAgentBridge.Desktop/Localization/DesktopStrings$resourceSuffix.resx")) -Raw
    $summaryTemplate = $resources.SelectSingleNode("/root/data[@name='IncomingFilesSummary']/value").InnerText
    $pendingName = $resources.SelectSingleNode("/root/data[@name='IncomingFileNamePending']/value").InnerText
    $expectedSummary = [string]::Format([Globalization.CultureInfo]::CurrentCulture, $summaryTemplate, 1, $pendingName)
    Assert ($confirmationText.Contains($expectedSummary, [StringComparison]::Ordinal)) "Approval confirmation did not show the localized attachment summary: $expectedSummary"
    Assert (@((Results).fileDownloads).Count -eq 0) 'Attachment bytes were fetched before the final run confirmation.'
    Invoke-Element $confirmation 'confirm approved attachment run'
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 1 } 'approved attachment run completion'
    $fileResult = Wait-Reply $thread { param($reply) $reply.message.Contains('codex-e2e:') } 'TestAgent response containing the supplied prompt'
    Assert (@((Results).fileInfoRequests | Where-Object { $_.id -eq $fileId }).Count -eq 2) 'Attachment metadata was not resolved before and during download.'
    Assert (@((Results).incomingPostRequests | Where-Object { $_.id -eq $filePostId -and $_.found }).Count -eq 2) 'Attachment post association was not revalidated before download.'
    Assert (@((Results).fileDownloads | Where-Object { $_.id -eq $fileId -and $_.post_id -eq $filePostId }).Count -eq 1) 'Approved attachment bytes were not downloaded for the source post.'
    Assert ($fileResult.message.Contains('.cab-incoming-', [StringComparison]::Ordinal) -and $fileResult.message.Contains('file-1.txt', [StringComparison]::Ordinal)) 'TestAgent prompt did not contain the staged local attachment path.'
    $promptPattern = $null
    $promptElement = Wait-Element 'InboxSelectedPrompt'
    Assert ($promptElement.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$promptPattern)) 'Selected prompt did not expose its text value.'
    $resolvedPrompt = ([Windows.Automation.ValuePattern]$promptPattern).Current.Value
    Assert ($resolvedPrompt.Contains($fileName, [StringComparison]::Ordinal)) 'Completed request details did not show the resolved attachment filename.'
    Assert (-not $resolvedPrompt.Contains($pendingName, [StringComparison]::Ordinal)) 'Completed request details still showed the pending filename placeholder.'
    Assert (@(Get-ChildItem -LiteralPath $workspace -Directory -Filter '.cab-incoming-*').Count -eq 0) 'Staged attachment directory was not removed after execution.'
    Capture-Window 'approved-attachment.png'
    Add-Pass 'Approval confirmation shows the localized attachment summary; the approved run receives a local path and cleans up staging'
    Invoke-Element (Wait-Element 'InboxBackToConversations') 'return to inbox conversations'

    $rejectedThread = 'incoming-rejected-' + [guid]::NewGuid().ToString('N')
    $rejectedId = 'reject-' + [guid]::NewGuid().ToString('N')
    $rejectedFile = @{ id = $rejectedId; name = 'rejected.txt'; content = 'must not download'; mimeType = 'text/plain' }
    Send-SmokeRequest ('file-reject-' + [guid]::NewGuid().ToString('N')) $rejectedThread 'reject this attachment' 'alice' @($rejectedFile)
    Wait-For { (Read-InboxDocument).Entries.Count -ge 2 } 'rejected attachment inbox row'
    Select-InboxConversation 'reject this attachment'
    Wait-For { (Wait-Element 'RejectChat').Current.IsEnabled } 'attachment rejection action'
    $downloadCountBeforeReject = @((Results).fileDownloads).Count
    Invoke-Element (Wait-Element 'RejectChat') 'reject attachment request'
    Start-Sleep -Milliseconds 700
    Assert (@((Results).fileDownloads).Count -eq $downloadCountBeforeReject) 'Rejected request downloaded attachment bytes.'
    Assert (@(Read-History | Where-Object { $_.Status -eq 'completed' }).Count -eq 1) 'Rejected request started an agent run.'
    Add-Pass 'Rejecting an attachment leaves file download count and agent run count unchanged'
    Invoke-Element (Wait-Element 'InboxBackToConversations') 'return to inbox before malformed attachment test'

    $malformedThread = 'incoming-malformed-' + [guid]::NewGuid().ToString('N')
    Send-SmokeRequest -Id ('file-malformed-' + [guid]::NewGuid().ToString('N')) -Thread $malformedThread -Text 'malformed metadata' -User 'alice' -Files @() -InvalidFileIds $true
    Wait-For { (Read-InboxDocument).Entries.Count -ge 3 } 'malformed attachment inbox row'
    Select-InboxConversation 'malformed metadata'
    Wait-For { (Wait-Element 'ApproveChat').Current.IsEnabled } 'malformed attachment request approval action'
    $startsBeforeMalformed = @(Read-History).Count
    Invoke-Element (Wait-Element 'ApproveChat') 'approve malformed attachment request'
    $malformedConfirmation = Wait-ProcessElement 'PrimaryButton'
    $malformedWindows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id))
    $malformedText = (@($malformedWindows | ForEach-Object { Get-WindowText $_ }) -join "`n")
    $invalidSummary = $resources.SelectSingleNode("/root/data[@name='IncomingFilesInvalid']/value").InnerText
    Assert ($malformedText.Contains($invalidSummary, [StringComparison]::Ordinal)) 'Malformed metadata confirmation omitted the localized invalid-file warning.'
    Invoke-Element $malformedConfirmation 'confirm malformed attachment request'
    $failedBody = $resources.SelectSingleNode("/root/data[@name='IncomingFilesFailed']/value").InnerText
    $failedReply = Wait-Reply $malformedThread { param($reply) $reply.message.Contains($failedBody, [StringComparison]::Ordinal) } 'localized malformed attachment warning'
    Wait-For { @((Read-InboxDocument).Entries | Where-Object { $_.Key.ThreadId -eq $malformedThread -and $_.State -eq 'failed' }).Count -gt 0 } 'malformed attachment terminal inbox row'
    $malformedEntry = @((Read-InboxDocument).Entries | Where-Object { $_.Key.ThreadId -eq $malformedThread })[0]
    Assert ($malformedEntry.State -eq 'failed') "Malformed attachment did not persist a failed state (actual: '$($malformedEntry.State)')."
    $localizedFailureState = $resources.SelectSingleNode("/root/data[@name='IncomingFilesFailedTitle']/value").InnerText
    $selectedState = Wait-Element 'InboxSelectedState'
    Assert ($selectedState.Current.Name -eq $localizedFailureState) 'Malformed attachment detail did not show its localized failure state.'
    Assert ((Read-History).Count -eq $startsBeforeMalformed + 1) 'Malformed request did not write its terminal history record.'
    Assert (@((Results).fileDownloads).Count -eq $downloadCountBeforeReject) 'Malformed metadata caused a file download.'
    Assert (@((Results).replies | Where-Object { $_.message.Contains('codex-e2e:') }).Count -eq 1) 'Malformed metadata started an agent run.'
    Add-Pass 'Malformed attachment metadata shows a localized warning without download or agent start'

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.AgentMode = 'app-server'
    $settings.AgentArguments = 'app-server'
    $settings.ChatApprovalMode = 'auto'
    Save-Json $settingsPath $settings
    $statusThread = 'status-scope-' + [guid]::NewGuid().ToString('N')
    $otherThread = 'status-other-' + [guid]::NewGuid().ToString('N')
    $gatedFileId = 'gated-' + [guid]::NewGuid().ToString('N')
    $gatedFile = @{ id = $gatedFileId; name = 'gated-payload.txt'; content = 'CAB_GATED_FILE_CONTENT'; mimeType = 'text/plain' }
    $gatedPostId = 'status-running-' + [guid]::NewGuid().ToString('N')
    Send-SmokeRequest $gatedPostId $statusThread 'LIVE_STEER_QUEUE_1 active request with attachment' 'alice' @($gatedFile)
    Wait-For { @(Read-History | Where-Object { $_.Status -eq 'running' }).Count -eq 1 } 'gated app-server request execution'
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/start' -and $_.scenario -eq 'LIVE_STEER_QUEUE_1' }).Count -eq 1 } 'gated TestAgent turn start'
    $stagingDirectories = @(Get-ChildItem -LiteralPath $workspace -Directory -Filter '.cab-incoming-*')
    Assert ($stagingDirectories.Count -eq 1) 'Staged attachment directory was not present while the agent turn was held.'
    $stagedFile = Join-Path $stagingDirectories[0].FullName 'file-1.txt'
    Assert (Test-Path -LiteralPath $stagedFile -PathType Leaf) 'Staged attachment was not present during the held agent run.'
    Assert ((Get-Content -LiteralPath $stagedFile -Raw).Contains('CAB_GATED_FILE_CONTENT')) 'Staged attachment content did not match the loopback file response.'
    Assert (@((Results).fileDownloads | Where-Object { $_.id -eq $gatedFileId -and $_.post_id -eq $gatedPostId }).Count -eq 1) 'Gated request did not download its attached bytes before the agent started.'
    Send-SmokeRequest ('status-queued-' + [guid]::NewGuid().ToString('N')) $statusThread 'LIVE_STEER_QUEUE_2 queued request' 'alice'
    Send-SmokeRequest ('status-bob-' + [guid]::NewGuid().ToString('N')) $statusThread 'LIVE_STEER_OTHER_USER_1 other requester' 'bob'
    Send-SmokeRequest ('status-thread-' + [guid]::NewGuid().ToString('N')) $otherThread 'LIVE_STEER_OTHER_THREAD_1 other thread' 'alice'
    Wait-For {
        $entries = (Read-InboxDocument).Entries
        @($entries | Where-Object { $_.Key.ThreadId -eq $statusThread -and $_.Key.UserId -eq 'alice' -and $_.RequestCount -ge 2 }).Count -eq 1 -and
        @($entries | Where-Object { $_.Key.ThreadId -eq $statusThread -and $_.Key.UserId -eq 'bob' }).Count -eq 1 -and
        @($entries | Where-Object { $_.Key.ThreadId -eq $otherThread -and $_.Key.UserId -eq 'alice' }).Count -eq 1
    } 'all queued status scope requests'
    Send-SmokeRequest ('status-command-' + [guid]::NewGuid().ToString('N')) $statusThread 'status' 'alice'
    $statusReply = Wait-Reply $statusThread { param($reply) $reply.message -match 'Live requests|このスレッドの実行中' } 'expanded status response during a running request'
    Assert ($statusReply.message -match '\(2\)|（2件）') 'Status included another user or thread, or omitted the queued request.'
    Assert ($statusReply.message -match 'Running|実行中') 'Status did not distinguish the active request as running.'
    Assert ($statusReply.message -match 'Queued|自動実行待ち') 'Status did not distinguish the automatic queue.'
    Assert ($statusReply.message.Contains('incoming-files-fixture-model', [StringComparison]::Ordinal)) 'Status omitted the effective conversation model.'
    Assert ($statusReply.message.Contains($workspace, [StringComparison]::Ordinal)) 'Status omitted the effective workspace.'
    Assert ($statusReply.message -match 'Codex') 'Status omitted the effective agent label.'
    $repliesBeforeDenied = @((Results).replies).Count
    Send-SmokeRequest ('status-denied-' + [guid]::NewGuid().ToString('N')) $statusThread 'status' 'charlie'
    Start-Sleep -Milliseconds 900
    Assert (@((Results).replies).Count -eq $repliesBeforeDenied) 'An unauthorized user received a status reply.'
    Add-Pass 'Status lists running and automatic-queue requests in scope, excludes other users and threads, and denies unauthorized users'

    Set-Content -LiteralPath $script:gate -Value 'release gated attachment run' -Encoding utf8
    Wait-For { @(Read-Observations | Where-Object { $_.method -eq 'turn/completed' }).Count -eq 4 } 'all accepted queued requests to complete' 40
    Wait-For { @(Get-ChildItem -LiteralPath $workspace -Directory -Filter '.cab-incoming-*').Count -eq 0 } 'staged attachment cleanup after the gated run'
    Send-SmokeRequest ('status-stale-command-' + [guid]::NewGuid().ToString('N')) $statusThread 'status' 'alice'
    $emptyStatus = Wait-Reply $statusThread { param($reply) $reply.message -match 'No live requests|実行中・待機中の依頼はありません' } 'empty status after queued requests completed'
    Assert ($emptyStatus.message -match 'No live requests|実行中・待機中の依頼はありません') 'Completed request appeared in live status.'
    Add-Pass 'Gated run keeps staged files available until execution ends, then cleans them; completed requests are omitted from status'

    Save-Evidence
    Write-Output "PASS inbound files and chat status UI smoke ($Language): $script:root"
}
catch {
    try { Capture-Window 'failure.png' } catch { try { Capture-FailureScreenshot } catch { } }
    Save-Json (Join-Path $script:root 'results.json') @{
        passed = $false; checks = $script:results.ToArray(); dataDirectory = $script:data
        error = $_.Exception.Message; errorStack = $_.ScriptStackTrace; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    throw
}
finally { Stop-IsolatedApp }
