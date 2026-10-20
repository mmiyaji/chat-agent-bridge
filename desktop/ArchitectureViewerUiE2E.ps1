param([Parameter(Mandatory)][string]$AppDirectory,[ValidateSet('ja','en','both')][string]$Language='both',[ValidateSet('light','dark','both')][string]$Theme='both')
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an isolated portable payload under desktop/artifacts.'}
$run=Join-Path $artifacts ('architecture-viewer-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run|Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class ArchitectureViewerNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$app=$null;$window=[IntPtr]::Zero;$checks=[Collections.Generic.List[object]]::new()
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function UI([string[]]$Arguments){$out=winapp ui @Arguments -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' ')"};$out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($window)}
function Find($Parent,[string]$Id){$Parent.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Element([string]$Id){Find (Root) $Id}
function Visible($Element){$Element -and -not $Element.Current.IsOffscreen -and $Element.Current.BoundingRectangle.Height -gt 0}
function Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 12|Set-Content (Join-Path $data $Name) -Encoding utf8}
function Resize([int]$Width,[int]$Height=740){$scale=[ArchitectureViewerNative]::GetDpiForWindow($window)/96.0;[void][ArchitectureViewerNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0014);Start-Sleep -Milliseconds 220}
function Invoke-Element($Element){$Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Capture([string]$Name){UI @('screenshot','-o',(Join-Path $data ($Name+'.png')))|Out-Null}
function Check-State([string]$Id){(Element $Id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState}
function Select-Language([string]$Label){
    UI @('scroll-into-view','DisplayLanguage')|Out-Null
    UI @('invoke','DisplayLanguage')|Out-Null
    $tree=UI @('inspect','DisplayLanguage','-d','6')|ConvertFrom-Json
    function Choice($nodes){foreach($node in $nodes){if($node.type -eq 'ListItem' -and $node.name -eq $Label){$node.selector};if($node.children){Choice $node.children}}}
    $selector=@($tree.windows|ForEach-Object {Choice $_.elements})|Select-Object -First 1
    Assert ([bool]$selector) "Language choice not found: $Label"
    UI @('invoke',$selector)|Out-Null
    Start-Sleep -Milliseconds 250
    Assert (-not (Visible (Element 'LanguageRestartDialog'))) 'Initial diagram language change requires a restart'
}
function Reach-Preview([string]$Outer,[string]$ScrollId){
    $parent=Element $Outer;Assert ($null -ne $parent) "Missing architecture preview: $Outer"
    $button=Find $parent 'OpenArchitectureViewer';Assert ($null -ne $button) 'Preview is not a keyboard-accessible button'
    $scroll=Element $ScrollId;$pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if($pattern.Current.VerticallyScrollable){$pattern.SetScrollPercent(-1,0)}
    for($i=0;$i -lt 40;$i++){
        $r=$button.Current.BoundingRectangle;$v=$scroll.Current.BoundingRectangle
        if((Visible $button) -and $r.Top -ge $v.Top -and $r.Bottom -le [Math]::Min($v.Bottom,(Root).Current.BoundingRectangle.Bottom-8)){return $button}
        if(-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100){break}
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement);Start-Sleep -Milliseconds 70
    }
    throw 'Preview is not fully reachable in its scroll viewport'
}
function Zoom-Level {
    $end=[DateTime]::UtcNow.AddSeconds(4)
    do{$level=Find (Element 'ArchitectureViewerDialog') 'ArchitectureZoomLevel';if($level -and $level.Current.Name -match '(\d+)'){return [int]$Matches[1]};Start-Sleep -Milliseconds 50}while([DateTime]::UtcNow -lt $end)
    throw 'Zoom level is missing'
}
function Wait-Zoom([scriptblock]$Condition){$end=[DateTime]::UtcNow.AddSeconds(4);do{$value=Zoom-Level;if(& $Condition $value){return $value};Start-Sleep -Milliseconds 70}while([DateTime]::UtcNow -lt $end);throw 'Zoom level did not settle'}
function Assert-ViewerBounds {
    $dialog=Element 'ArchitectureViewerDialog';$bounds=(Root).Current.BoundingRectangle;$rect=$dialog.Current.BoundingRectangle
    Assert ($rect.Left -ge $bounds.Left-1 -and $rect.Right -le $bounds.Right+1 -and $rect.Top -ge $bounds.Top-1 -and $rect.Bottom -le $bounds.Bottom+1) 'Viewer extends outside the app window'
    if($bounds.Width/[ArchitectureViewerNative]::GetDpiForWindow($window)*96 -gt 1000){Assert ($rect.Width -gt $bounds.Width*0.8) 'Viewer is constrained by the default narrow dialog width'}
    $previous=$null
    foreach($id in @('ArchitectureZoomOut','ArchitectureZoomLevel','ArchitectureZoomIn','ArchitectureFit','ArchitectureActualSize')){
        $control=Find $dialog $id;Assert (Visible $control) "Viewer toolbar control is inaccessible: $id"
        $r=$control.Current.BoundingRectangle
        Assert ($r.Left -ge $bounds.Left -and $r.Right -le $bounds.Right -and $r.Top -ge $bounds.Top -and $r.Bottom -le $bounds.Bottom) "Viewer toolbar is clipped: $id"
        if($previous){Assert (-not $r.IntersectsWith($previous)) 'Viewer toolbar controls overlap'};$previous=$r
    }
    $close=Find $dialog 'CloseButton';Assert (Visible $close) 'Viewer has no native close action'
    Assert ($close.Current.IsEnabled) 'Viewer close action is disabled'
    $error=Find $dialog 'ArchitectureLoadError';Assert (-not (Visible $error)) 'SVG failed to load in the viewer'
}
function Exercise-Viewer($Button,[string]$Label){
    Invoke-Element $Button;UI @('wait-for','ArchitectureViewerDialog','-t','6000')|Out-Null;Start-Sleep -Milliseconds 250
    Assert-ViewerBounds
    $fit=Zoom-Level;Assert ($fit -gt 0 -and $fit -le 100) 'Initial viewer did not fit the complete diagram'
    Capture ($Label+'-fit')
    Invoke-Element (Find (Element 'ArchitectureViewerDialog') 'ArchitectureZoomIn')
    $null=Wait-Zoom {param($x)$x -gt $fit}
    Invoke-Element (Find (Element 'ArchitectureViewerDialog') 'ArchitectureActualSize')
    $null=Wait-Zoom {param($x)$x -eq 100}
    Capture ($Label+'-actual-size')
    $scroll=Find (Element 'ArchitectureViewerDialog') 'ArchitectureViewport'
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if($width -eq 500){
        Assert ($pattern.Current.HorizontallyScrollable) 'Actual-size diagram cannot be panned in a narrow window'
        $pattern.SetScrollPercent(50,-1);Assert ($pattern.Current.HorizontalScrollPercent -gt 0) 'Horizontal panning did not work'
    }
    Invoke-Element (Find (Element 'ArchitectureViewerDialog') 'ArchitectureZoomOut')
    $null=Wait-Zoom {param($x)$x -lt 100}
    Invoke-Element (Find (Element 'ArchitectureViewerDialog') 'ArchitectureFit')
    $null=Wait-Zoom {param($x)[Math]::Abs($x-$fit) -le 1}
    if($width -eq 1160){
        Resize 500 640;Assert-ViewerBounds
        $null=Wait-Zoom {param($x)$x -lt $fit}
        Capture ($Label+'-resized')
        Resize 1160;Assert-ViewerBounds
    }
    Invoke-Element (Find (Element 'ArchitectureViewerDialog') 'CloseButton')
    UI @('wait-for','ArchitectureViewerDialog','--gone','-t','4000')|Out-Null
    $focusDeadline=[DateTime]::UtcNow.AddSeconds(3)
    while([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'OpenArchitectureViewer' -and [DateTime]::UtcNow -lt $focusDeadline){Start-Sleep -Milliseconds 50}
    Assert ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'OpenArchitectureViewer') 'Closing the viewer did not restore focus to the preview'
    Assert (-not $app.HasExited) 'Closing the viewer terminated the app'
    $checks.Add(@{locale=$locale;theme=$color;width=$width;context=$Label;passed=$true})
}
try{
    $locales=if($Language -eq 'both'){@('ja','en')}else{@($Language)};$colors=if($Theme -eq 'both'){@('light','dark')}else{@($Theme)}
    foreach($locale in $locales){foreach($color in $colors){
        $data=Join-Path $run ($locale+'-'+$color);New-Item -ItemType Directory $data|Out-Null
        Json 'language.json' @{Language=$locale};Json 'appearance.json' @{Theme=$color}
        Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false;$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
        $app=[Diagnostics.Process]::Start($start)
        UI @('wait-for','WizardArchitecture','-t','15000')|Out-Null;$app.Refresh();$window=$app.MainWindowHandle
        foreach($width in @(1160,720,500)){
            Resize $width
            $button=Reach-Preview 'WizardArchitecture' 'WizardScroll';Capture "$width-wizard-preview"
            $terms=Check-State 'WizardAcceptTerms';$privacy=Check-State 'WizardAcceptPrivacy'
            Exercise-Viewer $button "$width-wizard"
            Assert ((Check-State 'WizardAcceptTerms') -eq $terms -and (Check-State 'WizardAcceptPrivacy') -eq $privacy) 'Viewing the diagram changed consent choices'
            Assert (-not (Test-Path (Join-Path $data 'consent.json'))) 'Viewing the diagram accepted the terms'
            Write-Output "PASS $locale $color $width : vector preview, zoom, fit, pan, resize and close"
        }
        if($locale -eq 'ja' -and $color -eq 'light'){
            Select-Language 'English'
            $button=Reach-Preview 'WizardArchitecture' 'WizardScroll'
            Assert ($button.Current.Name -match '^Open architecture diagram') 'Preview did not switch to English immediately'
            Exercise-Viewer $button '500-language-en'
            Select-Language '日本語 (Japanese)'
            $button=Reach-Preview 'WizardArchitecture' 'WizardScroll'
            Assert ($button.Current.Name -notmatch '^Open architecture diagram') 'Preview did not return to Japanese immediately'
            Assert ((Check-State 'WizardAcceptTerms') -eq $terms -and (Check-State 'WizardAcceptPrivacy') -eq $privacy) 'Changing diagram language changed consent'
            Write-Output 'PASS immediate Japanese / English / Japanese diagram language switching'
        }
        UI @('invoke','OnboardingDefer')|Out-Null
        UI @('wait-for','OnboardingTitle','--gone','-t','4000')|Out-Null
        if(-not (Visible (Element 'NavOverview'))){UI @('invoke','TogglePaneButton')|Out-Null}
        UI @('invoke','NavOverview')|Out-Null
        $button=Reach-Preview 'OverviewArchitecture' 'MainContentScroll'
        Exercise-Viewer $button '500-general'
        Assert (-not (Test-Path (Join-Path $data 'history.json'))) 'Diagram interaction started an AI execution'
        Write-Output "PASS $locale $color : shared General viewer and unchanged onboarding consent"
        Stop-Process -Id $app.Id -Force;$app.WaitForExit();$app=$null
    }}
}
catch{$checks.Add(@{passed=$false;detail=$_.Exception.Message});throw}
finally{
    if($app -and -not $app.HasExited){Capture 'last-screen';Stop-Process -Id $app.Id -Force}
    $checks.ToArray()|ConvertTo-Json -Depth 6|Set-Content (Join-Path $run 'results.json') -Encoding utf8
    Write-Output "Architecture viewer artifacts: $run"
}
