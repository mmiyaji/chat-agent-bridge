# Opens the isolated demo Chrome with DevTools on 9333, signs alice in inside the page and opens #ai-requests.
param([ValidateSet('ja', 'en')][string]$Lang = 'ja')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class B2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
}
'@
[B2]::SetProcessDPIAware() | Out-Null
. (Join-Path $PSScriptRoot 'demo-env.ps1')
. (Join-Path $PSScriptRoot 'cdp.ps1')
if (-not $ChromeExe) { throw 'Google Chrome was not found. Set CAB_DEMO_CHROME.' }
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$profile = Join-Path $DemoData 'chrome-profile'
# Close only Chrome processes that use the demo profile.
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep 2
$chromeArgs = @("--user-data-dir=`"$profile`"", "--app=$($mm.serverUrl)/login", '--remote-debugging-port=9333', '--no-first-run',
          '--deny-permission-prompts', '--no-default-browser-check', '--disable-features=Translate', "--lang=$Lang", '--window-position=0,0', '--window-size=880,1032')
Start-Process $ChromeExe -ArgumentList $chromeArgs | Out-Null
for ($i = 0; $i -lt 40; $i++) { try { $null = Invoke-RestMethod 'http://127.0.0.1:9333/json' -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
Start-Sleep 3
$ws = Connect-Cdp
$body = (@{ login_id = 'alice'; password = $mm.alice.password } | ConvertTo-Json -Compress).Replace("'", "\'")
$status = Invoke-Js $ws "fetch('/api/v4/users/login',{method:'POST',headers:{'Content-Type':'application/json','X-Requested-With':'XMLHttpRequest'},body:'$body'}).then(r=>r.status)"
"login status: $status"
$null = Invoke-Cdp $ws 'Page.navigate' @{ url = "$($mm.serverUrl)/demo/channels/ai-requests" }
Start-Sleep 6
$title = Invoke-Js $ws 'document.title'
"page: $title"
$win = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profile*" -and $_.CommandLine -notlike '*--type=*' } | ForEach-Object { Get-Process -Id $_.ProcessId } | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($win) { [B2]::SetWindowPos($win.MainWindowHandle, [IntPtr]::Zero, 0, 0, 880, 1032, 0x0040) | Out-Null; "window placed" }
