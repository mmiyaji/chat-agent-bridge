param(
  [string]$ChannelId = "",
  [string]$ThreadId = "",
  [switch]$Build
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$orchestratorLog = Join-Path $runtimeDir "bridge.orchestrator.log"
$stopScript = if ($env:CAB_BRIDGE_STOP_SCRIPT) { $env:CAB_BRIDGE_STOP_SCRIPT } else { Join-Path $PSScriptRoot "bridge-stop.ps1" }
$startScript = if ($env:CAB_BRIDGE_START_SCRIPT) { $env:CAB_BRIDGE_START_SCRIPT } else { Join-Path $PSScriptRoot "bridge-start.ps1" }
$systemRoot = if ($env:SystemRoot) { $env:SystemRoot } else { "C:\Windows" }
$powershellPath = Join-Path $systemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$settingsFile = if ($env:CAB_SETTINGS_FILE) { $env:CAB_SETTINGS_FILE } else { Join-Path $repoRoot "data\admin-settings.json" }
$slackApiBaseUrl = if ($env:CAB_SLACK_API_BASE_URL) { $env:CAB_SLACK_API_BASE_URL.TrimEnd("/") } else { "https://slack.com" }

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

"[$([DateTime]::UtcNow.ToString('o'))] detached restart helper started build=$($Build.IsPresent) channel=$ChannelId thread=$ThreadId" | Add-Content -Path $orchestratorLog -Encoding utf8

foreach ($name in @("SLACK_BOT_TOKEN")) {
  if ([string]::IsNullOrWhiteSpace((Get-Item "Env:$name" -ErrorAction SilentlyContinue).Value)) {
    $userValue = [Environment]::GetEnvironmentVariable($name, "User")
    if (![string]::IsNullOrWhiteSpace($userValue)) {
      Set-Item -Path "Env:$name" -Value $userValue
    }
  }
}

function Read-SettingsValue {
  param(
    [Parameter(Mandatory = $true)][string]$Key
  )

  if (!(Test-Path $settingsFile)) {
    return $null
  }

  try {
    $raw = Get-Content $settingsFile -Raw | ConvertFrom-Json
    return $raw.$Key
  } catch {
    return $null
  }
}

function Wait-AdminReady {
  $port = if ($env:CAB_PORT) {
    [int]$env:CAB_PORT
  } elseif (Read-SettingsValue "port") {
    [int](Read-SettingsValue "port")
  } else {
    8787
  }

  $adminPort = if ($env:CAB_ADMIN_PORT) {
    [int]$env:CAB_ADMIN_PORT
  } elseif (Read-SettingsValue "adminPort") {
    [int](Read-SettingsValue "adminPort")
  } else {
    $port + 1
  }

  $uri = "http://127.0.0.1:$adminPort/admin/healthz"
  for ($attempt = 1; $attempt -le 30; $attempt++) {
    Start-Sleep -Seconds 1
    try {
      $response = Invoke-WebRequest -UseBasicParsing $uri
      if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
        "[$([DateTime]::UtcNow.ToString('o'))] admin health ready on attempt=$attempt uri=$uri" | Add-Content -Path $orchestratorLog -Encoding utf8
        return $true
      }
    } catch {
      "[$([DateTime]::UtcNow.ToString('o'))] admin health not ready on attempt=$attempt uri=$uri error=$($_.Exception.Message)" | Add-Content -Path $orchestratorLog -Encoding utf8
    }
  }

  return $false
}

Start-Sleep -Milliseconds 500
"[$([DateTime]::UtcNow.ToString('o'))] restarting bridge build=$($Build.IsPresent)" | Add-Content -Path $orchestratorLog -Encoding utf8

$restartFailed = $false
$restartError = ""
try {
  & $powershellPath -NoProfile -ExecutionPolicy Bypass -File $stopScript *>> $orchestratorLog
  "[$([DateTime]::UtcNow.ToString('o'))] bridge stop script completed" | Add-Content -Path $orchestratorLog -Encoding utf8

if ($Build) {
  & $powershellPath -NoProfile -ExecutionPolicy Bypass -File $startScript *>> $orchestratorLog
} else {
  & $powershellPath -NoProfile -ExecutionPolicy Bypass -File $startScript -SkipBuild *>> $orchestratorLog
}
"[$([DateTime]::UtcNow.ToString('o'))] bridge start script completed build=$($Build.IsPresent)" | Add-Content -Path $orchestratorLog -Encoding utf8

$ready = $false
if (!$restartFailed) {
  $ready = Wait-AdminReady
  if (!$ready) {
    $restartFailed = $true
    $restartError = "bridge restart completed but admin health did not become ready in time"
    "[$([DateTime]::UtcNow.ToString('o'))] bridge readiness wait failed: $restartError" | Add-Content -Path $orchestratorLog -Encoding utf8
  }
}
} catch {
  $restartFailed = $true
  $restartError = $_.Exception.Message
  "[$([DateTime]::UtcNow.ToString('o'))] bridge restart failed: $restartError" | Add-Content -Path $orchestratorLog -Encoding utf8
}

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
    $slackText = if ($restartFailed) {
      "Bridge restart failed: $restartError"
    } else {
      "Bridge restart completed. The bridge is back online."
    }
    $body = @{
      channel = $ChannelId
      thread_ts = $ThreadId
      text = $slackText
    } | ConvertTo-Json -Compress

    Invoke-RestMethod `
      -Method Post `
      -Uri "$slackApiBaseUrl/api/chat.postMessage" `
      -Headers $headers `
      -Body $body | Out-Null
    "[$([DateTime]::UtcNow.ToString('o'))] posted restart $(if ($restartFailed) { 'failure' } else { 'completion' }) to Slack channel=$ChannelId thread=$ThreadId" | Add-Content -Path $orchestratorLog -Encoding utf8
  } catch {
    "[$([DateTime]::UtcNow.ToString('o'))] failed to post restart status to Slack: $($_.Exception.Message)" | Add-Content -Path $orchestratorLog -Encoding utf8
  }
} elseif (![string]::IsNullOrWhiteSpace($ChannelId) -and ![string]::IsNullOrWhiteSpace($ThreadId)) {
  "[$([DateTime]::UtcNow.ToString('o'))] skipped restart Slack post because SLACK_BOT_TOKEN is unavailable" | Add-Content -Path $orchestratorLog -Encoding utf8
}
