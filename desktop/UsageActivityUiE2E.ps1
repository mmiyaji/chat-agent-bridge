param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja')
# Use only an isolated, marked fixture. No chat messages or AI requests are sent.
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$output=Join-Path $dataPath ('usage-review-'+$Language)
New-Item $output -ItemType Directory -Force|Out-Null
function Choice([string]$Id,[string]$Option){
    Scroll-To $Id;UI @('invoke',$Id)|Out-Null
    winapp ui invoke $Option -a $AppProcessId --json|Out-Null
    if($LASTEXITCODE){throw "Could not select $Option"}
    Start-Sleep -Milliseconds 200
}
try{
    Resize 1160;Activity;Period '90' 90
    Scroll-To 'ActivityCalendar'
    $start=[DateTime]::Today.AddDays(-89)
    $monday=$start.AddDays(-(([int]$start.DayOfWeek+6)%7))
    if($monday -lt $start){
        $id='ActivityDay_'+$monday.ToString('yyyyMMdd')
        Assert ([bool](Element $id)) 'First Monday placeholder is missing'
        UI @('invoke',$id)|Out-Null
        Wait-Text 'ActivitySelectedDay' $(if($Language -eq 'ja'){'期間外'}else{'outside'})
        Assert (-not (Element 'ActivityOpenDay').Current.IsEnabled) 'Outside date can open history'
    }
    Capture 'calendar-outside.png';Pass 'First Monday has a selectable outside-period placeholder'
    Scroll-To 'UsageMetric';Wait-Text 'UsageHeadline' '40,405';Wait-Text 'UsageCoverage' '25/374'
    Capture 'tokens.png';Pass 'Turn totals and coverage match fixture without cumulative double counting'
    Choice 'UsageMetric' 'UsageMetricCache'
    Wait-Text 'UsageHeadline' '34.4%'
    Scroll-To 'UsageModelChart';Capture 'cache-models.png'
    $chart=Element 'UsageModelChart'
    $bars=$chart.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ProgressBar))
    Assert ($bars.Count -eq 3) 'Reported and unknown model buckets are missing'
    Assert (@($bars|Where-Object {$_.Current.Name.Contains('model-b')}).Count -eq 1) 'Reported model missing'
    Pass 'Cache metric uses percentage scale and preserves reported/unknown models'
    Scroll-To 'UsageDayExpander';UI @('invoke','UsageDayExpander')|Out-Null
    Wait-Visible 'UsageDay' $true
    Scroll-To 'UsageDayDetails';Wait-Text 'UsageDayDetails' '1,030';Wait-Text 'UsageDayDetails' '10.0%'
    Capture 'daily-detail.png';Pass 'Daily details show weighted 10 percent, not 50 percent average'
    Choice 'UsageScope' 'UsageScopeRequest';Wait-Text 'UsageHeadline' '220';Wait-Text 'UsageHeadline' '75.0%'
    Capture 'last-request.png';Pass 'Last request values remain isolated from full execution and cumulative scopes'
    Choice 'UsageScope' 'UsageScopeTurn';Choice 'UsageMetric' 'UsageMetricTokens'
    foreach($theme in @('light','dark')){
        $script:activityMode=$false;Go 'NavAppearance'
        $themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
        Scroll-To $themeId;UI @('invoke',$themeId)|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Activity;Scroll-To 'UsageMetric';Capture "usage-$theme-$width.png"
            foreach($id in @('UsageMetric','UsageScope','UsageDailyChart','UsageModelChart')){
                $r=(Element $id).Current.BoundingRectangle
                $w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($r.Right -le $w.Right -and $r.Left -ge $w.Left) "$id is clipped at $width"
            }
            Scroll-To 'UsageModelChart';Capture "models-$theme-$width.png"
        }
    }
    Pass 'New charts fit Light/Dark at 500 and 1160 DIP'
    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'Charts modified history'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Charts modified bridge settings'
    Pass 'Usage exploration preserves saved history and settings'
}catch{$checks.Add(@{Name='Usage UI';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize}
