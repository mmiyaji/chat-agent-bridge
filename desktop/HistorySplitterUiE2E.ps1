param([Parameter(Mandatory)][string]$AppDirectory,[ValidateSet('ja','en')][string]$Language='ja')
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an isolated portable payload under desktop/artifacts.'}
$data=Join-Path $artifacts ('history-splitter-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $data|Out-Null
$app=$null;$hwnd=[IntPtr]::Zero;$checks=[Collections.Generic.List[string]]::new()
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class HistorySplitterNative {
 [StructLayout(LayoutKind.Sequential)] public struct Point {public int X,Y;}
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$cursor=[HistorySplitterNative+Point]::new();[void][HistorySplitterNative]::GetCursorPos([ref]$cursor)
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Pass([string]$Name){$checks.Add($Name);Write-Output "PASS $Name"}
function Write-Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 14|Set-Content (Join-Path $data $Name) -Encoding utf8}
function UI([string[]]$Arguments){$value=winapp ui @Arguments -a $app.Id --json;if($LASTEXITCODE){throw "UI action failed: $($Arguments -join ' ')"};return $value}
function Element([string]$Id){([Windows.Automation.AutomationElement]::FromHandle($hwnd)).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Width {return (Element 'SavedSessionsList').Current.BoundingRectangle.Width/$scale}
function Resize([int]$Width){[void][HistorySplitterNative]::SetWindowPos($hwnd,[IntPtr]::Zero,60,60,[int]($Width*$scale),[int](850*$scale),0x0014);Start-Sleep -Milliseconds 500}
function Assert-CommandBarWithinDetail([string]$Context) {
    $bar=Element 'SessionActionsCommandBar';$divider=Element 'SavedSessionsSplitter'
    Assert ($null -ne $bar -and -not $bar.Current.IsOffscreen) "${Context}: session CommandBar is not visible."
    Assert ($null -ne $divider -and -not $divider.Current.IsOffscreen) "${Context}: session divider is not visible."
    $barBounds=$bar.Current.BoundingRectangle;$dividerBounds=$divider.Current.BoundingRectangle
    $windowBounds=([Windows.Automation.AutomationElement]::FromHandle($hwnd)).Current.BoundingRectangle
    Assert ($barBounds.Width -gt 0 -and $barBounds.Height -gt 0) "${Context}: session CommandBar has no usable bounds."
    Assert ($barBounds.Left -ge $dividerBounds.Right-2 -and $barBounds.Right -le $windowBounds.Right-8) "${Context}: session CommandBar extends outside the detail pane."
}
function Foreground {
    [void][HistorySplitterNative]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 150
    Assert ([HistorySplitterNative]::GetForegroundWindow() -eq $hwnd) 'Refusing to send input without the test window in the foreground.'
}
function Move-Mouse([int]$X,[int]$Y) {
    $left=[HistorySplitterNative]::GetSystemMetrics(76);$top=[HistorySplitterNative]::GetSystemMetrics(77)
    $width=[HistorySplitterNative]::GetSystemMetrics(78);$height=[HistorySplitterNative]::GetSystemMetrics(79)
    [HistorySplitterNative]::mouse_event(0xC001,[uint32](($X-$left)*65535/($width-1)),[uint32](($Y-$top)*65535/($height-1)),0,[UIntPtr]::Zero)
}
function Drag([int]$Distance){
    Foreground
    $bounds=(Element 'SavedSessionsSplitter').Current.BoundingRectangle
    Assert ($bounds.Width -gt 0 -and $bounds.Height -gt 0) 'Splitter is not visible.'
    $window=([Windows.Automation.AutomationElement]::FromHandle($hwnd)).Current.BoundingRectangle
    $x=[int]($bounds.X+$bounds.Width/2);$y=[int]($bounds.Y+$bounds.Height/2)
    $target=[int][Math]::Clamp($x+$Distance*$scale,$window.Left+8,$window.Right-8)
    Move-Mouse $x $y
    Start-Sleep -Milliseconds 100
    [HistorySplitterNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    try {for($i=1;$i -le 12;$i++){Move-Mouse ([int]($x+($target-$x)*$i/12)) $y;Start-Sleep -Milliseconds 20}}
    finally {[HistorySplitterNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)}
    Start-Sleep -Milliseconds 250
}
function Capture([string]$Name){UI @('screenshot','-o',(Join-Path $data ($Name+'.png')))|Out-Null}
try {
    $bindings=@(0..1|ForEach-Object {@{Key=@{Provider='slack';ChannelId='Cfixture';ThreadId="thread-$_";UserId='Ufixture';Origin='slack'};WorkspaceRoot=$data;AgentExecutable='codex';AgentArguments='exec --json -';SessionId=[guid]::NewGuid().ToString('N');UpdatedAt=[DateTimeOffset]::UtcNow.AddMinutes(-$_).ToString('o');DisplayName="Splitter test conversation $_"}})
    Write-Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$data;AgentExecutable='unused';AgentProvider='custom';AgentMode='exec';AgentArguments='';TrustedWorkspaceRoots=$data;AutoConnectProvider='none'}
    Write-Json 'sessions.json' @{SchemaVersion=1;Bindings=$bindings;Workspaces=@()}
    Write-Json 'history.json' @{SchemaVersion=1;Records=@()}
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    $policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Current policy version missing.'
    Write-Json 'consent.json' @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme=$(if($Language -eq 'ja'){'dark'}else{'light'})}
    Write-Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
    Write-Json 'portable-update.json' @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$true}
    $settingsHash=(Get-FileHash (Join-Path $data 'settings.json')).Hash
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
    $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
    $app=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavHistory','-t','15000')|Out-Null
    $app.Refresh();$hwnd=$app.MainWindowHandle;$scale=[HistorySplitterNative]::GetDpiForWindow($hwnd)/96.0
    UI @('invoke','NavHistory')|Out-Null
    Resize 1400
    UI @('wait-for','SavedSessionsSplitter','-t','5000')|Out-Null
    $list=Element 'SavedSessionsList'
    $rows=$list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
    Assert ($rows.Count -eq 2) 'Fixture conversations missing.'
    $rows[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Capture 'initial'
    $before=Width;Drag 90;$after=Width
    Capture 'after-drag'
    Assert ($after -gt $before+40) 'Mouse drag did not increase the list width.'
    Capture 'dragged-wide'
    Pass 'Mouse drag changes the list/detail split'
    $rows[1].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 200
    Assert ([Math]::Abs((Width)-$after) -lt 3) 'Selecting a conversation reset the list width.'
    UI @('invoke','NavOverview')|Out-Null;UI @('invoke','NavHistory')|Out-Null;Start-Sleep -Milliseconds 200
    Assert ([Math]::Abs((Width)-$after) -lt 3) 'Navigation reset the list width.'
    Pass 'Selection and navigation preserve the adjusted width'
    Foreground;(Element 'SavedSessionsSplitter').SetFocus();[Windows.Forms.SendKeys]::SendWait('{LEFT 4}');Start-Sleep -Milliseconds 200
    Assert ((Width) -lt $after-1) 'Keyboard resizing did not work.'
    Pass 'Focused splitter supports arrow keys'
    Drag -700
    Assert ((Width) -ge 278 -and (Width) -lt $after) 'Dragging beyond the minimum did not preserve a usable list.'
    Drag 900
    $maxWidth=Width
    Capture 'minimum-detail'
    Assert-CommandBarWithinDetail 'Minimum detail width'
    Pass 'Both panes retain usable minimum widths'
    Resize 720
    $split=Element 'SavedSessionsSplitter'
    Assert (-not $split -or $split.Current.IsOffscreen -or $split.Current.BoundingRectangle.Width -eq 0) 'Narrow layout still shows the splitter.'
    Capture 'narrow'
    Resize 1400
    Assert ([Math]::Abs((Width)-$maxWidth) -lt 3) 'Returning from narrow mode lost the preferred width.'
    Resize 1160
    Assert-CommandBarWithinDetail 'Clamped window'
    Capture 'clamped-window'
    Resize 1400
    Assert ([Math]::Abs((Width)-$maxWidth) -lt 3) 'Temporary width clamping replaced the preferred width.'
    Pass 'Narrow layout hides the splitter and wider layouts restore/clamp the preference'
    Assert ((Get-FileHash (Join-Path $data 'settings.json')).Hash -eq $settingsHash) 'Resizing changed agent settings.'
} finally {
    [HistorySplitterNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    [void][HistorySplitterNative]::SetCursorPos($cursor.X,$cursor.Y)
    if($app -and -not $app.HasExited){try{([Windows.Automation.AutomationElement]::FromHandle($hwnd)).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}catch{};if(-not $app.WaitForExit(8000)){Stop-Process -Id $app.Id -Force}}
    @{checks=$checks.ToArray();dataDirectory=$data;language=$Language}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $data 'results.json') -Encoding utf8
    Write-Output "Splitter UI results: $data"
}
