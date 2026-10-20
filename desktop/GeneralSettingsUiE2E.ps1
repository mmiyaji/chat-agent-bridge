param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an isolated portable test payload under desktop/artifacts.'}
$data=Join-Path $artifacts ('general-settings-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $data|Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class GeneralSettingsNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$app=$null;$window=[IntPtr]::Zero;$checks=[Collections.Generic.List[string]]::new()
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Pass([string]$Message){$checks.Add($Message);Write-Output "PASS $Message"}
function Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 12|Set-Content (Join-Path $data $Name) -Encoding utf8}
function UI([string[]]$Arguments){$out=winapp ui @Arguments -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' ')"};$out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($window)}
function Element([string]$Id){(Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Visible([string]$Id){$e=Element $Id;return $e -and -not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Height -gt 0}
function Go([string]$Id){if(-not (Visible $Id)){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke',$Id)|Out-Null;Start-Sleep -Milliseconds 180}
function Scroll([string]$Id){
    $until=[DateTime]::UtcNow.AddSeconds(5)
    do{$scroll=Element 'MainContentScroll';if($scroll){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until)
    Assert ($null -ne $scroll) "Settings scroll area is not ready for $Id"
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if($pattern.Current.VerticallyScrollable){$pattern.SetScrollPercent(-1,0)}
    for($i=0;$i -lt 35;$i++){
        $e=Element $Id;$viewport=$scroll.Current.BoundingRectangle
        if($e){$r=$e.Current.BoundingRectangle;if((Visible $Id) -and $r.Top -ge $viewport.Top -and $r.Bottom -le [Math]::Min($viewport.Bottom,(Root).Current.BoundingRectangle.Bottom-8)){return}}
        if(-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100){break}
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 90
    }
    throw "Cannot reach $Id"
}
function Wait-Saved([string]$Name,[string]$Property,[string]$Value){
    $end=[DateTime]::UtcNow.AddSeconds(8)
    do{if(Test-Path (Join-Path $data $Name)){$saved=Get-Content (Join-Path $data $Name) -Raw|ConvertFrom-Json;if($saved.$Property -eq $Value){return $saved}};Start-Sleep -Milliseconds 80}while([DateTime]::UtcNow -lt $end)
    throw "$Name : $Property did not become $Value"
}
function Start-App{
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
    $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
    $script:app=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavOverview','-t','15000')|Out-Null
    $app.Refresh();$script:window=$app.MainWindowHandle
    Go 'NavWorkbench';Go 'NavOverview'
}
function Resize([int]$Width){$scale=[GeneralSettingsNative]::GetDpiForWindow($window)/96.0;[void][GeneralSettingsNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int](850*$scale),0x0014);Start-Sleep -Milliseconds 250}
function Check-AppearanceMatrix([string]$prefix){
    foreach($theme in @('light','dark')){
        foreach($width in @(1160,720,500)){
            Resize $width;Go 'NavWorkbench';Go 'NavOverview'
            Scroll 'DisplayLanguage';Scroll 'ThemeLight'
            UI @('invoke',$(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}))|Out-Null
            Wait-Saved 'appearance.json' 'Theme' $theme|Out-Null
            foreach($id in @('DisplayLanguage','ThemeDark')){
                Scroll $id;$r=(Element $id).Current.BoundingRectangle;$w=(Root).Current.BoundingRectangle
                Assert ($r.Left -ge $w.Left -and $r.Right -le $w.Right) "$id is clipped at $width"
                if($id -eq 'ThemeDark'){Assert ((Element 'ThemeSettingRow').Current.BoundingRectangle.Height -lt 260) "Theme row is inflated or its description is crushed at $width"}
            }
            UI @('screenshot','-o',(Join-Path $data "$prefix$theme-$width.png"))|Out-Null
        }
    }
}
try{
    Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$data;AgentExecutable='unused';AgentProvider='custom';AgentMode='exec';AgentArguments='';TrustedWorkspaceRoots=$data;AutoConnectProvider='none'}
    Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    $policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Missing current consent policy.'
    Json 'consent.json' @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    Json 'portable-update.json' @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$true}
    Json 'language.json' @{Language='ja'}
    Json 'appearance.json' @{Theme='dark'}
    Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
    Start-App;Resize 1160
    Assert ((Element 'NavOverview').Current.Name -eq '全般') 'Japanese General navigation label is missing.'
    Assert ($null -eq (Element 'NavAppearance')) 'Appearance remains a separate navigation item.'
    Assert ((Element 'OverviewDetailsExpander').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) 'Details must start expanded.'
    $details=(Element 'OverviewDetailsExpander').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $details.Collapse();Go 'NavWorkbench';Go 'NavOverview'
    Assert ($details.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) 'Navigation reopened deliberately collapsed details.'
    $details.Expand()
    Pass 'General replaces Overview and Appearance; setup details start open'

    Go 'NavAgentSettings';Scroll 'WorkspaceRoot'
    $draft=Join-Path $data 'unsaved-workspace'
    UI @('set-value','WorkspaceRoot',$draft)|Out-Null
    Go 'NavOverview';UI @('invoke','OverviewAutoConnectTile')|Out-Null;Start-Sleep -Milliseconds 200
    Assert ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'AutoConnectProvider') 'Status tile did not focus the General auto-connect setting.'
    UI @('invoke','AutoConnectProvider')|Out-Null;UI @('invoke','AutoConnectOptionSlack')|Out-Null
    Scroll 'SaveGeneralSettings';UI @('invoke','SaveGeneralSettings')|Out-Null
    $saved=Wait-Saved 'settings.json' 'AutoConnectProvider' 'slack'
    Assert ($saved.WorkspaceRoot -eq $data) 'Saving General committed the unrelated workspace draft.'
    Go 'NavAgentSettings'
    Assert ((UI @('get-value','WorkspaceRoot')|ConvertFrom-Json).text -eq $draft) 'Saving General discarded the workspace draft.'
    Pass 'General saves only auto-connect and preserves other unsaved drafts'
    Go 'NavOverview';Scroll 'AutoConnectProvider'
    UI @('invoke','AutoConnectProvider')|Out-Null;UI @('invoke','AutoConnectOptionNone')|Out-Null
    Scroll 'SaveGeneralSettings';UI @('invoke','SaveGeneralSettings')|Out-Null
    Wait-Saved 'settings.json' 'AutoConnectProvider' 'none'|Out-Null

    Go 'NavConnections'
    foreach($id in @('StartWithWindows','AutoConnectProvider','CloseToTrayToggle','DisplayLanguage','ThemeLight')){Assert (-not (Visible $id)) "General preference remains visible in Connections: $id"}
    Go 'NavOverview';UI @('invoke','OverviewStartupTile')|Out-Null;Start-Sleep -Milliseconds 150
    Scroll 'StartWithWindows';Assert (Visible 'StartWithWindows') 'Startup setting is unreachable in General.'
    Scroll 'CloseToTrayToggle';Assert (Visible 'CloseToTrayToggle') 'Tray setting is unreachable in General.'
    Pass 'Startup and tray settings are reachable in General and removed from Connections'

    Check-AppearanceMatrix ''
    Pass 'Language and theme controls fit at 1160, 720 and 500 DIP in Light and Dark'
    Resize 1160;Go 'NavOverview';Scroll 'DisplayLanguage'
    UI @('invoke','DisplayLanguage')|Out-Null
    $tree=UI @('inspect','DisplayLanguage','-d','6')|ConvertFrom-Json
    function Find-English($nodes){foreach($node in $nodes){if($node.type -eq 'ListItem' -and $node.name -eq 'English'){$node.selector};if($node.children){Find-English $node.children}}}
    $choice=@($tree.windows|ForEach-Object {Find-English $_.elements})|Select-Object -First 1
    Assert ([bool]$choice) 'English language option is missing.'
    UI @('invoke',$choice)|Out-Null
    UI @('wait-for','LanguageRestartDialog','-t','10000')|Out-Null
    Assert (-not (Element 'PrimaryButton').Current.IsEnabled) 'Restart is allowed while another tab has unsaved drafts.'
    UI @('invoke','CloseButton')|Out-Null
    Wait-Saved 'language.json' 'Language' 'en'|Out-Null
    Pass 'Language preference saves independently and offers restart while protecting unsaved drafts'
    Stop-Process -Id $app.Id -Force;$app.WaitForExit()
    Start-App;Resize 1160
    Assert ((Element 'NavOverview').Current.Name -eq 'General') 'General did not localize after restart.'
    Assert ((Element 'GeneralPageTitle').Current.Name -eq 'General') 'General page title did not localize.'
    Go 'NavAgentSettings'
    Assert ((UI @('get-value','WorkspaceRoot')|ConvertFrom-Json).text -eq $data) 'Unrelated draft was persisted across restart.'
    Go 'NavOverview';Scroll 'AutoConnectProvider'
    $selection=(Element 'AutoConnectProvider').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    Assert ($selection.Count -eq 1 -and $selection[0].Current.AutomationId -eq 'AutoConnectOptionNone') 'Saved auto-connect preference was not restored.'
    Scroll 'ThemeDark'
    Assert ((Element 'ThemeDark').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'Theme did not survive restart.'
    Pass 'Restart restores English, theme and saved General preferences without committing unrelated drafts'
    Check-AppearanceMatrix 'en-'
    Pass 'English appearance rows also fit at 1160, 720 and 500 DIP in Light and Dark'
    Stop-Process -Id $app.Id -Force;$app.WaitForExit()
    '{invalid-test-settings'|Set-Content (Join-Path $data 'settings.json')
    $invalidHash=(Get-FileHash (Join-Path $data 'settings.json')).Hash
    Start-App;Resize 1160;Scroll 'SaveGeneralSettings'
    Assert (-not (Element 'SaveGeneralSettings').Current.IsEnabled) 'General save can overwrite invalid bridge settings.'
    Assert (-not (Element 'AutoConnectProvider').Current.IsEnabled) 'Auto-connect editor is enabled without loaded settings.'
    Scroll 'DisplayLanguage';Assert ((Element 'DisplayLanguage').Current.IsEnabled) 'Language recovery is disabled by a bridge settings failure.'
    Scroll 'ThemeLight';UI @('invoke','ThemeLight')|Out-Null
    Wait-Saved 'appearance.json' 'Theme' 'light'|Out-Null
    Assert ((Get-FileHash (Join-Path $data 'settings.json')).Hash -eq $invalidHash) 'Theme recovery overwrote invalid bridge settings.'
    Pass 'General keeps language and theme usable after bridge settings fail to load'
}catch{
    Write-Output $_.ScriptStackTrace
    throw
}finally{
    if($app -and -not $app.HasExited){UI @('screenshot','-o',(Join-Path $data 'last-screen.png'))|Out-Null;Stop-Process -Id $app.Id -Force}
    Json 'results.json' $checks.ToArray()
    Write-Output "General settings artifacts: $data"
}
