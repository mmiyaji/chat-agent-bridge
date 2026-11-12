# Entry script for the remote-win-ui-test skill: runs on the test PC inside the logged-on desktop.
# Builds the portable app, launches it with an empty isolated data directory, gets past the first-run UI and
# runs SmokeTest.ps1 (or, with -Probe, an uploaded probe script) against it. Results go to $env:REMOTE_UI_OUT.
param(
    # File name of an uploaded probe script to run instead of SmokeTest.ps1.
    # It receives -AppPid, -DataDirectory and -OutDirectory.
    [string]$Probe
)
$ErrorActionPreference = 'Stop'
$out = if ($env:REMOTE_UI_OUT) { $env:REMOTE_UI_OUT } else { Join-Path $PSScriptRoot '..\artifacts\remote-ui' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$desktop = Split-Path $PSScriptRoot -Parent
$app = $null
try {
    dotnet build (Join-Path $desktop 'ChatAgentBridge.Desktop\ChatAgentBridge.Desktop.csproj') -p:CABPortable=true -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $exe = Get-ChildItem (Join-Path $desktop 'ChatAgentBridge.Desktop\bin\AnyCPU\Debug\portable') -Recurse -Filter 'ChatAgentBridge.Desktop.exe' |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $exe) { throw 'Built exe not found.' }

    $data = Join-Path ([IO.Path]::GetTempPath()) "cab-ui-smoke-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
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

    if ($Probe) {
        & (Join-Path $env:REMOTE_UI_UPLOAD $Probe) -AppPid $app.Id -DataDirectory $data -OutDirectory $out
        exit 0
    }
    & (Join-Path $desktop 'SmokeTest.ps1') -AppPid $app.Id -DataDirectory $data
    $code = $LASTEXITCODE
    Copy-Item (Join-Path $desktop 'artifacts\smoke-results.json') $out -ErrorAction SilentlyContinue
    exit $code
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill($true) }
}
