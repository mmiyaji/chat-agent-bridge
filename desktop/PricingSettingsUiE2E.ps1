param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$DataDir,
    [ValidateSet('ja','en')][string]$Language='ja',
    [switch]$RunOfflineRefresh,
    [switch]$LayoutOnly
)
$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDir)
if(-not (Test-Path (Join-Path $dataPath '.cab-pricing-settings-test')) -or -not (Test-Path (Join-Path $dataPath '.cab-activity-cost-test'))){throw 'Use a marked isolated pricing-settings cost fixture.'}
$appRoot=[IO.Path]::GetFullPath($AppDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $appRoot.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Portable app payload must be under desktop/artifacts.'}
$si=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
$si.UseShellExecute=$false;$si.WorkingDirectory=[IO.Path]::GetTempPath();$si.ArgumentList.Add('--data-dir');$si.ArgumentList.Add($dataPath)
$appProcess=[Diagnostics.Process]::Start($si)
try {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(15)
    do { $window=winapp ui list-windows -a $appProcess.Id --json|ConvertFrom-Json|Select-Object -First 1; if($window){break};Start-Sleep -Milliseconds 100 } while([DateTimeOffset]::UtcNow -lt $deadline)
    if(-not $window){throw 'App window did not appear.'}
    . "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $appProcess.Id -DataDir $DataDir -Language $Language -OnlyHelpers
    $output=Join-Path $dataPath ('pricing-settings-review-'+$Language)
    New-Item $output -ItemType Directory -Force|Out-Null
    function Wait-File([string]$File,[scriptblock]$Predicate,[int]$Seconds=6){
        $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
        do { $obj=Get-Content -LiteralPath $File -Raw|ConvertFrom-Json; if(& $Predicate $obj){return $obj};Start-Sleep -Milliseconds 100 } while([DateTimeOffset]::UtcNow -lt $until)
        throw "Timed out waiting for $File"
    }
    function Wait-Value([string]$Id,[string]$Expected,[bool]$Different=$false,[int]$Seconds=8){
        $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
        do{$actual=Value $Id;if(($Different -and $actual -ne $Expected) -or (-not $Different -and $actual -eq $Expected)){return $actual};Start-Sleep -Milliseconds 150}while([DateTimeOffset]::UtcNow -lt $until)
        throw "Value did not settle for $Id"
    }
    function Wait-Enabled([string]$Id,[bool]$Expected){
        $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
        do{$el=Element $Id;if($el -and $el.Current.IsEnabled -eq $Expected){return};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until)
        $validation=if(Element 'PricingValidation'){(Element 'PricingValidation').Current.Name}else{''}
        $source=if(Element 'PricingEffectiveSource'){(Element 'PricingEffectiveSource').Current.Name}else{''}
        throw "Unexpected enabled state for $Id (validation='$validation'; source='$source')"
    }
    function Scroll-Pricing([string]$Id){UI @('scroll-into-view',$Id)|Out-Null;Start-Sleep -Milliseconds 150}
    function Select-UsageCost{
        Scroll-Pricing 'UsageMetric';$combo=Element 'UsageMetric'
        $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $option=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'UsageMetricCost'))
        if(-not $option){throw 'Cost metric option is unavailable.'}
        $option.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded){$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()}
        $until=[DateTimeOffset]::UtcNow.AddSeconds(3)
        while($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed -and [DateTimeOffset]::UtcNow -lt $until){Start-Sleep -Milliseconds 50}
        Start-Sleep -Milliseconds 250
    }
    function Select-Model([string]$Model){
        Scroll-Pricing 'PricingKnownModel'
        $combo=Element 'PricingKnownModel'
        if($Model -eq '__PROMPT__'){
            Add-Type -AssemblyName System.Windows.Forms
            $combo.SetFocus()
            $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            [Windows.Forms.SendKeys]::SendWait('{HOME}{ENTER}')
            Start-Sleep -Milliseconds 250
            return
        }
        $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $until=[DateTimeOffset]::UtcNow.AddSeconds(4);$item=$null
        do{
            $items=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty,'ComboBoxItem'))
            $item=@($items|Where-Object {$_.Current.Name -ceq $Model}|Select-Object -First 1)
            if($item.Count){break};Start-Sleep -Milliseconds 100
        }while([DateTimeOffset]::UtcNow -lt $until)
        if(-not $item.Count){throw "Model picker item not found: $Model"}
        $item[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded){$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()}
        $until=[DateTimeOffset]::UtcNow.AddSeconds(3)
        while($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed -and [DateTimeOffset]::UtcNow -lt $until){Start-Sleep -Milliseconds 50}
    }
    function Set-Rate([string]$Id,[string]$Value){
        Scroll-Pricing $Id
        $box=Element $Id
        if(-not $box){throw "Missing rate field $Id"}
        $range=$null
        if($box.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern,[ref]$range)){
            $range.SetValue([double]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture))
            Start-Sleep -Milliseconds 80
            return
        }
        $edit=$box.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
        if(-not $edit){throw "Missing editable field inside $Id"}
        $edit.SetFocus()
        $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 80
    }
    function Set-Rates([string[]]$Values){
        $ids=@('PricingInputMin','PricingInputMax','PricingCachedInputMin','PricingCachedInputMax','PricingCacheWriteMin','PricingCacheWriteMax','PricingOutputMin','PricingOutputMax')
        for($i=0;$i -lt $ids.Count;$i++){Set-Rate $ids[$i] $Values[$i]}
    }
    function Read-Manual(){Get-Content -LiteralPath (Join-Path $dataPath 'pricing-manual.json') -Raw|ConvertFrom-Json}
    function Check([string]$Name,[scriptblock]$Body){&$Body;$checks.Add(@{Name=$Name;Status='PASS'});Write-Output "PASS $Name"}
    $checks=[Collections.Generic.List[object]]::new()
    $manualPath=Join-Path $dataPath 'pricing-manual.json';$autoPath=Join-Path $dataPath 'pricing-auto.json'
    $manualInitialHash=(Get-FileHash $manualPath).Hash;$autoInitialHash=(Get-FileHash $autoPath).Hash
    if($LayoutOnly){
        Activity
        $manual=(Read-Manual).models|Select-Object -First 1
        $manualBeforeUpdateHash=(Get-FileHash $manualPath).Hash
        foreach($theme in @('light','dark')){
            $script:activityMode=$false
            Go 'NavOverview'
            $themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
            UI @('scroll-into-view',$themeId)|Out-Null;UI @('invoke',$themeId)|Out-Null
            Resize 500;Activity
            UI @('scroll-into-view','PricingSettings')|Out-Null
            $expander=Element 'PricingSettings'
            if($expander.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed){
                $expander.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            }
            if($manual){Select-Model $manual.model}
            if($RunOfflineRefresh -and $theme -eq 'dark'){
                Scroll-Pricing 'PricingUpdateNow';UI @('invoke','PricingUpdateNow')|Out-Null
                $limit=[DateTimeOffset]::UtcNow.AddSeconds(15);$errorElement=$null
                do{$errorElement=Element 'PricingManagerErrorDetails';if($errorElement -and $errorElement.Current.Name.Length -gt 0){break};Start-Sleep -Milliseconds 200}while([DateTimeOffset]::UtcNow -lt $limit)
                Assert ($errorElement -and $errorElement.Current.Name.Length -gt 0) 'Manual refresh did not show its remote-error fallback.'
                Assert ((Get-FileHash $manualPath).Hash -eq $manualBeforeUpdateHash) 'Failed automatic refresh modified manual pricing.'
                if($manual){Assert ((Value 'PricingEffectiveSource').Contains($(if($Language -eq 'ja'){'手動'}else{'Manual'}))) 'Failed refresh displaced the manual rate.'}
            }
            UI @('scroll-into-view','PricingSettings')|Out-Null;Capture "pricing-$Language-$theme-500-top.png"
            UI @('scroll-into-view','PricingInputMin')|Out-Null;Capture "pricing-$Language-$theme-500-rates.png"
            foreach($id in @('PricingSettings','PricingKnownModel','PricingInputMin','PricingOutputMax','PricingSaveManual','PricingRemoveManual')){
                UI @('scroll-into-view',$id)|Out-Null
                $rect=(Element $id).Current.BoundingRectangle;$windowRect=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($rect.Width -gt 0 -and $rect.Right -le $windowRect.Right -and $rect.Left -ge $windowRect.Left) "$id is clipped in $theme at 500 DIP"
            }
        }
        Check 'Pricing controls remain reachable at 500 DIP in Light and Dark themes' {}
        if($manual){
            Check 'Manual override remains active after app restart' {
                Assert ((Value 'PricingEffectiveSource').Contains($(if($Language -eq 'ja'){'手動'}else{'Manual'}))) 'Persisted manual override is not active after restart'
            }
        }
        Assert ((Get-FileHash (Join-Path $dataPath 'history.json')).Hash -eq $historyHash) 'Pricing layout test modified history'
        Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Pricing layout test modified bridge settings'
    } else {
    Activity
    Select-UsageCost
    $baselineCost=Value 'UsageCostHeadline'
    Scroll-Pricing 'PricingSettings';UI @('invoke','PricingSettings')|Out-Null
    UI @('wait-for','PricingKnownModel','-t','5000')|Out-Null
    Check 'Pricing settings are available in the usage dashboard' {
        Assert ((Value 'PricingAutomaticFilePath') -eq $autoPath) 'Automatic pricing path is not shown'
        Assert ((Value 'PricingManualFilePath') -eq $manualPath) 'Manual pricing path is not shown'
        Assert ((Element 'PricingManualRatesHeader') -or (Element 'PricingInputMin')) 'Manual rate editor did not load'
    }

    Select-Model 'gpt-5.3-codex'
    Set-Rates @('4','4','0.4','0.4','5','5','20','20')
    Scroll-Pricing 'PricingSaveManual';Wait-Enabled 'PricingSaveManual' $true;UI @('invoke','PricingSaveManual')|Out-Null
    $saved=Wait-File $manualPath {param($x) @($x.models|Where-Object model -eq 'gpt-5.3-codex').Count -eq 1}
    $changedCost=Wait-Value 'UsageCostHeadline' $baselineCost -Different $true
    Check 'Manual rate save writes a full model override and leaves automatic catalog intact' {
        Assert ((Get-FileHash $autoPath).Hash -eq $autoInitialHash) 'Saving manual rates modified pricing-auto.json'
        $p=@($saved.models|Where-Object model -eq 'gpt-5.3-codex')[0]
        Assert ($p.input.min -eq 4 -and $p.output.max -eq 20 -and [Math]::Abs([double]$p.cachedInput.min-0.4) -lt 0.000001) 'Saved override did not retain all entered rates'
        Assert ((Value 'PricingEffectiveSource').Contains($(if($Language -eq 'ja'){'手動'}else{'Manual'}))) 'Manual source was not selected after saving'
        Assert ($changedCost -ne $baselineCost) 'History cost estimate did not respond to manual pricing'
    }

    Select-Model '__PROMPT__'
    $modelEdit=Element 'PricingModelId'
    if(-not $modelEdit){throw 'Custom model ID field did not appear after selecting the model prompt.'}
    $modelEdit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('new-fixture-model')
    Set-Rates @('5','4','0','0','0','0','1','1')
    Wait-Enabled 'PricingSaveManual' $false
    Check 'Invalid minimum and maximum rates disable saving' {
        Assert ((Value 'PricingValidation').Length -gt 0) 'Invalid range has no validation feedback'
    }
    Set-Rate 'PricingInputMax' '5'
    Scroll-Pricing 'PricingSaveManual';Wait-Enabled 'PricingSaveManual' $true
    UI @('invoke','PricingSaveManual')|Out-Null
    $saved=Wait-File $manualPath {param($x) @($x.models|Where-Object model -eq 'new-fixture-model').Count -eq 1}
    Check 'An unlisted model can receive a manual override' {
        Assert (@($saved.models|Where-Object model -eq 'gpt-5.3-codex').Count -eq 1) 'Saving a new model removed the existing override'
        Assert (@($saved.models|Where-Object model -eq 'new-fixture-model').Count -eq 1) 'New model ID was not persisted exactly'
    }

    # User-facing confirmation protects deliberate removal; click only the dialog's primary action.
    Select-Model 'new-fixture-model'
    Scroll-Pricing 'PricingRemoveManual';UI @('invoke','PricingRemoveManual')|Out-Null
    $dialogUntil=[DateTimeOffset]::UtcNow.AddSeconds(5);$primary=$null
    do{$primary=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'PrimaryButton'));if($primary){break};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $dialogUntil)
    if(-not $primary){throw 'Manual override confirmation dialog did not appear.'}
    $primary.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $saved=Wait-File $manualPath {param($x) @($x.models|Where-Object model -eq 'new-fixture-model').Count -eq 0}
    Check 'Removing an override requires confirmation and removes only the selected model' {Assert (@($saved.models|Where-Object model -eq 'gpt-5.3-codex').Count -eq 1) 'Removing new model affected another override'}

    Select-Model 'gpt-5.3-codex';Scroll-Pricing 'PricingRemoveManual';UI @('invoke','PricingRemoveManual')|Out-Null
    $dialogUntil=[DateTimeOffset]::UtcNow.AddSeconds(5);$primary=$null
    do{$primary=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'PrimaryButton'));if($primary){break};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $dialogUntil)
    if(-not $primary){throw 'Manual override confirmation dialog did not appear.'}
    $primary.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $saved=Wait-File $manualPath {param($x) @($x.models|Where-Object model -eq 'gpt-5.3-codex').Count -eq 0}
    $restoredCost=Wait-Value 'UsageCostHeadline' $baselineCost
    Check 'Removing an override restores automatic pricing' {
        Assert ((Get-FileHash $autoPath).Hash -eq $autoInitialHash) 'Removing manual rates modified automatic prices'
        Assert ((Value 'PricingEffectiveSource').Contains($(if($Language -eq 'ja'){'自動'}else{'Automatic'}))) 'Automatic source was not restored'
        Assert ($restoredCost -eq $baselineCost) 'History cost estimate did not return to its automatic-rate total'
    }

    $toggle=Element 'PricingAutoUpdateToggle'
    $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-File $autoPath {param($x) $x.autoUpdateEnabled -eq $true}|Out-Null
    $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-File $autoPath {param($x) $x.autoUpdateEnabled -eq $false}|Out-Null
    Check 'Automatic-update preference persists without touching manual overrides' {Assert ((Read-Manual).models.Count -eq 0) 'Auto toggle rewrote manual file'}

    if($RunOfflineRefresh){
        Scroll-Pricing 'PricingUpdateNow';UI @('invoke','PricingUpdateNow')|Out-Null
        $limit=[DateTimeOffset]::UtcNow.AddSeconds(15)
        do{$errorElement=Element 'PricingManagerErrorDetails';if($errorElement -and $errorElement.Current.Name.Length -gt 0){break};Start-Sleep -Milliseconds 200}while([DateTimeOffset]::UtcNow -lt $limit)
        Check 'Manual refresh reports endpoint failure and retains local prices' {
            Assert ($errorElement -and $errorElement.Current.Name.Length -gt 0) 'No localized fallback status appeared after manual refresh'
            Assert ((Read-Manual).models.Count -eq 0) 'Manual refresh altered manual override file'
        }
    }

    # Capture representative narrow layouts in both themes; settings stay collapsed until explicitly opened.
    foreach($theme in @('light','dark')){
        $script:activityMode=$false
        Go 'NavOverview';$themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'};Scroll-Pricing $themeId;UI @('invoke',$themeId)|Out-Null
        Resize 500;Activity;Scroll-Pricing 'PricingSettings'
        $expander=(Element 'PricingSettings').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        if($expander.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed){$expander.Expand()}
        Scroll-Pricing 'PricingSettings';Capture "pricing-$Language-$theme-500-top.png"
        Scroll-Pricing 'PricingInputMin';Capture "pricing-$Language-$theme-500-rates.png"
        foreach($id in @('PricingSettings','PricingKnownModel','PricingInputMin','PricingOutputMax','PricingSaveManual','PricingRemoveManual')){
            Scroll-Pricing $id;$rect=(Element $id).Current.BoundingRectangle;$windowRect=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
            Assert ($rect.Width -gt 0 -and $rect.Right -le $windowRect.Right -and $rect.Left -ge $windowRect.Left) "$id is clipped in $theme at 500 DIP"
        }
    }
    Pass 'Pricing controls remain reachable at narrow width in Light and Dark themes'
    Assert ((Get-FileHash (Join-Path $dataPath 'history.json')).Hash -eq $historyHash) 'Pricing UI modified saved history'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Pricing UI modified bridge settings'
    Pass 'Pricing settings do not mutate history or bridge settings'
    }
} catch {
    $checks.Add(@{Name='Pricing settings UI';Status='FAIL';Detail="$($_.Exception.Message)"})
    throw
} finally {
    if($output){$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize}
    if($appProcess -and -not $appProcess.HasExited){try{$appProcess.CloseMainWindow()|Out-Null;if(-not $appProcess.WaitForExit(3000)){Stop-Process -Id $appProcess.Id -Force}}catch{}}
}
