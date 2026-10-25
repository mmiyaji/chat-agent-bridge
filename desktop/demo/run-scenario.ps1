# Plays a feature demo in Japanese: "project" switches the conversation's folder, "agent" hands the conversation to Claude Code.
param([ValidateSet('project', 'agent')][string]$Scenario, [switch]$Record, [switch]$AutoSwitch)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-lib.ps1')
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$ws = Connect-Cdp
$botId = $mm.'cab-bot'.id
$log = Join-Path $DemoData 'run-scenario.log'
function Mark([string]$Text) { $line = "{0:HH:mm:ss.fff} $Text" -f (Get-Date); $line; Add-Content $log $line }
Set-Content $log ''

# Forward slashes: Mattermost markdown treats "\." as an escape and drops the backslash.
$todo = (Join-Path $DemoData 'todo-cli').Replace('\', '/')
$steps = @{
    project = @{
        Request = '@cab-bot README.md を読んで、このプロジェクトの概要を2行で教えて'
        Switch = "folder $todo"
        FollowUp = 'このプロジェクトの README.md を読んで、使い方を2行で教えて'
    }
    agent = @{
        Request = '@cab-bot weather.py を読んで、対応している都市を教えて'
        Switch = '!cab agent claude summary'
        FollowUp = '続けて、weather.py に福岡の予報を追加して（テストの実行は不要です）'
    }
}[$Scenario]

function Get-BotDoneCount {
    [int](Invoke-Js $ws "fetch('/api/v4/channels/$($mm.channelId)/posts?since=$script:since',{headers:{'X-Requested-With':'XMLHttpRequest'}}).then(r=>r.json()).then(j=>Object.values(j.posts).filter(p=>p.user_id==='$botId' && p.message.startsWith('✅')).length)")
}
function Wait-BotDone([int]$Count) {
    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) { Start-Sleep 2; if ((Get-BotDoneCount) -ge $Count) { return $true } }
    $false
}
# Approves the pending request in CAB when one appears, then confirms the dialog with the given button.
function Approve-Pending([string]$Button, [int]$TimeoutSec = 20) {
    try { $null = Find-Cab 'ApproveChat' 2 } catch {
        # An open conversation keeps showing its earlier request; pick the waiting one from the request list.
        if (-not (Select-CabPendingRequest $TimeoutSec)) { return $false }
        try { $null = Find-Cab 'ApproveChat' 5 } catch { return $false }
    }
    Start-Sleep 2
    Invoke-Cab 'ApproveChat'
    Start-Sleep 2
    Click-CabButtonByName $Button
    $true
}
function Complete-ApprovalStep([string]$Step, [string]$Button, [int]$TimeoutSec = 20, [switch]$Automatic) {
    if ($Automatic) { Mark "$Step approval: automatic"; return }
    if (-not (Approve-Pending $Button $TimeoutSec)) { throw "Timed out waiting for approval: $Step" }
    Mark "$Step approved"
}
function Require-BotResponse([string]$Step, [int]$Count) {
    if (-not (Wait-BotDone $Count)) { throw "Timed out waiting for Mattermost response: $Step" }
    Mark "$Step reply received"
}

Move-Cursor 1380 600 10
& (Join-Path $PSScriptRoot 'stage.ps1')
$ffmpeg = if ($Record) { Start-DemoRecording "demo-$Scenario" }
Mark 'start'
try {
    Start-Sleep 2
    Invoke-Cab 'NavChat'; Mark 'chat page'
    Start-Sleep 2
    Invoke-Cab 'ConnectMattermost'; Mark 'connect'
    Start-Sleep 5

    $script:since = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    Send-MattermostMessage $ws $steps.Request; Mark 'request posted'
    Select-CabFirstItem 'InboxConversations' 60; Mark 'conversation opened'
    Complete-ApprovalStep 'initial request' '実行' -Automatic:$AutoSwitch
    Move-Cursor 1380 820 900
    Require-BotResponse 'initial request' 1
    Start-Sleep 4

    # Switch inside the same thread; CAB asks for approval because it changes how the conversation runs.
    Mark (Open-LatestThread $ws)
    Start-Sleep 1
    Send-MattermostMessage $ws $steps.Switch -Box '#reply_textbox'; Mark 'switch posted'
    Complete-ApprovalStep 'switch' '変更' -Automatic:$AutoSwitch
    Move-Cursor 1380 820 900
    Start-Sleep 6

    $done = Get-BotDoneCount
    Send-MattermostMessage $ws $steps.FollowUp -Box '#reply_textbox'; Mark 'follow-up posted'
    Complete-ApprovalStep 'follow-up request' '実行' 10 -Automatic:($AutoSwitch -and $Scenario -eq 'project')
    Move-Cursor 1380 820 900
    Require-BotResponse 'follow-up request' ($done + 1)
    Move-ToSelector $ws '#reply_textbox'
    Start-Sleep 6
    Mark 'end'
}
finally {
    if ($ffmpeg) { Mark "recording saved: $(Stop-DemoRecording $ffmpeg)" }
    & (Join-Path $PSScriptRoot 'stage.ps1') -Off
}
