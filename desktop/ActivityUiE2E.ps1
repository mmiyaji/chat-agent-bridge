param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja',[switch]$OnlyHelpers)
$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDir)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path (Join-Path $dataPath '.cab-activity-test'))) { throw 'Use a marked isolated activity fixture.' }
$output=Join-Path $dataPath ('activity-review-'+$Language)
New-Item $output -ItemType Directory -Force | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$script:hwnd=[IntPtr]((winapp ui list-windows -a $AppProcessId --json|ConvertFrom-Json|Where-Object title -eq 'Chat Agent Bridge'|Select-Object -First 1).hwnd)
if($script:hwnd -eq [IntPtr]::Zero){throw 'App window missing'}
$script:activityMode=$false
$historyFile=Join-Path $dataPath 'history.json'
$records=@((Get-Content $historyFile -Raw|ConvertFrom-Json).Records)
$historyHash=(Get-FileHash $historyFile).Hash
$settingsHash=(Get-FileHash (Join-Path $dataPath 'settings.json')).Hash
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ActivityLayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function UI([string[]]$Arguments){
    $result=winapp ui @Arguments -w $script:hwnd --json 2>$null
    if($LASTEXITCODE){throw "UI action failed: $($Arguments[0]) $($Arguments[1])"}
    return $result
}
function Element([string]$Id){
    $w=[Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    return $w.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Visible($El){return $El -and -not $El.Current.IsOffscreen -and $El.Current.BoundingRectangle.Height -gt 0}
function Value([string]$Id){return [string](UI @('get-value',$Id)|ConvertFrom-Json).text}
function Wait-Text([string]$Id,[string]$Text){
    $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
    do{if((Value $Id).Contains($Text)){return};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until)
    throw "Unexpected value for $Id"
}
function Wait-Visible([string]$Id,[bool]$Expected){
    $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
    do{if([bool](Visible (Element $Id)) -eq $Expected){return};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until)
    throw "Visibility did not settle for $Id"
}
function Scroll-To([string]$Id){
    $scroll=Element $(if($script:activityMode){'ActivityScroll'}else{'MainContentScroll'})
    Assert ([bool]$scroll) "Missing viewport for $Id"
    $sp=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if($sp.Current.VerticallyScrollable){$sp.SetScrollPercent(-1,0);Start-Sleep -Milliseconds 150}
    for($i=0;$i -lt 55;$i++){
        $target=Element $Id
        if($target){
            $r=$target.Current.BoundingRectangle;$v=$scroll.Current.BoundingRectangle
            $bottom=[Math]::Min($v.Bottom,([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle.Bottom-12)
            if((Visible $target) -and $r.Top -ge $v.Top -and $r.Bottom -le $bottom){return}
        }
        if(-not $sp.Current.VerticallyScrollable -or $sp.Current.VerticalScrollPercent -ge 100){break}
        $sp.SetScrollPercent(-1,[Math]::Min(100,$sp.Current.VerticalScrollPercent+2))
        Start-Sleep -Milliseconds 100
    }
    throw "Could not expose $Id"
}
function Go([string]$Id){
    if(-not (Visible (Element $Id))){UI @('invoke','TogglePaneButton')|Out-Null}
    UI @('invoke',$Id)|Out-Null
    Start-Sleep -Milliseconds 120
}
function Activity {
    $script:activityMode=$false;Go 'NavHistory';UI @('invoke','ActivityTab')|Out-Null
    $script:activityMode=$true;Start-Sleep -Milliseconds 120
}
function Resize([int]$Width){
    $scale=[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
    Assert ([ActivityLayoutNative]::SetWindowPos($script:hwnd,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int](860*$scale),0x0010)) 'Resize failed'
    Start-Sleep -Milliseconds 180
}
function Capture([string]$Name){Start-Sleep -Milliseconds 150;UI @('screenshot','-o',(Join-Path $output $Name))|Out-Null}
function Pass([string]$Text){$checks.Add(@{Name=$Text;Status='PASS'});Write-Output "PASS: $Text"}
function Period([string]$Name,[int]$Count){
    Scroll-To 'ActivityPeriod';UI @('invoke','ActivityPeriod')|Out-Null
    winapp ui invoke "ActivityPeriod$Count" -a $AppProcessId --json|Out-Null
    if($LASTEXITCODE){throw "Missing period $Name"}
    $start=[DateTime]::Today.AddDays(1-$Count)
    $expected=@($records|Where-Object {([DateTimeOffset]$_.StartedAt).LocalDateTime.Date -ge $start -and ([DateTimeOffset]$_.StartedAt).LocalDateTime.Date -le [DateTime]::Today}).Count
    Wait-Text 'ActivityTotal' ($expected.ToString('N0'))
    Assert ((Value 'ActivityTotal') -eq $expected.ToString('N0')) "Incorrect total for $Name"
}
if($OnlyHelpers){return}
try {
    Activity
    foreach($range in @(90,30,365,90,30,365,90)){
        $name=if($Language -eq 'ja'){if($range -eq 365){'1年'}else{"${range}日"}}else{if($range -eq 365){'1 year'}else{"$range days"}}
        Period $name $range
    }
    Pass 'Period changes preserve exact saved execution counts'
    Scroll-To 'ActivityCalendar'
    $dayId='ActivityDay_'+[DateTime]::Today.AddDays(-1).ToString('yyyyMMdd')
    UI @('invoke',$dayId)|Out-Null
    $selection=(Element 'ActivityCalendar').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    Assert ($selection.Count -eq 1) 'Calendar has no selected day'
    Assert ((Value 'ActivitySelectedDay') -eq $selection[0].Current.Name) 'Calendar selection is not exposed as text'
    Scroll-To 'ActivityOpenDay';UI @('invoke','ActivityOpenDay')|Out-Null
    Wait-Visible 'ClearExecutionDayFilter' $true
    Assert (Visible (Element 'ClearExecutionDayFilter')) 'Day filter was not applied'
    Assert (Visible (Element 'ExecutionHistoryList')) 'Day execution list did not open'
    UI @('invoke','ClearExecutionDayFilter')|Out-Null
    Wait-Visible 'ClearExecutionDayFilter' $false
    Assert (-not (Visible (Element 'ClearExecutionDayFilter'))) 'Day filter did not reset'
    Pass 'Selected day opens execution history and clear restores all dates'
    foreach($theme in @('light','dark')){
        $script:activityMode=$false;Go 'NavOverview';Scroll-To $(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'})
        UI @('invoke',$(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}))|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Activity;Scroll-To 'ActivityTotal';Capture "overview-$theme-$width.png"
            foreach($id in @('SavedConversationsTab','ActivityTab','ExecutionHistoryTab','AuditHistoryTab')){
                $el=Element $id
                Assert (Visible $el) "Tab $id is inaccessible at $width"
                Assert ($el.Current.BoundingRectangle.Right -le ([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle.Right) "Tab $id is clipped at $width"
            }
            Scroll-To 'ActivityDailyHistogram';Capture "histogram-$theme-$width.png"
            Scroll-To 'ActivityWeekdayChart';Capture "weekdays-$theme-$width.png"
            (Element 'ActivityScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1,100)
            Start-Sleep -Milliseconds 120;Capture "breakdown-$theme-$width.png"
            foreach($id in @('ActivityWeekdayChart','ActivityModelChart')){
                $r=(Element $id).Current.BoundingRectangle
                Assert ($r.Right -le ([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle.Right) "$id exceeds window at $width"
            }
        }
    }
    Pass 'Light and Dark charts and four history tabs fit at 500 and 1160 DIP'
    Resize 1160;Activity;Period $(if($Language -eq 'ja'){'1年'}else{'1 year'}) 365
    Scroll-To 'ActivityCalendar';Capture 'year-calendar.png'
    Assert ((Value 'ActivitySelectedDay').Length -gt 0) 'Year calendar lost selected day'
    Pass 'Year calendar retains selection'
    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'Charts modified execution history'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Charts modified bridge settings'
    Pass 'Read-only charts preserve history and settings'
}catch{$checks.Add(@{Name='Activity UI';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize}
