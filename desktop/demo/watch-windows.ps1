# Logs every new visible top-level window (class, title, process) for a while, to catch brief console flashes.
param([int]$Seconds = 120, [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\demo\windows.log'))
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class Ww {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static List<string> Visible() {
    var list = new List<string>();
    EnumWindows((h, l) => {
      if (IsWindowVisible(h)) {
        var c = new StringBuilder(256); GetClassName(h, c, 256); var t = new StringBuilder(256); GetWindowText(h, t, 256);
        uint pid; GetWindowThreadProcessId(h, out pid);
        list.Add(h.ToInt64() + "|" + c + "|" + t + "|" + pid);
      }
      return true; }, IntPtr.Zero);
    return list;
  }
}
'@
$seen = [Collections.Generic.HashSet[string]]::new([string[]](([Ww]::Visible()) | ForEach-Object { ($_ -split '\|')[0] }))
Set-Content $Out "watch start $(Get-Date -Format HH:mm:ss.fff)"
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
    foreach ($w in [Ww]::Visible()) {
        $f = $w -split '\|'
        if ($seen.Add($f[0])) {
            $name = (Get-Process -Id $f[3] -ErrorAction SilentlyContinue).ProcessName
            Add-Content $Out ("{0:HH:mm:ss.fff} class={1} title={2} pid={3} proc={4}" -f (Get-Date), $f[1], $f[2], $f[3], $name)
        }
    }
    Start-Sleep -Milliseconds 25
}
Add-Content $Out "watch end"
