# Shared helpers for the demo: visible cursor moves, CAB UI Automation, and Mattermost typing over CDP.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class D1 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
}
'@
[D1]::SetProcessDPIAware() | Out-Null
. (Join-Path $PSScriptRoot 'demo-env.ps1')
. (Join-Path $PSScriptRoot 'cdp.ps1')

# Records the primary monitor's work area with Desktop Duplication (gdigrab drops frames at 30 fps).
function Start-DemoRecording([string]$Name) {
    if (-not $FfmpegExe) { throw 'ffmpeg was not found. Set CAB_DEMO_FFMPEG.' }
    # MKV stays playable even if the encoder is cut off while flushing.
    $out = Join-Path $DemoData "$Name-raw.mkv"
    if (Test-Path $out) { Remove-Item $out }
    $psi = [Diagnostics.ProcessStartInfo]::new($FfmpegExe,
        "-y -nostats -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30:video_size=1920x1032:offset_x=0:offset_y=0:draw_mouse=1 -vf hwdownload,format=bgra -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p `"$out`"")
    # Progress output is disabled: an unread stderr pipe fills up and stalls ffmpeg.
    $psi.RedirectStandardInput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($psi)
    Start-Sleep 2
    $process
}
function Stop-DemoRecording($Process) {
    $Process.StandardInput.Write('q'); $Process.StandardInput.Flush()
    $Process.WaitForExit(120000)
}

function Move-Cursor([double]$X, [double]$Y, [int]$Ms = 700) {
    $p = New-Object D1+P; [D1]::GetCursorPos([ref]$p) | Out-Null
    $steps = [Math]::Max(10, [int]($Ms / 15))
    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps; $e = if ($t -lt 0.5) { 2 * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 2) / 2 }
        [D1]::SetCursorPos([int]($p.X + ($X - $p.X) * $e), [int]($p.Y + ($Y - $p.Y) * $e)) | Out-Null
        Start-Sleep -Milliseconds 15
    }
}

function Get-CabProcess {
    if ($script:cabProcess -and -not $script:cabProcess.HasExited) { return $script:cabProcess }
    $data = $CabData
    $script:cabProcess = Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.CommandLine -like "*$data*" } | ForEach-Object { Get-Process -Id $_.ProcessId } | Select-Object -First 1
    if (-not $script:cabProcess) { throw 'Demo CAB is not running.' }
    $script:cabProcess
}
function Get-CabWindow { [Windows.Automation.AutomationElement]::FromHandle((Get-CabProcess).MainWindowHandle) }
function Find-Cab([string]$Id, [int]$TimeoutSec = 10) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $e = (Get-CabWindow).FindFirst('Descendants', $c)
        if ($e -and -not $e.Current.IsOffscreen) { return $e }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "CAB element $Id not found."
}
# Moves the visible cursor onto the element, then activates it through UI Automation (no synthetic keys or clicks).
function Invoke-Cab([string]$Id, [int]$TimeoutSec = 10) {
    $e = Find-Cab $Id $TimeoutSec
    $r = $e.Current.BoundingRectangle
    Move-Cursor ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
    Start-Sleep -Milliseconds 350
    $pattern = $null
    if ($e.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
    elseif ($e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
    else { throw "CAB element $Id cannot be activated." }
}

# Clicks a CAB button by its visible name (for dialog buttons that have no AutomationId).
function Click-CabButtonByName([string]$Name, [int]$TimeoutSec = 10) {
    $c = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Name)))
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not ($e = (Get-CabWindow).FindFirst('Descendants', $c)) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (-not $e) { throw "CAB button $Name not found." }
    $r = $e.Current.BoundingRectangle
    Move-Cursor ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
    Start-Sleep -Milliseconds 400
    Click-CabUnderCursor
}

# Opens the conversation's request list and clicks the request that waits for approval.
function Select-CabPendingRequest([int]$TimeoutSec = 20) {
    $combo = Find-Cab 'ChatRequests' 5
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $expand = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $r = $combo.Current.BoundingRectangle
    Move-Cursor ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
    Start-Sleep -Milliseconds 300
    # Items exist in the automation tree only while the drop-down is open.
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $pending = $null
    while (-not $pending -and (Get-Date) -lt $deadline) {
        $expand.Expand()
        Start-Sleep -Milliseconds 700
        $pending = @($combo.FindAll('Descendants', $c)) | Where-Object { $_.Current.Name -match '承認待ち|Awaiting approval' } | Select-Object -Last 1
        if (-not $pending) { try { $expand.Collapse() } catch { }; Start-Sleep -Milliseconds 800 }
    }
    if (-not $pending) { return $false }
    $r = $pending.Current.BoundingRectangle
    if ($r.Width -gt 0) { Move-Cursor ($r.X + [Math]::Min($r.Width / 2, 200)) ($r.Y + $r.Height / 2); Start-Sleep -Milliseconds 400 }
    $pending.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    try { $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    $true
}

# Selects an item of a CAB list (the first by default, e.g. the newest conversation) with a visible cursor move.
function Select-CabFirstItem([string]$ListId, [int]$TimeoutSec = 30, [int]$Index = 0) {
    $list = Find-Cab $ListId $TimeoutSec
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $item = $null
    while (-not $item -and (Get-Date) -lt $deadline) {
        $item = @($list.FindAll('Descendants', $c))[$Index]
        if (-not $item) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $item) { throw "No item $Index in $ListId." }
    $r = $item.Current.BoundingRectangle
    Move-Cursor ($r.X + [Math]::Min($r.Width / 2, 200)) ($r.Y + $r.Height / 2)
    Start-Sleep -Milliseconds 350
    Click-CabUnderCursor
}
# Throws unless the window under the cursor belongs to the demo CAB process, so no input reaches another app.
function Assert-CursorOverCab {
    $p = New-Object D1+P; [D1]::GetCursorPos([ref]$p) | Out-Null
    $top = [D1]::GetAncestor([D1]::WindowFromPoint($p), 2)
    $procId = 0; [D1]::GetWindowThreadProcessId($top, [ref]$procId) | Out-Null
    if ($procId -ne (Get-CabProcess).Id) { throw 'The window under the cursor is not the demo CAB; input skipped.' }
}
# Left-clicks at the cursor only when the window under it belongs to the demo CAB process.
function Click-CabUnderCursor {
    Assert-CursorOverCab
    [D1]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [D1]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}
# Scrolls the CAB page with the mouse wheel (positive notches scroll down), a notch at a time so it reads on camera.
function Scroll-Cab([int]$Notches, [double]$X, [double]$Y, [int]$StepMs = 140) {
    Move-Cursor $X $Y 500
    Assert-CursorOverCab
    $delta = if ($Notches -gt 0) { [uint32]([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]-120), 0)) } else { [uint32]120 }
    for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) { [D1]::mouse_event(0x0800, 0, 0, $delta, [UIntPtr]::Zero); Start-Sleep -Milliseconds $StepMs }
}

# Mattermost page helpers (page coordinates are converted to screen coordinates at 100% DPI).
function Get-PageOrigin($ws) {
    Invoke-Js $ws 'JSON.stringify({x: window.screenX + (window.outerWidth - window.innerWidth) / 2, y: window.screenY + window.outerHeight - window.innerHeight})' | ConvertFrom-Json
}
function Move-ToSelector($ws, [string]$Selector) {
    $o = Get-PageOrigin $ws
    $r = Invoke-Js $ws "JSON.stringify((() => { const r = document.querySelector('$Selector').getBoundingClientRect(); return {x: r.x + Math.min(r.width / 2, 120), y: r.y + r.height / 2}; })())" | ConvertFrom-Json
    Move-Cursor ($o.x + $r.x) ($o.y + $r.y)
}
# Hovers the newest post with the real cursor, then opens its thread from the reply icon (Shift+Up as fallback).
function Open-LatestThread($ws) {
    Move-ToSelector $ws '.post-list__dynamic .post:last-of-type .post__body'
    Start-Sleep -Milliseconds 900
    $hasIcon = Invoke-Js $ws "!!document.querySelector('.post-list__dynamic .post:last-of-type [id^=CENTER_commentIcon_]')"
    if ($hasIcon) {
        Move-ToSelector $ws '.post-list__dynamic .post:last-of-type [id^=CENTER_commentIcon_]'
        Start-Sleep -Milliseconds 400
        $null = Invoke-Js $ws "document.querySelector('.post-list__dynamic .post:last-of-type [id^=CENTER_commentIcon_]').click()"
    } else {
        $null = Invoke-Js $ws "document.querySelector('#post_textbox').focus()"
        $null = Invoke-Cdp $ws 'Input.dispatchKeyEvent' @{ type = 'keyDown'; key = 'ArrowUp'; code = 'ArrowUp'; windowsVirtualKeyCode = 38; modifiers = 8 }
        $null = Invoke-Cdp $ws 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = 'ArrowUp'; code = 'ArrowUp'; windowsVirtualKeyCode = 38; modifiers = 8 }
    }
    for ($i = 0; $i -lt 20 -and -not (Invoke-Js $ws "!!document.querySelector('#reply_textbox')"); $i++) { Start-Sleep -Milliseconds 300 }
    "thread icon: $hasIcon"
}
function Send-MattermostMessage($ws, [string]$Text, [int]$CharMs = 55, [string]$Box = '#post_textbox') {
    Move-ToSelector $ws $Box
    Start-Sleep -Milliseconds 300
    $null = Invoke-Js $ws "document.querySelector('$Box').focus()"
    foreach ($ch in $Text.ToCharArray()) {
        $null = Invoke-Cdp $ws 'Input.insertText' @{ text = [string]$ch }
        Start-Sleep -Milliseconds $CharMs
    }
    Start-Sleep -Milliseconds 700
    $null = Invoke-Cdp $ws 'Input.dispatchKeyEvent' @{ type = 'keyDown'; key = 'Enter'; code = 'Enter'; windowsVirtualKeyCode = 13; text = "`r" }
    $null = Invoke-Cdp $ws 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = 'Enter'; code = 'Enter'; windowsVirtualKeyCode = 13 }
}
