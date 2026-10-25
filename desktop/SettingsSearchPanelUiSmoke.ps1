# Offline native UI checks for the on-demand settings search, including real keyboard/mouse input.
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Threading;
public static class SearchPanelInput {
 [StructLayout(LayoutKind.Explicit,Size=40)] public struct Input { [FieldOffset(0)] public uint type;[FieldOffset(8)]public ushort vk;[FieldOffset(10)]public ushort scan;[FieldOffset(12)]public uint flags; }
 [DllImport("user32.dll")]public static extern uint SendInput(uint count,Input[] inputs,int size);
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr w);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr w,IntPtr z,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr w,uint m,IntPtr a,IntPtr b);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
 [DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")]public static extern bool AttachThreadInput(uint a,uint b,bool attach);
 [DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")]public static extern int GetSystemMetrics(int n);
 [DllImport("user32.dll")]public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 public static void Activate(IntPtr w) {
  uint pid;var target=GetWindowThreadProcessId(w,out pid);var fore=GetWindowThreadProcessId(GetForegroundWindow(),out pid);var own=GetCurrentThreadId();
  bool a=fore!=0&&fore!=own&&AttachThreadInput(own,fore,true);
  bool b=target!=own&&target!=fore&&AttachThreadInput(own,target,true);
  try {SetForegroundWindow(w);}finally{if(b)AttachThreadInput(own,target,false);if(a)AttachThreadInput(own,fore,false);}
  if(GetForegroundWindow()!=w)throw new Exception("Refusing keyboard input outside isolated test window.");
 }
 private static void Send(Input[] inputs){if(SendInput((uint)inputs.Length,inputs,40)!=inputs.Length)throw new Exception("SendInput failed");}
 public static void Key(ushort vk,bool ctrl=false){
  if(ctrl)Send(new[]{new Input{type=1,vk=17},new Input{type=1,vk=vk},new Input{type=1,vk=vk,flags=2},new Input{type=1,vk=17,flags=2}});
  else Send(new[]{new Input{type=1,vk=vk},new Input{type=1,vk=vk,flags=2}});
 }
 public static void Text(string text){foreach(char c in text)Send(new[]{new Input{type=1,scan=c,flags=4},new Input{type=1,scan=c,flags=6}});}
 public static void Click(int x,int y){var old=SetThreadDpiAwarenessContext(new IntPtr(-4));try{
  mouse_event(0xC001,(uint)((x-GetSystemMetrics(76))*65535/(GetSystemMetrics(78)-1)),(uint)((y-GetSystemMetrics(77))*65535/(GetSystemMetrics(79)-1)),0,UIntPtr.Zero);
  Thread.Sleep(80);mouse_event(2,0,0,0,UIntPtr.Zero);Thread.Sleep(40);mouse_event(4,0,0,0,UIntPtr.Zero);
 }finally{SetThreadDpiAwarenessContext(old);}}
}
"@
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot=(Resolve-Path -LiteralPath $AppDirectory).Path
$exe=Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if(-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or -not $appRoot.StartsWith($artifacts.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {throw 'Use an isolated ordinary portable publish under desktop/artifacts.'}
$root=Join-Path $artifacts ('settings-search-panel-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$results=[Collections.Generic.List[object]]::new()
$script:app=$null;$script:window=$null;$script:hwnd=[IntPtr]::Zero
function Json($path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))}
function Wait-For([scriptblock]$condition,[string]$name){$end=[DateTime]::UtcNow.AddSeconds(15);do{if(& $condition){return};if($script:app.HasExited){throw 'Test app exited.'};Start-Sleep -Milliseconds 80}while([DateTime]::UtcNow -lt $end);throw "Timed out: $name"}
function Windows {
 $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id)
 [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$filter)
}
function Find($id,[bool]$visible=$true){
 $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 foreach($window in (Windows)){
  $item=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
  if($item -and (-not $visible -or (-not $item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Width -gt 0))){return $item}
 }
 return $null
}
function Element($id){Wait-For {$null -ne (Find $id)} "visible $id";Find $id}
function Invoke($id){
 if($id -eq 'OpenSettingsSearch'){
  if($null -eq (Find $id)){Invoke 'TogglePaneButton'}
  [void](Element $id)
  # A NavigationView command is invoked by a real click, not SelectionItemPattern.Select.
  & winapp ui click $id -a $script:app.Id --json | Out-Null
  if($LASTEXITCODE){throw 'Could not click the sidebar search action.'}
  return
 }
 $e=Element $id;$selection=$null;if($e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)){([Windows.Automation.SelectionItemPattern]$selection).Select()}else{($e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()}
}
function Assert-Selected($id){
 $e=Find $id $false
 # Minimal NavigationView unloads its closed menu; verify the visible destination instead.
 if((-not $e -or $e.Current.IsOffscreen) -and $id -eq 'NavChat' -and $null -ne (Find 'InboxConversations')){return}
 if((-not $e -or $e.Current.IsOffscreen) -and $id -eq 'NavOverview'){
  Wait-For {$null -ne (Find 'LanguageSettingRow')} 'General search destination remains visible';return
 }
 if(-not $e -or -not (($e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected)){throw "Selected page changed unexpectedly: $id"}
}
function Assert-SearchAction {
 $e=Find 'OpenSettingsSearch' $false;$p=$null
 if($e -and $e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$p) -and ([Windows.Automation.SelectionItemPattern]$p).Current.IsSelected){throw 'Search action became a selected page.'}
}
function Closed {Wait-For {$null -eq (Find 'SettingsSearchPanel')} 'search panel closed';Start-Sleep -Milliseconds 200}
function Opened {
 [void](Element 'SettingsSearchPanel');$box=Element 'SettingsSearchBox'
 $edit=$box.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
 if(-not $edit){throw 'Search input missing.'}
 Wait-For {$edit.Current.HasKeyboardFocus} 'search input focus'
 # WinUI animates the popup bounds after Opened; inspect the settled layout.
 Start-Sleep -Milliseconds 200
 if(($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -ne ''){throw 'Search retained a stale query.'}
 return $edit
}
function Fits($element){
 $r=$element.Current.BoundingRectangle;$w=$script:window.Current.BoundingRectangle
 # The popup presenter includes a theme-shadow overhang. Its actual input must fit strictly.
 $overhang=if($element.Current.AutomationId -eq 'SettingsSearchPanel'){32*[SearchPanelInput]::GetDpiForWindow($script:hwnd)/96.0}else{0}
 if($r.Width -le 0 -or $r.Left -lt $w.Left-$overhang -or $r.Right -gt $w.Right+$overhang+1 -or $r.Top -lt $w.Top-$overhang -or $r.Bottom -gt $w.Bottom+$overhang){throw "Search control is clipped: $r in $w"}
}
function Type-Query($edit){[SearchPanelInput]::Activate($script:hwnd);$edit.SetFocus();[SearchPanelInput]::Text('Language');Wait-For {($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'Language'} 'typed search query';Wait-For {
 foreach($w in (Windows)){
  $label=$w.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Language / 表示言語'))
  if($label -and -not $label.Current.IsOffscreen){return $true}
 };return $false
 } 'matching language suggestion'}
function Capture($name){& winapp ui screenshot -a $script:app.Id -o (Join-Path $root ($name+'.png')) --json|Out-Null;if($LASTEXITCODE){throw 'Screenshot failed.'}}
function Resize($width){$scale=[SearchPanelInput]::GetDpiForWindow($script:hwnd)/96.0;[void][SearchPanelInput]::SetWindowPos($script:hwnd,[IntPtr]::Zero,20,20,[int]($width*$scale),[int](800*$scale),0x0040);Start-Sleep -Milliseconds 200}
function Stop-App {if($script:app -and -not $script:app.HasExited){[void][SearchPanelInput]::PostMessage($script:hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero);if(-not $script:app.WaitForExit(10000)){throw 'Normal exit did not complete.'}}}
$policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
if(-not $policy){throw 'Consent version missing.'}
try{
 foreach($language in @('en','ja')){foreach($theme in @('light','dark')){
  $prefix=$language+'-'+$theme;$data=Join-Path $root $prefix;$workspace=Join-Path $data 'workspace';New-Item -ItemType Directory -Path $workspace -Force|Out-Null
  Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$workspace;TrustedWorkspaceRoots=$workspace;AgentProvider='custom';AgentExecutable='unused';AgentArguments='';AgentMode='exec';AutoConnectProvider='none'}
  Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
  Json (Join-Path $data 'consent.json') @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Json (Join-Path $data 'language.json') @{Language=$language};Json (Join-Path $data 'appearance.json') @{Theme=$theme}
  Json (Join-Path $data 'background.json') @{CloseBehavior='ExitApp'}
  Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
  Json (Join-Path $data 'portable-update.json') @{SchemaVersion=1;AutomaticChecksEnabled=$false;IncludePrereleases=$false}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
  $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=$appRoot;$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data);[void]$start.Environment.Remove('CAB_WINUI_DATA_DIR')
  $script:app=[Diagnostics.Process]::Start($start);$script:window=$null
  Wait-For {foreach($w in (Windows)){if($w.Current.Name -eq 'Chat Agent Bridge'){$script:window=$w;return $true}};return $false} 'main window'
  $script:hwnd=[IntPtr]$script:window.Current.NativeWindowHandle
  [void](Element 'OpenSettingsSearch');$settingsHash=(Get-FileHash (Join-Path $data 'settings.json')).Hash
  foreach($mode in @(@{name='expanded';width=1160},@{name='folded';width=1160},@{name='compact';width=900},@{name='minimal';width=500})){
   $width=$mode.width;$name=$prefix+'-'+$mode.name;Resize 1160
   if($null -eq (Find 'NavChat')){Invoke 'TogglePaneButton'}
   Invoke 'NavChat';Resize $width
   if($mode.name -eq 'folded'){Invoke 'TogglePaneButton';Start-Sleep -Milliseconds 250}
   if($null -eq (Find 'OpenSettingsSearch')){Invoke 'TogglePaneButton';Start-Sleep -Milliseconds 250}
   Closed
   $button=Element 'OpenSettingsSearch';Fits $button
   $scale=[SearchPanelInput]::GetDpiForWindow($script:hwnd)/96.0
   $nav=Element 'MainNavigation';$navBounds=$nav.Current.BoundingRectangle;$buttonBounds=$button.Current.BoundingRectangle
   if($buttonBounds.Top -lt $navBounds.Top -or $buttonBounds.Left-$navBounds.Left -gt 80*$scale){throw 'Search action is not in the sidebar.'}
   if($mode.name -in @('folded','compact') -and $buttonBounds.Width -gt 80*$scale){throw 'Collapsed search action is not icon-only.'}
   if($mode.name -eq 'expanded' -and $buttonBounds.Width -lt 120*$scale){throw 'Expanded search action has no room for its label.'}
   Assert-Selected 'NavChat';Assert-SearchAction
   if($null -ne (Find 'SettingsSearchBox')){throw 'Search input is visible before opening.'}
   Capture ($name+'-closed')
   Invoke 'OpenSettingsSearch';$edit=Opened;Fits (Element 'SettingsSearchPanel');Fits $edit
   Assert-Selected 'NavChat';Assert-SearchAction
   $expected=if($language -eq 'ja'){'設定を検索'}else{'Search settings'}
   if((Element 'SettingsSearchBox').Current.Name -ne $expected){throw 'Search accessible label is not localized.'}
   Capture ($name+'-open')
   [SearchPanelInput]::Activate($script:hwnd);[SearchPanelInput]::Key(27);Closed
   Assert-Selected 'NavChat';Assert-SearchAction
   [SearchPanelInput]::Key(70,$true);$edit=Opened;Type-Query $edit;Capture ($name+'-suggestions')
   [SearchPanelInput]::Key(70,$true)
   Wait-For {$edit.Current.HasKeyboardFocus -and ($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'Language'} 'Ctrl+F retains the open search'
   [SearchPanelInput]::Key(27);Closed
   [SearchPanelInput]::Key(70,$true);$edit=Opened;Type-Query $edit
   [SearchPanelInput]::Key(13);Closed
   Assert-Selected 'NavOverview';Assert-SearchAction
   Wait-For {$null -ne (Find 'LanguageSettingRow')} 'search result scrolled into view'
   Start-Sleep -Milliseconds 400
   Invoke 'OpenSettingsSearch';[void](Opened)
   $panel=(Element 'SettingsSearchPanel').Current.BoundingRectangle;$w=$script:window.Current.BoundingRectangle
   $x=[int]($w.Right-30);$y=[int]($w.Bottom-30)
   if($panel.Contains([Windows.Point]::new($x,$y))){throw 'Outside-click test point is inside search panel.'}
   [SearchPanelInput]::Activate($script:hwnd);[SearchPanelInput]::Click($x,$y);Closed
   # Repeated activation uses a single panel and remains dismissible.
   for($i=0;$i -lt 3;$i++){[SearchPanelInput]::Key(70,$true);[void](Opened);[SearchPanelInput]::Key(27);Closed}
   if($mode.name -eq 'expanded'){
    [SearchPanelInput]::Key(53,$true);Wait-For {((Find 'NavAgentSettings' $false).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} 'Ctrl+5 still opens agent settings'
    [SearchPanelInput]::Key(49,$true);Wait-For {((Find 'NavOverview' $false).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} 'Ctrl+1 still opens General'
   }
   if((Get-FileHash (Join-Path $data 'settings.json')).Hash -ne $settingsHash){throw 'Search interaction changed agent/chat settings.'}
   $results.Add(@{case=$name;passed=$true;checks=@('sidebar action','expanded label or compact icon','selection preserved','click open','focus','localized name','bounds','Ctrl+F','Esc with suggestions','result navigation','outside click','repeat open/close','settings unchanged')})
   Write-Host "PASS $name"
  }
  Stop-App
 }}
 Json (Join-Path $root 'results.json') @{passed=$true;checks=$results.ToArray();generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')};Write-Host "Evidence: $root"
}catch{Json (Join-Path $root 'results.json') @{passed=$false;checks=$results.ToArray();error=$_.ToString()};try{Capture 'failure'}catch{};throw}
finally{if($script:app -and -not $script:app.HasExited){try{Stop-App}catch{if($script:app.MainModule.FileName -eq $exe){$script:app.Kill($true);[void]$script:app.WaitForExit(7000)}}}}
