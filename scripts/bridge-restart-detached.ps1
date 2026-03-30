param(
  [string]$ChannelId = "",
  [string]$ThreadId = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$orchestratorLog = Join-Path $runtimeDir "bridge.orchestrator.log"

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

Start-Sleep -Seconds 3
"[$([DateTime]::UtcNow.ToString('o'))] restarting bridge" | Add-Content -Path $orchestratorLog -Encoding utf8

& (Join-Path $PSScriptRoot "bridge-restart.ps1") *>> $orchestratorLog

if (
  ![string]::IsNullOrWhiteSpace($ChannelId) -and
  ![string]::IsNullOrWhiteSpace($ThreadId) -and
  ![string]::IsNullOrWhiteSpace($env:SLACK_BOT_TOKEN)
) {
  try {
    $headers = @{
      Authorization = "Bearer $($env:SLACK_BOT_TOKEN)"
      "Content-Type" = "application/json; charset=utf-8"
    }
    $body = @{
      channel = $ChannelId
      thread_ts = $ThreadId
      text = "Bridge restart completed. The bridge is back online."
    } | ConvertTo-Json -Compress

    Invoke-RestMethod `
      -Method Post `
      -Uri "https://slack.com/api/chat.postMessage" `
      -Headers $headers `
      -Body $body | Out-Null
  } catch {
    "[$([DateTime]::UtcNow.ToString('o'))] failed to post restart completion to Slack: $($_.Exception.Message)" | Add-Content -Path $orchestratorLog -Encoding utf8
  }
}
