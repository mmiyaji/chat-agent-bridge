param(
 [Parameter(Mandatory)][string]$AppDirectory,
 [Parameter(Mandatory)][string]$TestArchive,
 [Parameter(Mandatory)][string]$UpdateArchive
)
$ErrorActionPreference='Stop'
$payload=(Resolve-Path -LiteralPath $AppDirectory).Path
if(Test-Path (Join-Path $payload 'Data')){throw 'Use a clean test publish directory.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class AutomaticUpdateNative {
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$app=$null;$demo=$null;$checks=[Collections.Generic.List[object]]::new()
function Assert([bool]$value,[string]$message){if(-not $value){throw $message}}
function UI([string[]]$commands){$out=winapp ui @commands -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $commands $out"};$out}
function Element([string]$id){$app.Refresh();[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Name([string]$id){$e=Element $id;if($e){$e.Current.Name}else{''}}
function Visible([string]$id){$e=Element $id;$e -and -not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Height -gt 0}
function Enabled([string]$id){$e=Element $id;$e -and $e.Current.IsEnabled}
function Wait([scriptblock]$condition,[string]$message,[int]$seconds=30){$end=[DateTime]::UtcNow.AddSeconds($seconds);do{if(& $condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);throw $message}
function Capture([string]$name){UI @('screenshot','-o',(Join-Path $demo.Root ($name+'.png')))|Out-Null}
function Preferences{Get-Content (Join-Path $demo.Data 'portable-update.json') -Raw|ConvertFrom-Json}
function Save-Preferences($value){$value|ConvertTo-Json -Depth 10|Set-Content (Join-Path $demo.Data 'portable-update.json') -Encoding utf8}
function Launch {
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $demo.Install 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
 $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($demo.Data);$start.Environment['CAB_PORTABLE_UPDATE_TEST_DIRECTORY']=$demo.Fixture
 $script:app=[Diagnostics.Process]::Start($start);UI @('wait-for','NavAbout','-t','15000')|Out-Null
}
function Stop-TestApp{if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force;$app.WaitForExit()};$script:app=$null}
function Go-About{if(-not (Visible 'NavAbout')){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke','NavAbout')|Out-Null;UI @('scroll-into-view','PortableCheckUpdates')|Out-Null}
function Resize([int]$width){$app.Refresh();$scale=[AutomaticUpdateNative]::GetDpiForWindow($app.MainWindowHandle)/96.0;[void][AutomaticUpdateNative]::SetWindowPos($app.MainWindowHandle,[IntPtr]::Zero,40,40,[int]($width*$scale),[int](740*$scale),0x14);Start-Sleep -Milliseconds 180}
function Assert-Controls {
 UI @('scroll-into-view','PortableCheckUpdates')|Out-Null
 $app.Refresh();$bounds=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).Current.BoundingRectangle
 $previous=$null
 foreach($id in @('PortableCheckUpdates','PortableUpdateReleaseNotes')){
  Assert (Visible $id) "Missing update action: $id";$rect=(Element $id).Current.BoundingRectangle
  Assert ($rect.Left -ge $bounds.Left -and $rect.Right -le $bounds.Right -and $rect.Top -ge $bounds.Top -and $rect.Bottom -le $bounds.Bottom) "Clipped update action: $id"
  if($previous){Assert (-not $rect.IntersectsWith($previous)) 'Update check and release link overlap'};$previous=$rect
 }
 Assert ((Element 'PortableUpdateReleaseNotes').Current.HelpText -eq 'https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6') 'Release link does not point to the offered version'
}
try{
 foreach($locale in @('ja','en')){foreach($theme in @('light','dark')){
  $demo=& (Join-Path $PSScriptRoot 'New-PortableUpdateTryout.ps1') -TestArchive $TestArchive -UpdateArchive $UpdateArchive -NoLaunch
  Get-ChildItem -LiteralPath $payload -Force|Where-Object {$_.Name -ne '.cab-instance.lock'}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $demo.Install -Recurse -Force}
  @{Language=$locale}|ConvertTo-Json|Set-Content (Join-Path $demo.Data 'language.json')
  @{Theme=$theme}|ConvertTo-Json|Set-Content (Join-Path $demo.Data 'appearance.json')
  $feed=Get-Content (Join-Path $demo.Fixture 'releases.json') -Raw
  Launch
  Wait {(Name 'NavAbout').Contains($demo.UpdateVersion)} 'Automatic check did not mark Information' 35
  Wait {Visible 'NavAboutUpdateBadge'} 'Expanded sidebar has no visible update icon' 5
  Assert ((Preferences).availableRelease.version -eq $demo.UpdateVersion) 'Automatic candidate was not cached'
  $cachedCandidate=(Preferences).availableRelease
  Assert ($null -ne (Preferences).lastAttemptUtc) 'Automatic check time was not saved'
  Capture "$locale-$theme-sidebar"
  Go-About
  Assert (-not (Visible 'PortableApplyUpdate')) 'Automatic check downloaded or applied the release'
  foreach($width in @(1160,500)){
   Resize $width;Assert-Controls;Capture "$locale-$theme-$width-offer"
   if($width -eq 500){if(-not (Visible 'NavAbout')){UI @('invoke','TogglePaneButton')|Out-Null};Wait {(Visible 'NavAbout') -and (Visible 'NavAboutUpdateBadge')} 'Narrow sidebar hides the update icon' 5;Capture "$locale-$theme-500-sidebar";UI @('invoke','NavAbout')|Out-Null}
  }
  Resize 1160
  UI @('scroll-into-view','PortableUpdateAutomaticChecks')|Out-Null
  UI @('invoke','PortableUpdateAutomaticChecks')|Out-Null
  Wait { -not (Preferences).automaticChecksEnabled -and (Enabled 'PortableUpdateAutomaticChecks')} 'Automatic check setting was not saved'
  Assert ((Name 'NavAbout').Contains($demo.UpdateVersion)) 'Disabling automatic checks erased the existing update badge'
  Stop-TestApp
  $prefs=Preferences;$prefs.lastAttemptUtc=[DateTimeOffset]::UtcNow.AddHours(-7).ToString('o');Save-Preferences $prefs
  '[]'|Set-Content (Join-Path $demo.Fixture 'releases.json')
  Launch
  Wait {(Name 'NavAbout').Contains($demo.UpdateVersion)} 'Restart lost the cached update badge' 5
  Go-About
  Assert ((Element 'PortableUpdateAutomaticChecks').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Restart lost disabled automatic checks'
  if($locale -eq 'ja' -and $theme -eq 'light'){
   $attempt=(Preferences).lastAttemptUtc;Start-Sleep -Seconds 17
   Assert ((Preferences).lastAttemptUtc -eq $attempt -and (Name 'NavAbout').Contains($demo.UpdateVersion)) 'Disabled automatic check still contacted the feed'
  }
  UI @('scroll-into-view','PortableCheckUpdates')|Out-Null;UI @('invoke','PortableCheckUpdates')|Out-Null
  Wait {(Enabled 'PortableCheckUpdates') -and -not (Name 'NavAbout').Contains($demo.UpdateVersion)} 'Manual check while automatic checks are disabled did not clear the obsolete badge'
  Assert ((Element 'PortableUpdateReleaseNotes').Current.HelpText -eq 'https://github.com/mmiyaji/chat-agent-bridge/releases') 'No-update link does not point to the release listing'
  Capture "$locale-$theme-off-manual"
  UI @('scroll-into-view','PortableUpdateAutomaticChecks')|Out-Null
  UI @('invoke','PortableUpdateAutomaticChecks')|Out-Null
  Wait {(Preferences).automaticChecksEnabled -and (Enabled 'PortableUpdateAutomaticChecks')} 'Automatic checks could not be enabled again'
  if($locale -eq 'ja' -and $theme -eq 'light'){
   $path=Join-Path $demo.Data 'portable-update.json';$attributes=[IO.File]::GetAttributes($path)
   try{
    [IO.File]::SetAttributes($path,$attributes -bor [IO.FileAttributes]::ReadOnly)
    UI @('invoke','PortableUpdateAutomaticChecks')|Out-Null
    Wait {(Visible 'PortableUpdatePreferenceError') -and (Enabled 'PortableUpdateAutomaticChecks')} 'Save failure was not reported'
    Assert ((Preferences).automaticChecksEnabled) 'Failed save changed the persisted preference'
    Assert ((Element 'PortableUpdateAutomaticChecks').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On) 'Failed save did not restore the checkbox'
    Capture 'save-failure'
   }finally{[IO.File]::SetAttributes($path,$attributes)}
   Stop-TestApp
   $prefs=Preferences;$prefs.lastAttemptUtc=[DateTimeOffset]::UtcNow.AddHours(-7).ToString('o');$prefs.availableRelease=$cachedCandidate;Save-Preferences $prefs
   $feed|Set-Content (Join-Path $demo.Fixture 'releases.json')
   Launch;Go-About
   $status=Name 'PortableUpdateStatus';$attempt=(Preferences).lastAttemptUtc
   '['|Set-Content (Join-Path $demo.Fixture 'releases.json')
   Wait {(Preferences).lastAttemptUtc -ne $attempt -and (Enabled 'PortableCheckUpdates')} 'Due automatic retry did not run' 15
   Assert ((Name 'NavAbout').Contains($demo.UpdateVersion) -and (Name 'PortableUpdateStatus') -eq $status) 'Automatic failure erased availability or replaced visible status'
   Capture 'automatic-failure-keeps-cache'
   $attempt=(Preferences).lastAttemptUtc;Stop-TestApp
   $feed|Set-Content (Join-Path $demo.Fixture 'releases.json');Launch;Go-About
   '[]'|Set-Content (Join-Path $demo.Fixture 'releases.json')
   Wait {(Preferences).lastAttemptUtc -ne $attempt -and -not (Name 'NavAbout').Contains($demo.UpdateVersion)} 'Restart did not check for updates with a recent cached timestamp' 15
   Capture 'restart-check-clears-cache'
   Write-Output 'PASS save rollback, silent failure cache, a fresh startup check and re-enabling automatic checks'
  }
  $checks.Add(@{locale=$locale;theme=$theme;passed=$true;root=$demo.Root})
  Write-Output "PASS $locale $theme : automatic discovery, sidebar badge, release link, narrow layout, disabled persistence, cache restart and manual check"
  Stop-TestApp
 }}
}finally{
 if($app -and -not $app.HasExited){Capture 'last-screen';Stop-TestApp}
 $report=Join-Path $PSScriptRoot 'artifacts/automatic-update-ui-results.json';$checks|ConvertTo-Json -Depth 6|Set-Content $report
 Write-Output "Automatic update UI report: $report"
}
