# Starts the demo CAB build on its isolated data folder; stage.ps1 places it next to Mattermost.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class C1 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
}
'@
[C1]::SetProcessDPIAware() | Out-Null
. (Join-Path $PSScriptRoot 'demo-env.ps1')
$exe = $CabExe
if (-not (Test-Path $exe)) { throw "Demo CAB build not found: $exe (publish it to desktop\artifacts\demo-app or set CAB_DEMO_APP)." }
$data = $CabData
$p = Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.CommandLine -like "*$data*" } | ForEach-Object { Get-Process -Id $_.ProcessId } | Select-Object -First 1
if (-not $p) {
  $p = Start-Process $exe -ArgumentList '--data-dir', "`"$data`"" -PassThru
  for ($i = 0; $i -lt 60; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 500 }
  Start-Sleep 3
}
$p.Refresh()
[C1]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 880, 0, 1040, 1032, 0x0040) | Out-Null
"cab pid $($p.Id) placed"
