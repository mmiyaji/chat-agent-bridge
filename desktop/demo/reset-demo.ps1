# Returns the demo to a clean state: stops the demo CAB, clears #ai-requests, shows replies inline, recreates CAB data and relaunches.
param([ValidateSet('ja', 'en')][string]$Lang = 'ja', [ValidateSet('light', 'dark')][string]$Theme = 'light', [switch]$AutoSwitch, [switch]$SeedActivity)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'demo-env.ps1')
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$admin = (Get-Content $E2EState -Raw | ConvertFrom-Json).bridgeUser.token
$h = @{ Authorization = "Bearer $admin" }
$data = $CabData
Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.CommandLine -like "*$data*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 2

# Show thread replies inline in the channel on this local test server.
$null = Invoke-RestMethod -Method Put "$($mm.serverUrl)/api/v4/config/patch" -Headers $h -ContentType 'application/json' -Body '{"ServiceSettings":{"CollapsedThreads":"default_off"}}'
$posts = Invoke-RestMethod "$($mm.serverUrl)/api/v4/channels/$($mm.channelId)/posts?per_page=200" -Headers $h
$deleted = 0
foreach ($p in $posts.posts.PSObject.Properties.Value) {
    if ($p.user_id -in $mm.alice.id, $mm.'cab-bot'.id -and -not $p.type) {
        # Deleting a root post also removes its replies, so later replies may already be gone.
        try { $null = Invoke-RestMethod -Method Delete "$($mm.serverUrl)/api/v4/posts/$($p.id)" -Headers $h; $deleted++ } catch { }
    }
}
"deleted posts: $deleted"
& (Join-Path $root 'setup-cab-demo.ps1') -Lang $Lang -Theme $Theme -AutoSwitch:$AutoSwitch -SeedActivity:$SeedActivity
& (Join-Path $root 'launch-cab.ps1')
