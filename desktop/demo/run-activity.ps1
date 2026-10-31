# Plays the History and usage demo (Japanese, CAB only): saved conversations, the usage calendar for a busy day,
# its executions, the usage charts and the audit trail. Needs the data from `reset-demo.ps1 -SeedActivity`.
# -Record captures the primary monitor work area into demo-activity-raw.mkv.
param([switch]$Record)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-lib.ps1')
$log = Join-Path $DemoData 'run-activity.log'
function Mark([string]$Text) { $line = "{0:HH:mm:ss.fff} $Text" -f (Get-Date); $line; Add-Content $log $line }
Set-Content $log ''

# Only CAB is shown, so close the Mattermost window left over from an earlier demo.
$profile = Join-Path $DemoData 'chrome-profile'
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# The day cell with the most executions, so the click shows a meaningful list.
function Get-BusiestDayId {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $best = $null; $bestCount = 0
    foreach ($item in (Get-CabWindow).FindAll('Descendants', $c)) {
        $id = $item.Current.AutomationId
        if ($id -notlike 'ActivityDay_*') { continue }
        if ($item.Current.Name -match '：(\d+)回' -and [int]$Matches[1] -gt $bestCount) { $best = $id; $bestCount = [int]$Matches[1] }
    }
    $best
}

Move-Cursor 1380 600 10
& (Join-Path $PSScriptRoot 'stage.ps1')
$ffmpeg = if ($Record) { Start-DemoRecording 'demo-activity' }
Mark 'start'
try {
    Start-Sleep 2
    Invoke-Cab 'NavHistory'; Mark 'history page'
    Start-Sleep 3

    # Saved conversations: three different agents.
    foreach ($index in 0, 1, 2) {
        Select-CabFirstItem 'SavedSessionsList' 15 $index
        Mark "conversation $index"
        Start-Sleep 3
    }

    Invoke-Cab 'ActivityTab'; Mark 'usage tab'
    Start-Sleep 4
    $day = Get-BusiestDayId
    Invoke-Cab $day; Mark "day $day"
    Start-Sleep 2.5
    Invoke-Cab 'ActivityOpenDay'; Mark 'executions of the day'
    Start-Sleep 4
    try { Invoke-Cab 'ClearExecutionDayFilter' 3; Start-Sleep 1 } catch { }

    Invoke-Cab 'ActivityTab'
    Start-Sleep 2
    foreach ($step in 1..3) { Scroll-Cab 7 1100 600; Mark "usage scroll $step"; Start-Sleep 3.5 }
    Scroll-Cab -30 1100 600 60
    Start-Sleep 1.5

    Invoke-Cab 'AuditHistoryTab'; Mark 'audit trail'
    Start-Sleep 4
    Scroll-Cab 5 1100 600
    Start-Sleep 3
    Mark 'end'
}
finally {
    if ($ffmpeg) { Mark "recording saved: $(Stop-DemoRecording $ffmpeg)" }
    & (Join-Path $PSScriptRoot 'stage.ps1') -Off
}
