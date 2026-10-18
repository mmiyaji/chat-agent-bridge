param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference='Stop'
# This test changes only an isolated CAB.Review package's startup task, then restores it.
$proc=Get-Process -Id $AppProcessId
$testLayout=Split-Path (Split-Path $proc.Path)
[xml]$manifest=Get-Content (Join-Path $testLayout 'AppxManifest.xml') -Raw
if(-not ([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.',[StringComparison]::Ordinal)){throw 'Refusing to toggle the normal app startup task.'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language ja -OnlyHelpers
$output=Join-Path $dataPath 'startup-burst-review';New-Item -ItemType Directory $output -Force|Out-Null
$initial=$null
function Wait-Idle{
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
    do{
        $el=Element 'StartWithWindows'
        if($el.Current.IsEnabled){return}
        Start-Sleep -Milliseconds 20
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Startup toggle stayed disabled'
}
function Startup{
    Go 'NavOverview';Scroll-To 'OverviewStartupTile';UI @('invoke','OverviewStartupTile')|Out-Null
    Wait-Visible 'StartWithWindows' $true
}
function Toggle-State{return (Element 'StartWithWindows').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState}
function Burst([bool]$RefreshOverview){
    Wait-Idle
    $toggle=Element 'StartWithWindows'
    $pattern=$toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $before=$pattern.Current.ToggleState
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $pattern.Toggle()
    Assert (-not $toggle.Current.IsEnabled) 'First toggle did not block further input'
    $accepted=1;$blocked=0
    for($i=0;$i -lt 24;$i++){
        try{$pattern.Toggle();$accepted++}
        catch{
            if($_.Exception.ToString() -notmatch 'not enabled|ElementNotEnabled|有効|無効'){throw}
            $blocked++
        }
    }
    Assert ($accepted -eq 1 -and $blocked -eq 24) "Burst was accepted $accepted times and blocked $blocked times"
    if($RefreshOverview){
        # A read requested while changing startup must not re-enable the switch.
        $nav=Element 'NavOverview'
        $nav.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if($clock.ElapsedMilliseconds -lt 550){Assert (-not $toggle.Current.IsEnabled) 'Overview refresh unlocked a pending change'}
        Startup
    }
    Wait-Idle
    Assert ($clock.ElapsedMilliseconds -ge 550) 'Settling interval was bypassed'
    $after=Toggle-State
    Assert ($after -ne $before) 'The accepted startup request was not applied'
    Capture $(if($after -eq [Windows.Automation.ToggleState]::On){'enabled.png'}else{'disabled.png'})
    Pass "One startup change applied; $blocked rapid presses blocked (overview refresh: $RefreshOverview)"
}
try{
    Resize 1160;Startup;Wait-Idle
    $initial=Toggle-State
    Burst $true
    Burst $false
    Go 'NavOverview';Scroll-To 'OverviewStartupTile'
    $label=Value 'OverviewStartupStatus'
    Assert ($label -match '有効|オフ') 'Overview has not settled to a known startup state'
    Capture 'overview.png'
    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'Startup test changed history'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Startup test changed bridge settings'
    Pass 'Overview and toggle settle to actual OS state; bridge settings and history remain unchanged'
}catch{$checks.Add(@{Name='Startup burst';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{
    try{
        if($null -ne $initial){Startup;Wait-Idle;if((Toggle-State) -ne $initial){(Element 'StartWithWindows').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();Wait-Idle}}
    }finally{$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize}
}
