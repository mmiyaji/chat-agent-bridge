param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StartupSplashSmokeNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
'@
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$install=(Resolve-Path -LiteralPath $AppDirectory).Path
if (-not $install.StartsWith($artifacts+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated portable publish under desktop/artifacts.' }
$root=Join-Path $artifacts ('startup-splash-ui-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$checks=[Collections.Generic.List[object]]::new(); $app=$null
function Json([string]$Path,$Value) { ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Assert([bool]$Value,[string]$Message) { if(-not $Value){throw $Message} }
function Find([string]$Id) {
 if(-not $app -or $app.HasExited){return $null}
 $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))
 foreach($window in $windows) {
  $item=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
  if($item){return $item}
 }
 return $null
}
function Visible([string]$Id) { $e=Find $Id; return $e -and -not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Height -gt 0 }
function Wait([scriptblock]$Condition,[string]$Description,[int]$Seconds=20) {
 $until=[DateTime]::UtcNow.AddSeconds($Seconds)
 do { if($app -and $app.HasExited){throw "App exited while waiting for $Description"}; if(& $Condition){return}; Start-Sleep -Milliseconds 40 } while([DateTime]::UtcNow -lt $until)
 throw "Timed out waiting for $Description"
}
function Capture([string]$Name) {
 [StartupSplashSmokeNative]::SetForegroundWindow($app.MainWindowHandle)|Out-Null
 $rect=New-Object StartupSplashSmokeNative+RECT
 if(-not [StartupSplashSmokeNative]::GetWindowRect($app.MainWindowHandle,[ref]$rect)){throw 'Could not read app window bounds.'}
 $width=$rect.Right-$rect.Left; $height=$rect.Bottom-$rect.Top
 if($width -le 0 -or $height -le 0){throw 'App window has empty screen bounds.'}
 $bitmap=[Drawing.Bitmap]::new($width,$height)
 try { $graphics=[Drawing.Graphics]::FromImage($bitmap); try {$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)} finally {$graphics.Dispose()}; $bitmap.Save((Join-Path $root ($Name+'.png')),[Drawing.Imaging.ImageFormat]::Png) } finally {$bitmap.Dispose()}
}
function SplashBlueVisible {
 if(-not $app -or $app.HasExited){return $false}
 $app.Refresh(); $handle=$app.MainWindowHandle
 if($handle -eq [IntPtr]::Zero){return $false}
 [StartupSplashSmokeNative]::SetWindowPos($handle,[IntPtr]::new(-1),0,0,0,0,0x0013)|Out-Null
 [StartupSplashSmokeNative]::SetForegroundWindow($handle)|Out-Null
 $rect=New-Object StartupSplashSmokeNative+RECT
 if(-not [StartupSplashSmokeNative]::GetWindowRect($handle,[ref]$rect)){return $false}
 $width=$rect.Right-$rect.Left; $height=$rect.Bottom-$rect.Top
 if($width -lt 300 -or $height -lt 300){return $false}
 # Startup graphic is centered 40 px above the client midpoint. Copy a small ROI and
 # sample a coarse grid so this loop does not call UI Automation during first paint.
 $left=$rect.Left+[int]($width/2)-100; $top=$rect.Top+[int]($height/2)-140
 $bitmap=[Drawing.Bitmap]::new(200,200)
 try {
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  try {$graphics.CopyFromScreen($left,$top,0,0,$bitmap.Size)} finally {$graphics.Dispose()}
  for($y=0;$y -lt 200;$y+=12){for($x=0;$x -lt 200;$x+=12){
   $pixel=$bitmap.GetPixel($x,$y)
   if($pixel.B -gt 170 -and $pixel.R -lt 80 -and $pixel.G -gt 40 -and $pixel.G -lt 190){return $true}
  }}
  return $false
 } finally {$bitmap.Dispose()}
}
function Launch([string]$Data) {
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe')); $start.UseShellExecute=$false
 $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($Data)
 $script:app=[Diagnostics.Process]::Start($start)
}
function CloseApp {
 if($app -and -not $app.HasExited){
  try {
   if(Visible 'NavAbout') { Invoke-Id 'NavAbout'; Invoke-Id 'AboutExit' }
   else { $app.CloseMainWindow()|Out-Null }
   Wait { Visible 'WindowCloseConfirmation' } 'close confirmation' 5
   Invoke-Id 'PrimaryButton'
  } catch { if(-not $app.HasExited){$app.CloseMainWindow()|Out-Null} }
  if(-not $app.WaitForExit(8000)){Stop-Process -Id $app.Id -Force}
 }
 $script:app=$null
}
function Invoke-Id([string]$Id) {
 $item=Find $Id
 if(-not $item){throw "Missing UI automation element: $Id"}
 $pattern=$null
 if($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke();return}
 if($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)){$pattern.Select();return}
 throw "No invoke/selection pattern: $Id"
}
function StartScenario([string]$Name,[string]$Language,[string]$Theme,[bool]$Onboarding,[string]$ExistingData='') {
 $data=if($ExistingData){$ExistingData}else{Join-Path $root ($Name+'-data')}
 if(-not (Test-Path -LiteralPath $data)){
  New-Item -ItemType Directory -Path $data|Out-Null
  if(-not $Onboarding) { Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$data;TrustedWorkspaceRoots=$data;AgentProvider='custom';AgentExecutable='unused';AgentMode='exec';AutoConnectProvider='none'} }
  if(-not $Onboarding){Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}}
  Json (Join-Path $data 'language.json') @{Language=$Language}
  Json (Join-Path $data 'appearance.json') @{Theme=$Theme}
  Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
  $candidate=@{version='9.9.9';tag='v9.9.9';assetName='ChatAgentBridge.Portable-9.9.9-win-x64.zip';downloadUri='https://github.com/mmiyaji/chat-agent-bridge/releases/download/v9.9.9/ChatAgentBridge.Portable-9.9.9-win-x64.zip';sha256=('a'*64);size=1234;releasePage='https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v9.9.9'}
  Json (Join-Path $data 'portable-update.json') @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$false;availableRelease=$candidate}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
 }
 Launch $data
 $startupClock=[Diagnostics.Stopwatch]::StartNew()
 # Detect and screenshot the centered blue graphic with native screen sampling before
 # making UI Automation calls; UIA can stall the UI thread during large-page startup.
 $pixelSeen=$false; $screenshotSaved=$false; $splashMessage=''; $uiSeen=$false
 $firstFrameDeadline=[DateTime]::UtcNow.AddSeconds(15)
 while([DateTime]::UtcNow -lt $firstFrameDeadline -and -not $app.HasExited) {
  if(SplashBlueVisible) {
   $pixelSeen=$true; Capture "$Name-startup-splash"; $screenshotSaved=$true; break
  }
  Start-Sleep -Milliseconds 25
 }
 Assert $pixelSeen 'Centered startup icon was not observed.'
 $message=Find 'StartupSplashMessage'
 if($message -and -not $message.Current.IsOffscreen) { $uiSeen=$true; $splashMessage=$message.Current.Name }
 if($uiSeen) {
  $expected=if($Language -eq 'ja'){'設定と会話を読み込んでいます'}else{'Loading settings and conversations'}
  Assert ($splashMessage -eq $expected) "Unexpected localized splash message: '$splashMessage' (expected '$expected')."
 }
 $target=if($Onboarding){'OnboardingTitle'}else{'NavOverview'}
 Wait { Visible $target } "content ready ($target)"
 Wait { -not (Visible 'StartupSplashMessage') } 'splash hidden after content is ready'
 $readyElapsed=$startupClock.ElapsedMilliseconds
 Assert ($readyElapsed -ge 400) "Startup reached ready state before the 400 ms minimum ($readyElapsed ms from process launch)."

 if(-not $Onboarding){Assert (Visible 'NavOverview') 'Overview navigation did not become visible.'}
 Capture "$Name-ready"
 $checks.Add(@{name=$Name;passed=$true;onboarding=$Onboarding;splashMessage=$splashMessage;bluePixelsDetected=$pixelSeen;uiAutomationObserved=$uiSeen;screenshotSaved=$screenshotSaved;startupToReadyMs=$readyElapsed})
 Write-Output "PASS $Name startup evidence saved (bluePixels=$pixelSeen, UIA=$uiSeen, elapsed=${readyElapsed}ms) and ready content visible"
}
try {
 $regularData=''
 foreach($language in @('ja','en')){foreach($theme in @('light','dark')){
  $name="$language-$theme";StartScenario $name $language $theme $false; if($name -eq 'ja-dark'){$regularData=Join-Path $root ($name+'-data')}; CloseApp
 }}
 # A genuinely fresh data directory exercises first-run onboarding and initial-content readiness.
 StartScenario 'fresh-onboarding' 'en' 'light' $true;CloseApp
 # Relaunch the already initialized profile to catch stale overlay state across close/reopen.
 StartScenario 'relaunch' 'ja' 'dark' $false $regularData;CloseApp
 Json (Join-Path $root 'results.json') @{passed=$true;checks=@($checks);root=$root};Write-Output "Evidence: $root"
} catch {
 if($app -and -not $app.HasExited){try{Capture 'failure'}catch{}}
 Json (Join-Path $root 'results.json') @{passed=$false;error=$_.Exception.Message;checks=@($checks);root=$root};throw
} finally {CloseApp}
