# Shared locations for the demo recording scripts. Generated files stay in the ignored desktop/artifacts/demo folder.
# Override any tool path with an environment variable (CAB_DEMO_APP, CAB_DEMO_AGY, CAB_DEMO_CLAUDE, CAB_DEMO_FFMPEG, CAB_DEMO_CHROME).
$desktopRoot = Split-Path $PSScriptRoot -Parent
$DemoData = Join-Path $desktopRoot 'artifacts\demo'
New-Item -ItemType Directory $DemoData -Force | Out-Null
$CabData = Join-Path $DemoData 'cab-data'
$MattermostState = Join-Path $DemoData 'mattermost-demo.json'
# Admin state written by the local Mattermost E2E setup (docker-compose.mattermost-e2e.yml).
$E2EState = Join-Path $desktopRoot 'artifacts\mattermost-e2e.json'

function Resolve-DemoTool([string]$Override, [string]$Command, [string[]]$Fallbacks) {
    if ($Override) { return $Override }
    $found = Get-Command $Command -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    $Fallbacks | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
$CabExe = if ($env:CAB_DEMO_APP) { $env:CAB_DEMO_APP } else { Join-Path $desktopRoot 'artifacts\demo-app\ChatAgentBridge.Desktop.exe' }
$AgyExe = Resolve-DemoTool $env:CAB_DEMO_AGY 'agy' @("$env:LOCALAPPDATA\agy\bin\agy.exe")
$ClaudeExe = Resolve-DemoTool $env:CAB_DEMO_CLAUDE 'claude' @("$env:USERPROFILE\.local\bin\claude.exe")
$FfmpegExe = Resolve-DemoTool $env:CAB_DEMO_FFMPEG 'ffmpeg' @()
$ChromeExe = Resolve-DemoTool $env:CAB_DEMO_CHROME 'chrome' @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
