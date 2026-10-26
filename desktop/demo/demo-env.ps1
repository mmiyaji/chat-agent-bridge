# Shared locations for the demo recording scripts. Generated files stay in the ignored desktop/artifacts/demo folder.
# Override any tool path with an environment variable (CAB_DEMO_APP, CAB_DEMO_AGY, CAB_DEMO_CLAUDE, CAB_DEMO_FFMPEG, CAB_DEMO_CHROME).
$desktopRoot = Split-Path $PSScriptRoot -Parent
$DemoData = Join-Path $desktopRoot 'artifacts\demo'
New-Item -ItemType Directory $DemoData -Force | Out-Null
$CabData = Join-Path $DemoData 'cab-data'
# Sample projects the agent works in. Their paths appear in chat and dialogs, so set CAB_DEMO_SAMPLES to a short
# neutral folder (for example C:\demo) before recording.
$SampleRoot = if ($env:CAB_DEMO_SAMPLES) { $env:CAB_DEMO_SAMPLES } else { Join-Path $DemoData 'samples' }
$MattermostState = Join-Path $DemoData 'mattermost-demo.json'
# Admin state written by the local Mattermost E2E setup (docker-compose.mattermost-e2e.yml).
$E2EState = Join-Path $desktopRoot 'artifacts\mattermost-e2e.json'

function Resolve-DemoTool([string]$Override, [string]$Command, [string[]]$Fallbacks) {
    if ($Override) { return $Override }
    $found = Get-Command $Command -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    $Fallbacks | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
# Dialogs and logs show the CLI path. Set CAB_DEMO_TOOLS to a short neutral folder (for example C:\demo\tools) and each
# CLI is exposed through a directory junction there, so a user profile path never appears on camera.
function Expose-DemoTool([string]$Exe, [string]$Name) {
    if (-not $Exe -or -not $env:CAB_DEMO_TOOLS) { return $Exe }
    New-Item -ItemType Directory $env:CAB_DEMO_TOOLS -Force | Out-Null
    $link = Join-Path $env:CAB_DEMO_TOOLS $Name
    if (-not (Test-Path $link)) { New-Item -ItemType Junction -Path $link -Target (Split-Path $Exe -Parent) | Out-Null }
    Join-Path $link (Split-Path $Exe -Leaf)
}
$CabExe = if ($env:CAB_DEMO_APP) { $env:CAB_DEMO_APP } else { Join-Path $desktopRoot 'artifacts\demo-app\ChatAgentBridge.Desktop.exe' }
$AgyExe = Resolve-DemoTool $env:CAB_DEMO_AGY 'agy' @("$env:LOCALAPPDATA\agy\bin\agy.exe")
$ClaudeExe = Resolve-DemoTool $env:CAB_DEMO_CLAUDE 'claude' @("$env:USERPROFILE\.local\bin\claude.exe")
$AgyExe = Expose-DemoTool $AgyExe 'agy'
$ClaudeExe = Expose-DemoTool $ClaudeExe 'claude'
$FfmpegExe = Resolve-DemoTool $env:CAB_DEMO_FFMPEG 'ffmpeg' @()
$ChromeExe = Resolve-DemoTool $env:CAB_DEMO_CHROME 'chrome' @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
