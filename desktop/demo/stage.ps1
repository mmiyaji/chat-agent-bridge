# Places the backdrop, Mattermost and CAB on the primary monitor in that z-order. -Off removes the backdrop.
param([switch]$Off)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class St {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string t);
}
'@
[St]::SetProcessDPIAware() | Out-Null
$root = $PSScriptRoot
. (Join-Path $root 'demo-env.ps1')
$backdrops = Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" | Where-Object { $_.CommandLine -like '*backdrop.ps1*' }
$profile = Join-Path $DemoData 'chrome-profile'
$chrome = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profile*" -and $_.CommandLine -notlike '*--type=*' } | ForEach-Object { Get-Process -Id $_.ProcessId } | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$data = $CabData
$cab = Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.CommandLine -like "*$data*" } | ForEach-Object { Get-Process -Id $_.ProcessId } | Select-Object -First 1
if ($Off) {
    # Drop topmost from the demo windows and remove the backdrop.
    foreach ($w in $chrome, $cab) { if ($w) { [St]::SetWindowPos($w.MainWindowHandle, [IntPtr]-2, 0, 0, 0, 0, 0x0013) | Out-Null } }
    $backdrops | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; 'unstaged'; return
}
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
"primary work area: $($wa.X),$($wa.Y) $($wa.Width)x$($wa.Height)"
if (-not $backdrops) { Start-Process pwsh -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$root\backdrop.ps1`"" -WindowStyle Hidden; Start-Sleep 3 }
$bd = [St]::FindWindow($null, 'CAB demo backdrop')
# Topmost in this order keeps other apps hidden behind the backdrop; system notifications still appear above.
$split = 880
[St]::SetWindowPos($bd, [IntPtr]-1, 0, 0, 0, 0, 0x0013) | Out-Null
[St]::SetWindowPos($chrome.MainWindowHandle, [IntPtr]-1, $wa.X, $wa.Y, $split, $wa.Height, 0x0050) | Out-Null
[St]::SetWindowPos($cab.MainWindowHandle, [IntPtr]-1, $wa.X + $split, $wa.Y, $wa.Width - $split, $wa.Height, 0x0050) | Out-Null
'staged'
