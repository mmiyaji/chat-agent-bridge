param(
  [string]$ChannelId = "",
  [string]$ThreadId = "",
  [switch]$Build,
  [string]$Actor = "service-script",
  [string]$Reason = "manual-restart"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot "data\runtime"
$orchestratorLog = Join-Path $runtimeDir "bridge.orchestrator.log"
$completionFile = if ($env:CAB_RESTART_COMPLETION_FILE) { $env:CAB_RESTART_COMPLETION_FILE } else { Join-Path $runtimeDir "restart-completion.jsonl" }
$restartScript = if ($env:CAB_BRIDGE_RESTART_SCRIPT) { $env:CAB_BRIDGE_RESTART_SCRIPT } else { Join-Path $PSScriptRoot "bridge-restart.ps1" }
$systemRoot = if ($env:SystemRoot) { $env:SystemRoot } else { "C:\Windows" }
$powershellPath = Join-Path $systemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

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

Start-Sleep -Seconds 3
"[$([DateTime]::UtcNow.ToString('o'))] restarting bridge build=$($Build.IsPresent)" | Add-Content -Path $orchestratorLog -Encoding utf8

$restartFailed = $false
$restartError = ""
$restartStartedAt = [DateTime]::UtcNow
try {
  if ($Build) {
    & $restartScript -Build -Actor $Actor -Reason $Reason
  } else {
    & $restartScript -Actor $Actor -Reason $Reason
  }
  "[$([DateTime]::UtcNow.ToString('o'))] restart script returned successfully" | Add-Content -Path $orchestratorLog -Encoding utf8
} catch {
  $restartFailed = $true
  $restartError = $_.Exception.Message
  "[$([DateTime]::UtcNow.ToString('o'))] bridge restart failed: $restartError" | Add-Content -Path $orchestratorLog -Encoding utf8
}
$restartDurationMs = [int]([DateTime]::UtcNow - $restartStartedAt).TotalMilliseconds

if ($restartFailed) {
  $completionText = "Bridge restart failed: $(if ([string]::IsNullOrWhiteSpace($restartError)) { "unknown error" } else { $restartError })"
} else {
  $completionText = "Bridge restart completed. The bridge is back online."
}

if (
  ![string]::IsNullOrWhiteSpace($ChannelId) -and
  ![string]::IsNullOrWhiteSpace($ThreadId)
) {
  try {
    $payload = @{
      channelId    = $ChannelId
      threadId     = $ThreadId
      text         = $completionText
      startedAt    = $restartStartedAt.ToString('o')
      durationMs   = $restartDurationMs
      failed       = $restartFailed
      errorMessage = if ($restartFailed) { $restartError } else { "" }
    } | ConvertTo-Json -Compress
    $completionDir = Split-Path -Parent $completionFile
    if ($completionDir) {
      New-Item -ItemType Directory -Force -Path $completionDir | Out-Null
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::AppendAllText($completionFile, $payload + [Environment]::NewLine, $utf8NoBom)
    "[$([DateTime]::UtcNow.ToString('o'))] queued restart $(if ($restartFailed) { 'failure' } else { 'completion' }) for channel=$ChannelId thread=$ThreadId" | Add-Content -Path $orchestratorLog -Encoding utf8
  } catch {
    "[$([DateTime]::UtcNow.ToString('o'))] failed to queue restart status: $($_.Exception.Message)" | Add-Content -Path $orchestratorLog -Encoding utf8
  }
}
