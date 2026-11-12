# Runs on the test PC (STAMPEDE) inside the logged-on user's desktop, started by the CabUiSmoke scheduled task.
# Reads the run id from request.txt, checks out the pushed ref, builds the portable app, runs SmokeTest.ps1
# against an isolated data directory and writes status/results/screenshot into out\.
param([string]$Root = 'C:\cab-ci')
$ErrorActionPreference = 'Stop'
$out = Join-Path $Root 'out'
$work = Join-Path $Root 'work'
$bare = Join-Path $Root 'repo.git'
$request = (Get-Content -LiteralPath (Join-Path $Root 'request.txt') -Raw).Trim() -split '\s+'
$runId = $request[0]
# "<runId> probe" runs C:\cab-ci\probe.ps1 (param $AppPid, $DataDirectory, $OutDirectory) instead of SmokeTest.ps1,
# for inspecting the live UI from the dev PC.
$probe = $request.Count -gt 1 -and $request[1] -eq 'probe'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
function Set-Status([string]$state) { Set-Content -LiteralPath (Join-Path $out 'status.txt') -Value "$runId $state" -Encoding ascii }
Set-Status 'running'
Start-Transcript -LiteralPath (Join-Path $out 'run.log') | Out-Null
$app = $null
try {
    if (-not (Test-Path $work)) { git clone --quiet $bare $work }
    git -C $work fetch --quiet --force $bare 'refs/heads/ui-test'
    git -C $work checkout --quiet --force FETCH_HEAD
    git -C $work clean -fdxq -e 'desktop/**/bin/' -e 'desktop/**/obj/'
    "commit: $(git -C $work rev-parse HEAD)"

    $desktop = Join-Path $work 'desktop'
    dotnet build (Join-Path $desktop 'ChatAgentBridge.Desktop\ChatAgentBridge.Desktop.csproj') -p:CABPortable=true -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $exe = Get-ChildItem (Join-Path $desktop 'ChatAgentBridge.Desktop\bin\AnyCPU\Debug\portable') -Recurse -Filter 'ChatAgentBridge.Desktop.exe' |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $exe) { throw 'Built exe not found.' }

    $data = Join-Path $Root "data-$runId"
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    # Mark the fresh directory as SmokeTest's isolated one before the wizard deferral writes settings.json.
    Set-Content -LiteralPath (Join-Path $data '.cab-ui-smoke') -Value 'Isolated UI smoke test directory' -Encoding utf8
    # Mark the one-time "connect a chat" teaching tip as seen: its popup and OK button (also named CloseButton)
    # would otherwise capture the test's dialog commands.
    Set-Content -LiteralPath (Join-Path $data 'tips.json') -Value '{ "Seen": [ "chat-connect" ] }' -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $exe
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
    $app = [Diagnostics.Process]::Start($start)
    Start-Sleep -Seconds 8
    # A fresh data directory opens the first-run wizard; defer it so the main navigation is reachable.
    & winapp ui wait-for OnboardingDefer -t 15000 -a $app.Id
    if ($LASTEXITCODE -eq 0) {
        & winapp ui invoke OnboardingDefer -a $app.Id
        & winapp ui wait-for NavOverview -t 15000 -a $app.Id
        if ($LASTEXITCODE -ne 0) { throw 'Main navigation did not appear after deferring the first-run wizard.' }
    }

    # Pipe through Tee so winapp's own error text (native output) lands in smoke.log.
    $script = if ($probe) { Join-Path $Root 'probe.ps1' } else { Join-Path $desktop 'SmokeTest.ps1' }
    $extra = if ($probe) { @{ OutDirectory = $out } } else { @{} }
    & $script -AppPid $app.Id -DataDirectory $data @extra *>&1 |
        Tee-Object -FilePath (Join-Path $out 'smoke.log')
    $passed = $LASTEXITCODE -eq 0
    Copy-Item (Join-Path $desktop 'artifacts\smoke-results.json') $out -ErrorAction SilentlyContinue
    if (-not $passed) { throw 'Smoke test reported failures.' }
    Set-Status 'passed'
}
catch {
    "ERROR: $($_.Exception.Message)"
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = [Drawing.Bitmap]::new($b.Width, $b.Height)
        [Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $out 'failure.png'))
    } catch { "screenshot failed: $($_.Exception.Message)" }
    Set-Status 'failed'
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill($true) }
    Stop-Transcript | Out-Null
}
