# Plays the demo scenario: connect CAB, request from Mattermost, approve in CAB, reply lands in Mattermost.
# -Record captures the primary monitor work area with ffmpeg into demo-<lang>-raw.mkv.
param([switch]$Record, [ValidateSet('ja', 'en')][string]$Lang = 'ja')
$ErrorActionPreference = 'Stop'
$text = @{
    ja = @{ Request = '@cab-bot README.md を読んで、このプロジェクトの概要を3行で教えて'; Run = '実行'
            FollowUp = 'ありがとう！ test_weather.py に大阪のテストも追加して（テストの実行は不要です）' }
    en = @{ Request = '@cab-bot Read README.md and summarize this project in three lines'; Run = 'Run'
            FollowUp = 'Thanks! Please also add a test for Osaka to test_weather.py (no need to run the tests)' }
}[$Lang]
. (Join-Path $PSScriptRoot 'demo-lib.ps1')
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$ws = Connect-Cdp
$botId = $mm.'cab-bot'.id
$log = Join-Path $DemoData 'run-demo.log'
function Mark([string]$Text) { $line = "{0:HH:mm:ss.fff} $Text" -f (Get-Date); $line; Add-Content $log $line }
Set-Content $log ''

Move-Cursor 1380 600 10
& (Join-Path $PSScriptRoot 'stage.ps1')
$ffmpeg = if ($Record) { Start-DemoRecording "demo-$Lang" }
Mark 'start'
try {
    Start-Sleep 2
    Invoke-Cab 'NavChat'; Mark 'chat page'
    Start-Sleep 2
    Invoke-Cab 'ConnectMattermost'; Mark 'connect'
    Start-Sleep 5

    $since = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    Send-MattermostMessage $ws $text.Request; Mark 'request posted'

    Select-CabFirstItem 'InboxConversations' 60; Mark 'conversation opened'
    $null = Find-Cab 'ApproveChat' 20; Mark 'approval shown'
    Start-Sleep 3
    Invoke-Cab 'ApproveChat'; Mark 'approved'
    Start-Sleep 2
    Click-CabButtonByName $text.Run; Mark 'run confirmed'
    Move-Cursor 1380 820 900

    # Wait until the bot has finished the given number of runs in Mattermost.
    function Wait-BotDone([int]$Count) {
        $deadline = (Get-Date).AddMinutes(4)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep 2
            $n = Invoke-Js $ws "fetch('/api/v4/channels/$($mm.channelId)/posts?since=$since',{headers:{'X-Requested-With':'XMLHttpRequest'}}).then(r=>r.json()).then(j=>Object.values(j.posts).filter(p=>p.user_id==='$botId' && p.message.startsWith('✅')).length)"
            if ([int]$n -ge $Count) { return $true }
        }
        $false
    }
    Mark "reply: $(Wait-BotDone 1)"
    Start-Sleep 4

    # Continue the same conversation by replying in the thread; follow-ups run without another approval.
    Mark (Open-LatestThread $ws)
    Start-Sleep 1
    Send-MattermostMessage $ws $text.FollowUp -Box '#reply_textbox'; Mark 'follow-up posted'
    Move-Cursor 1380 820 900
    Mark "follow-up reply: $(Wait-BotDone 2)"
    Move-ToSelector $ws '#reply_textbox'
    Start-Sleep 6
    Mark 'end'
}
finally {
    if ($ffmpeg) { Mark "recording saved: $(Stop-DemoRecording $ffmpeg)" }
    & (Join-Path $PSScriptRoot 'stage.ps1') -Off
}
