# Real mouse drags followed by WM_CLOSE before either width-save debounce can expire.
# Uses fresh offline profiles; never connects to chat or runs an AI agent.
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public static class PanePersistenceInput {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr w, IntPtr a, int x, int y, int width, int height, uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w, uint message, IntPtr a, IntPtr b);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
 private static void Move(int x, int y) { mouse_event(0xC001, (uint)((x-GetSystemMetrics(76))*65535/(GetSystemMetrics(78)-1)), (uint)((y-GetSystemMetrics(77))*65535/(GetSystemMetrics(79)-1)),0,UIntPtr.Zero); }
 public static double DragThenClose(IntPtr w, int x, int y, int delta) {
   var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
   try {
     uint pid; var target = GetWindowThreadProcessId(w,out pid);
     var foreground = GetWindowThreadProcessId(GetForegroundWindow(),out pid);
     var current = GetCurrentThreadId();
     if(foreground != current) AttachThreadInput(current,foreground,true);
     if(target != current && target != foreground) AttachThreadInput(current,target,true);
     try { SetForegroundWindow(w); } finally {
       if(target != current && target != foreground) AttachThreadInput(current,target,false);
       if(foreground != current) AttachThreadInput(current,foreground,false);
     }
     if(GetForegroundWindow() != w) throw new Exception("Test window did not become foreground");
     Move(x,y); Thread.Sleep(60);
     mouse_event(2,0,0,0,UIntPtr.Zero); Thread.Sleep(25);
     for(int n=1;n<=5;n++) { Move(x+delta*n/5,y); Thread.Sleep(25); }
     var elapsed = Stopwatch.StartNew();
     mouse_event(4,0,0,0,UIntPtr.Zero);
     Thread.Sleep(25); // Let the pointer-release commit reach the UI before closing.
     if(!PostMessage(w,0x10,IntPtr.Zero,IntPtr.Zero)) throw new Exception("WM_CLOSE failed");
     return elapsed.Elapsed.TotalMilliseconds + 25;
   } finally { mouse_event(4,0,0,0,UIntPtr.Zero); SetThreadDpiAwarenessContext(old); }
 }
}
"@
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or
    -not $appRoot.StartsWith($artifactRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
 throw 'Use an isolated ordinary portable publish under desktop/artifacts.'
}
$root = Join-Path $artifactRoot ('pane-persistence-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$results = [Collections.Generic.List[object]]::new()
$script:app = $null
$script:window = $null
function Json($path,$value) { [IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false)) }
function Find($id) {
 $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-Element($id) {
 $end = [DateTime]::UtcNow.AddSeconds(20)
 do {
  $element = Find $id
  if ($element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Width -gt 0) { return $element }
  Start-Sleep -Milliseconds 100
 } while ([DateTime]::UtcNow -lt $end)
 throw "Visible element missing: $id"
}
function Start-App($data,$nav) {
 $start = [Diagnostics.ProcessStartInfo]::new($exe)
 $start.UseShellExecute = $false; $start.WorkingDirectory = $appRoot
 $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
 [void]$start.Environment.Remove('CAB_WINUI_DATA_DIR')
 $script:app = [Diagnostics.Process]::Start($start)
 $script:window = $null
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id)
 $end = [DateTime]::UtcNow.AddSeconds(25)
 do {
  foreach($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)) {
   if($w.Current.Name -eq 'Chat Agent Bridge') { $script:window=$w; break }
  }
  if($script:window) { break }
  if($script:app.HasExited) { throw 'Application exited before its window opened.' }
  Start-Sleep -Milliseconds 100
 } while([DateTime]::UtcNow -lt $end)
 if(-not $script:window) { throw 'Application window did not appear.' }
 $hwnd=[IntPtr]$script:window.Current.NativeWindowHandle
 $scale=[PanePersistenceInput]::GetDpiForWindow($hwnd)/96.0
 [void][PanePersistenceInput]::SetWindowPos($hwnd,[IntPtr]::Zero,20,20,[int](1160*$scale),[int](800*$scale),0x0040)
 [void][PanePersistenceInput]::SetForegroundWindow($hwnd)
 $element=Wait-Element $nav
 $selection=$null
 if($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)) {
  ([Windows.Automation.SelectionItemPattern]$selection).Select()
 } else { ($element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }
 Start-Sleep -Milliseconds 350
}
function Stop-App {
 if($script:app -and -not $script:app.HasExited) {
  [void][PanePersistenceInput]::PostMessage([IntPtr]$script:window.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
  if(-not $script:app.WaitForExit(12000)) { throw 'Normal shutdown did not complete.' }
 }
}
$policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
if(-not $policy) { throw 'Missing consent policy version.' }
$cases=@(
 @{name='sidebar';nav='NavOverview';sizer='SidebarSizer';field='SidebarWidth';initial=180},
 @{name='agent';nav='NavAgentSettings';sizer='AgentScopeSizer';field='SettingsNavigationWidth';initial=200},
 @{name='chat';nav='NavConnections';sizer='ConnectionScopeSizer';field='SettingsNavigationWidth';initial=200},
 @{name='inbox';nav='NavChat';sizer='InboxConversationsSplitter';field='InboxListWidth';initial=340}
)
try {
 foreach($language in @('en','ja')) { foreach($theme in @('light','dark')) { foreach($case in $cases) {
  $name=$language+'-'+$theme+'-'+$case.name
  $data=Join-Path $root $name; $workspace=Join-Path $data 'workspace'
  New-Item -ItemType Directory -Path $workspace -Force | Out-Null
  Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$workspace;TrustedWorkspaceRoots=$workspace;AgentProvider='custom';AgentExecutable='unused';AgentArguments='';AgentMode='exec';AutoConnectProvider='none'}
  Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
  Json (Join-Path $data 'consent.json') @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Json (Join-Path $data 'language.json') @{Language=$language}
  Json (Join-Path $data 'appearance.json') @{Theme=$theme}
  Json (Join-Path $data 'background.json') @{CloseBehavior='ExitApp'}
  Json (Join-Path $data 'portable-update.json') @{SchemaVersion=1;AutomaticChecksEnabled=$false;IncludePrereleases=$false}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
  $layout=@{SidebarWidth=180;SettingsNavigationWidth=200;InboxListWidth=340}
  $path=Join-Path $data 'layout.json'; Json $path $layout
  Start-App $data $case.nav
  & winapp ui screenshot -w $script:window.Current.NativeWindowHandle -o (Join-Path $root ($name+'-before.png')) --json | Out-Null
  $handle=Wait-Element $case.sizer
  $before=$handle.Current.BoundingRectangle
  $hwnd=[IntPtr]$script:window.Current.NativeWindowHandle
  $scale=[PanePersistenceInput]::GetDpiForWindow($hwnd)/96.0
  $delta=[int](40*$scale)
  Write-Host "Drag $name bounds=$before scale=$scale"
  $latency=[PanePersistenceInput]::DragThenClose($hwnd,[int]($before.Left+$before.Width/2),[int]($before.Top+$before.Height/2),$delta)
  if($latency -ge 300) { throw "Close was too late: $latency ms" }
  if(-not $script:app.WaitForExit(12000)) { throw "Immediate normal close did not exit: $name" }
  $saved=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  $expected=$case.initial+$delta/$scale
  if([Math]::Abs($saved.($case.field)-$expected) -gt 8) { throw "Width lost on exit ($name): $($saved.($case.field)), expected $expected" }
  foreach($field in $layout.Keys) {
   if($field -ne $case.field -and $saved.$field -ne $layout[$field]) { throw "Unrelated width changed: $field" }
  }
  Start-App $data $case.nav
  $after=(Wait-Element $case.sizer).Current.BoundingRectangle
  $restoredDelta=($saved.($case.field)-$case.initial)*$scale
  if([Math]::Abs(($after.Left-$before.Left)-$restoredDelta) -gt 3) { throw "Restored UI width incorrect ($name): delta=$($after.Left-$before.Left), expected $restoredDelta" }
  & winapp ui screenshot -w $script:window.Current.NativeWindowHandle -o (Join-Path $root ($name+'.png')) --json | Out-Null
  if($LASTEXITCODE) { throw 'Screenshot failed.' }
  Stop-App
  $results.Add(@{case=$name;passed=$true;savedWidth=$saved.($case.field);restoredPixelDelta=$after.Left-$before.Left;closeDelayMs=$latency})
  Write-Host "PASS $name (saved=$($saved.($case.field)), close=$([Math]::Round($latency,1)) ms)"
 } } }
 Json (Join-Path $root 'results.json') @{passed=$true;checks=$results.ToArray();generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
 Write-Host "Evidence: $root"
} catch {
 Json (Join-Path $root 'results.json') @{passed=$false;checks=$results.ToArray();error=$_.ToString()}
 throw
} finally {
 if($script:app -and -not $script:app.HasExited) {
  try { Stop-App } catch {
   # This is only the exact child process started by this script, never another app instance.
   if($script:app.MainModule.FileName -eq $exe) { $script:app.Kill($true); [void]$script:app.WaitForExit(7000) }
  }
 }
}
