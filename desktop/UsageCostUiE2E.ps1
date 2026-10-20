param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja')
$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDir)
if(-not (Test-Path (Join-Path $dataPath '.cab-activity-test')) -or -not (Test-Path (Join-Path $dataPath '.cab-activity-cost-test'))){throw 'Use a marked isolated cost fixture.'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$output=Join-Path $dataPath ('usage-cost-review-'+$Language)
New-Item $output -ItemType Directory -Force|Out-Null
$expectedTotal=if($Language -eq 'ja'){'$34～$35.8'}else{'$34–$35.8'}
function Choice([string]$Id,[string]$Option){
    Scroll-To $Id;UI @('invoke',$Id)|Out-Null
    winapp ui invoke $Option -a $AppProcessId --json|Out-Null
    if($LASTEXITCODE){throw "Could not select $Option"}
    Start-Sleep -Milliseconds 200
}
function CostText([string]$Text){Wait-Text 'UsageCostHeadline' $Text}
function Check-CostChartBounds([int]$Width){
    foreach($id in @('UsageMetric','UsageScope','UsageCostHeadline','UsageCostCoverage','UsageDailyChart','UsageModelChart','UsageCostMethod')){
        $element=Element $id
        Assert ([bool]$element) "Missing $id at $Width DIP"
        $rectangle=$element.Current.BoundingRectangle
        $window=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
        Assert ($rectangle.Right -le $window.Right -and $rectangle.Left -ge $window.Left) "$id is clipped at $Width DIP"
    }
}
try{
    Activity;Period '30' 30
    Scroll-To 'UsageMetric';Choice 'UsageMetric' 'UsageMetricCost'
    CostText $expectedTotal
    Wait-Text 'UsageCostCoverage' '4/8'
    Wait-Text 'UsageCostCoverage' '4'
    Wait-Text 'UsageCostCoverage' '3'
    Pass 'Turn scope estimates only the priced subset and retains the Claude/cache uncertainty ranges'

    $method=Element 'UsageCostMethod'
    $expand=$method.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    Assert ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) 'Pricing method expander should start collapsed'
    Scroll-To 'UsageCostMethod';$expand.Expand()
    Scroll-To 'UsageCostMethodDate'
    Wait-Visible 'UsageCostMethodDate' $true
    Wait-Text 'UsageCostMethodDate' '2026'
    Wait-Text 'UsageCostMethodDetails' $(if($Language -eq 'ja'){'標準API料金'}else{'standard API rates'})
    Assert ([bool](Element 'UsageOpenAIPricing')) 'OpenAI official pricing link is missing'
    Assert ([bool](Element 'UsageClaudePricing')) 'Claude official pricing link is missing'
    $expand.Collapse()
    Pass 'Dated calculation method and official pricing links are available in a collapsed read-only expander'

    Scroll-To 'UsageDayExpander';UI @('invoke','UsageDayExpander')|Out-Null
    Wait-Visible 'UsageDay' $true
    Scroll-To 'UsageDayCostDetails'
    Wait-Text 'UsageDayCostDetails' $expectedTotal
    Wait-Text 'UsageDayCostDetails' '4/8'
    Capture 'cost-day-details.png'
    Pass 'Selected-day details show the estimated range and partial-coverage counts'

    Scroll-To 'UsageModelChart'
    $chart=Element 'UsageModelChart'
    $bars=$chart.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ProgressBar))
    Assert ($bars.Count -eq 4) 'Known and unknown models are not all represented in the cost chart'
    Assert (@($bars|Where-Object {$_.Current.Name.Contains('gpt-5.3-codex') -and $_.Current.Name.Contains('$')}).Count -eq 1) 'Known exact/range cost is missing from model chart labels'
    Assert (@($bars|Where-Object {$_.Current.Name.Contains('claude-sonnet-4-6') -and $_.Current.Name.Contains('$18.075') -and $_.Current.Name.Contains('$18.3')}).Count -eq 1) 'Claude range is missing from model chart labels'
    $tinyCost=@($bars|Where-Object {$_.Current.Name.Contains('gpt-6-luna') -and $_.Current.Name.Contains('<$0.000001')})
    Assert ($tinyCost.Count -eq 1 -and -not $tinyCost[0].Current.Name.Contains('≈')) 'Positive costs below display precision were shown as zero or an ambiguous range'
    Assert (@($bars|Where-Object {$_.Current.Name.Contains('unknown-fixture-model') -and $_.Current.Name.Contains($(if($Language -eq 'ja'){'未算出'}else{'Not estimated'}))}).Count -eq 1) 'Unknown model was not marked unpriced'
    Pass 'Model chart labels retain known amounts, range bounds, and an unknown-model marker'

    Choice 'UsageScope' 'UsageScopeRequest'
    CostText '$0.63'
    Wait-Text 'UsageCostCoverage' '1/8'
    Wait-Text 'UsageCostCoverage' '7'
    Capture 'cost-last-request.png'
    Pass 'Last-request scope does not add excluded turn or cumulative records'

    Choice 'UsageScope' 'UsageScopeTurn'
    foreach($theme in @('light','dark')){
        $script:activityMode=$false;Go 'NavAppearance'
        $themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
        Scroll-To $themeId;UI @('invoke',$themeId)|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Activity;Scroll-To 'UsageMetric';Choice 'UsageMetric' 'UsageMetricCost'
            CostText $expectedTotal;Check-CostChartBounds $width
            Scroll-To 'UsageCostHeadline';Capture "cost-headline-$theme-$width.png"
            Scroll-To 'UsageModelChart';Capture "cost-models-$theme-$width.png"
            if($width -eq 500){
                Scroll-To 'UsageCostMethod';$method=Element 'UsageCostMethod'
                $method.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                Scroll-To 'UsageCostMethodDate';Capture "cost-method-$theme-$width.png"
                $method.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
            }
        }
    }
    Pass 'Cost metric and detail controls fit Light/Dark at 500 and 1160 DIP'

    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'Cost exploration modified execution history'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Cost exploration modified bridge settings'
    Pass 'Cost exploration preserves saved history and settings'
}catch{$checks.Add(@{Name='Usage cost UI';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize}
