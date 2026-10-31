param([Parameter(Mandatory)][string]$AppDirectory, [switch]$LayoutOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PreviewSmokeNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
}
'@
$install=(Resolve-Path -LiteralPath $AppDirectory).Path
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $install.StartsWith($artifacts+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated portable publish under desktop/artifacts.' }
$root=Join-Path $artifacts ('progress-preview-ui-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$app=$null
function Json([string]$Path,$Value) { ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Find([string]$Id) {
 $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))
 foreach($window in $windows) {
  $item=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
  if($item) { return $item }
 }
 return $null
}
function Visible([string]$Id) { $item=Find $Id; return $item -and -not $item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Height -gt 0 }
function Wait([scriptblock]$Condition,[string]$Description,[int]$Seconds=15) {
 $until=[DateTime]::UtcNow.AddSeconds($Seconds)
 do {
  if($app.HasExited) { throw "App exited while waiting for $Description" }
  if(& $Condition) { return }
  Start-Sleep -Milliseconds 100
 } while([DateTime]::UtcNow -lt $until)
 throw "Timed out waiting for $Description"
}
function Element([string]$Id) { Wait { $null -ne (Find $Id) } $Id; Find $Id }
function Invoke([string]$Id) {
 $item=Element $Id; $pattern=$null
 if($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke() }
 elseif($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select() }
 else { throw "No invoke/selection pattern: $Id" }
}
function ScrollTo([string]$Id) {
 $item=Element $Id
 if(-not $item.Current.IsOffscreen) { return }
 $pattern=$null
 if($item.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$pattern)) { $pattern.ScrollIntoView(); return }
 foreach($scrollId in @('ConnectionSettingsScroll','MainContentScroll')) {
  $scroll=Find $scrollId
  if(-not $scroll -or $scroll.Current.IsOffscreen) { continue }
  $sp=$null
  if(-not $scroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$sp)) { continue }
  for($i=0;$i -lt 18;$i++) {
   if(Visible $Id) { return }
   if(-not $sp.Current.VerticallyScrollable -or $sp.Current.VerticalScrollPercent -ge 100) { break }
   $sp.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement)
   Start-Sleep -Milliseconds 100
  }
 }
 Wait { Visible $Id } "$Id scrolls into view"
}
function SelectLevel([string]$Level) {
 [PreviewSmokeNative]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
 if(-not (Visible 'ProgressPreviewLevel')) { Invoke 'ProgressPreviewButton'; Wait { Visible 'ProgressPreviewLevel' } 'preview after focus change' }
 $combo=Element 'ProgressPreviewLevel'
 $expand=$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
 $expand.Expand(); Invoke ('ProgressPreview'+$Level)
}
function Capture([string]$Name) { & winapp ui screenshot -a $app.Id -o (Join-Path $root ($Name+'.png')) --json | Out-Null; if($LASTEXITCODE) { throw 'Screenshot failed.' } }
function AssertNoHorizontalScroll {
 $popup=Element 'ProgressPreviewPopup'
 $scrolls=$popup.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,$true))
 foreach($scroll in $scrolls) {
  $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
  Assert (-not $pattern.Current.HorizontallyScrollable) 'Preview has unwanted horizontal scrolling.'
 }
}
function AssertInlinePreview {
 $scroll=Element 'ConnectionSettingsScroll'; $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
 if($pattern.Current.VerticallyScrollable) { $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement); Start-Sleep -Milliseconds 250 }
 $combo=(Element 'ChatProgressDetail').Current.BoundingRectangle
 $button=(Element 'ProgressPreviewButton').Current.BoundingRectangle
 $scale=[PreviewSmokeNative]::GetDpiForWindow($app.MainWindowHandle)/96.0
 Assert ([Math]::Abs($combo.Bottom-$button.Bottom) -le 4*$scale) "Preview alignment delta: $([Math]::Abs($combo.Bottom-$button.Bottom)); combo=$combo button=$button scale=$scale"
 Assert ($button.Left -ge $combo.Right+4*$scale) 'Preview button overlaps the selection field.'
}
function AssertUpdateBadge([bool]$Expanded) {
 Wait { Visible 'NavAboutUpdateBadge' } 'cached update badge'
 $badge=(Element 'NavAboutUpdateBadge').Current.BoundingRectangle
 $item=(Element 'NavAbout').Current.BoundingRectangle
 $scale=[PreviewSmokeNative]::GetDpiForWindow($app.MainWindowHandle)/96.0
 Assert ($badge.Width -ge 23*$scale -and $badge.Height -ge 23*$scale) 'Update icon is clipped or shrunken.'
 if($Expanded) { Assert ($item.Right-$badge.Right -ge 4*$scale-0.5) 'Update icon has no right spacing.' }
}
function Size([int]$Width) {
 $app.Refresh(); $scale=[PreviewSmokeNative]::GetDpiForWindow($app.MainWindowHandle)/96.0
 Assert ([PreviewSmokeNative]::SetWindowPos($app.MainWindowHandle,[IntPtr]::Zero,20,20,[int]($Width*$scale),[int](840*$scale),0x0010)) 'Resize failed.'
 Start-Sleep -Milliseconds 200
}
try {
 foreach($language in @('ja','en')) { foreach($theme in @('light','dark')) {
  $scenario="$language-$theme"; $data=Join-Path $root $scenario
  New-Item -ItemType Directory -Path $data | Out-Null
  Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$data;TrustedWorkspaceRoots=$data;AgentProvider='custom';AgentExecutable='unused';AgentMode='exec';AutoConnectProvider='none';ChatProgressDetail='standard'}
  Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
  Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
  Json (Join-Path $data 'language.json') @{Language=$language}
  Json (Join-Path $data 'appearance.json') @{Theme=$theme}
  $candidate=@{version='9.9.9';tag='v9.9.9';assetName='ChatAgentBridge.Portable-9.9.9-win-x64.zip';downloadUri='https://github.com/mmiyaji/chat-agent-bridge/releases/download/v9.9.9/ChatAgentBridge.Portable-9.9.9-win-x64.zip';sha256=('a'*64);size=1234;releasePage='https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v9.9.9'}
  # Cached synthetic candidate only; checks are disabled and no update is downloaded.
  Json (Join-Path $data 'portable-update.json') @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$false;availableRelease=$candidate}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
  $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe')); $start.UseShellExecute=$false
  $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
  $app=[Diagnostics.Process]::Start($start)
  Element 'NavConnections' | Out-Null; Size 1160
  AssertUpdateBadge $true; Capture "$scenario-badge-expanded"
  Invoke 'TogglePaneButton'; Start-Sleep -Milliseconds 300
  AssertUpdateBadge $false; Capture "$scenario-badge-compact"
  Invoke 'TogglePaneButton'; Start-Sleep -Milliseconds 300
  Invoke 'NavConnections'
  if(Visible 'ConnectionScopeCommon') { Invoke 'ConnectionScopeCommon' }
  ScrollTo 'ChatProgressDetailHelpNotice'; ScrollTo 'ProgressPreviewButton'; Start-Sleep -Milliseconds 200
  AssertInlinePreview; Capture "$scenario-settings-inline"
  if($LayoutOnly) {
   Size 500; ScrollTo 'ChatProgressDetailHelpNotice'; ScrollTo 'ProgressPreviewButton'; AssertInlinePreview; Capture "$scenario-settings-inline-narrow"
   Size 1160; Invoke 'NavAbout'; Invoke 'AboutExit'; Element 'WindowCloseConfirmation' | Out-Null; Invoke 'PrimaryButton'
   Assert ($app.WaitForExit(15000)) 'App did not exit normally.'; $app=$null
   $checks.Add(@{name=$scenario;passed=$true;layoutOnly=$true}); Write-Output "PASS $scenario inline action, narrow layout, update badge expanded/compact"; continue
  }
  $realSelection=(Element 'ChatProgressDetail').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
  Invoke 'ProgressPreviewButton'
  Wait { Visible 'ProgressPreviewMessage' } 'preview popup'
  $samples=@{}
  foreach($level in @('Minimal','Standard','Detailed')) {
   SelectLevel $level
   foreach($stage in @('Received','Running','Completed')) {
    Invoke ('ProgressPreview'+$stage)
    $message=(Element 'ProgressPreviewMessage').Current.Name
    Assert (-not [string]::IsNullOrWhiteSpace($message)) "$level $stage sample is blank"
    Assert (-not $message.Contains('ProgressPreview')) 'Preview exposes untranslated sample resource keys.'
    Assert (-not (Element 'ProgressPreviewStatus').Current.Name.Contains('ProgressPreview')) 'Preview exposes an untranslated stage resource key.'
    $samples["$level-$stage"]=$message
   }
   AssertNoHorizontalScroll
   Capture "$scenario-$level"
  }
  Assert ($samples['Detailed-Running'].Length -gt $samples['Minimal-Running'].Length) 'Detail modes show no difference.'
  Assert (-not $samples['Minimal-Running'].Contains("`n")) 'Minimal sample is not one line.'
  Assert ((Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).ChatProgressDetail -eq 'standard') 'Preview changed the real saved setting.'
  Assert ((Element 'ChatProgressDetail').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name -eq $realSelection) 'Preview changed the actual settings selection.'
  Invoke 'ProgressPreviewReceived'; Invoke 'ProgressPreviewPlay'
  Wait { (Element 'ProgressPreviewMessage').Current.Name -ne $samples['Detailed-Received'] } 'animated stage advances' 10
  Wait { (Element 'ProgressPreviewMessage').Current.Name -eq $samples['Detailed-Completed'] } 'playback reaches completion' 12
  Start-Sleep -Milliseconds 2500
  Assert ((Element 'ProgressPreviewMessage').Current.Name -eq $samples['Detailed-Completed']) 'Playback loops after completion.'
  [PreviewSmokeNative]::SetForegroundWindow($app.MainWindowHandle) | Out-Null; (Element 'ProgressPreviewPlay').SetFocus();[Windows.Forms.SendKeys]::SendWait('{ESC}')
  Wait { -not (Visible 'ProgressPreviewMessage') } 'Esc closes preview'
  Size 500; ScrollTo 'ChatProgressDetailHelpNotice'; ScrollTo 'ProgressPreviewButton'; Start-Sleep -Milliseconds 200; AssertInlinePreview; Capture "$scenario-settings-inline-narrow"; Invoke 'ProgressPreviewButton'
  Wait { Visible 'ProgressPreviewMessage' } 'narrow preview'
  SelectLevel 'Detailed'; Invoke 'ProgressPreviewRunning'; AssertNoHorizontalScroll; Capture "$scenario-narrow"
  $app.Refresh(); $window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).Current.BoundingRectangle
  foreach($id in @('ProgressPreviewLevel','ProgressPreviewPlay','ProgressPreviewCompleted')) {
   $item=Element $id; $rect=$item.Current.BoundingRectangle
   Assert (-not $item.Current.IsOffscreen -and $rect.Left -ge $window.Left -and $rect.Right -le $window.Right) "Narrow preview clips $id"
  }
  [PreviewSmokeNative]::SetForegroundWindow($app.MainWindowHandle) | Out-Null; (Element 'ProgressPreviewPlay').SetFocus(); [Windows.Forms.SendKeys]::SendWait('{ESC}')
  Wait { -not (Visible 'ProgressPreviewMessage') } 'narrow preview dismissal'
  Size 1160; Invoke 'NavAbout'; Invoke 'AboutExit'; Element 'WindowCloseConfirmation' | Out-Null; Invoke 'PrimaryButton'
  Assert ($app.WaitForExit(15000)) 'App did not exit normally.'; $app=$null
  $checks.Add(@{name=$scenario;passed=$true;samples=$samples});Write-Output "PASS $scenario preview levels/stages, playback, Esc, narrow layout, no settings changes"
 }}
 Json (Join-Path $root 'results.json') @{passed=$true;checks=@($checks);root=$root}; Write-Output "Evidence: $root"
} catch {
 if($app -and -not $app.HasExited) { try { Capture 'failure' } catch {} }
 Json (Join-Path $root 'results.json') @{passed=$false;error=$_.Exception.Message;checks=@($checks);root=$root};throw
} finally { if($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force } }
