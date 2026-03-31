param(
  [switch]$SkipBuild,
  [switch]$OpenAdmin,
  [string]$Actor = "service-script",
  [string]$Reason = "manual-start"
)

$ErrorActionPreference = "Stop"

$commonScript = Join-Path $PSScriptRoot "bridge-common.ps1"
. $commonScript

$repoRoot = $script:BridgeRepoRoot
$runtimeDir = $script:BridgeRuntimeDir
$pidFile = $script:BridgePidFile
$stdoutLog = Join-Path $runtimeDir "bridge.stdout.log"
$stderrLog = Join-Path $runtimeDir "bridge.stderr.log"

function Test-BridgeOnboardingRequired {
  $chatProvider = (Get-Item "Env:CAB_CHAT_PROVIDER" -ErrorAction SilentlyContinue).Value
  if ([string]::IsNullOrWhiteSpace($chatProvider)) {
    $chatProvider = "slack"
  }

  $defaultWorkdir = (Get-Item "Env:CAB_DEFAULT_WORKDIR" -ErrorAction SilentlyContinue).Value
  if ([string]::IsNullOrWhiteSpace($defaultWorkdir)) {
    return $true
  }

  if ($chatProvider -eq "mattermost") {
    $serverUrl = (Get-Item "Env:MATTERMOST_SERVER_URL" -ErrorAction SilentlyContinue).Value
    $botToken = (Get-Item "Env:MATTERMOST_BOT_TOKEN" -ErrorAction SilentlyContinue).Value
    $mode = (Get-Item "Env:MATTERMOST_MODE" -ErrorAction SilentlyContinue).Value
    if ([string]::IsNullOrWhiteSpace($mode)) {
      $mode = "http"
    }
    if ([string]::IsNullOrWhiteSpace($serverUrl) -or [string]::IsNullOrWhiteSpace($botToken)) {
      return $true
    }
    if ($mode -eq "http") {
      $webhookToken = (Get-Item "Env:MATTERMOST_WEBHOOK_TOKEN" -ErrorAction SilentlyContinue).Value
      if ([string]::IsNullOrWhiteSpace($webhookToken)) {
        return $true
      }
    }
    return $false
  }

  $slackBotToken = (Get-Item "Env:SLACK_BOT_TOKEN" -ErrorAction SilentlyContinue).Value
  $slackMode = (Get-Item "Env:CAB_SLACK_MODE" -ErrorAction SilentlyContinue).Value
  if ([string]::IsNullOrWhiteSpace($slackMode)) {
    $slackMode = "http"
  }
  if ([string]::IsNullOrWhiteSpace($slackBotToken)) {
    return $true
  }
  if ($slackMode -eq "socket") {
    $slackAppToken = (Get-Item "Env:SLACK_APP_TOKEN" -ErrorAction SilentlyContinue).Value
    return [string]::IsNullOrWhiteSpace($slackAppToken)
  }
  return $false
}

function Open-BridgeAdminPage {
  $adminHost = (Get-Item "Env:CAB_ADMIN_HOST" -ErrorAction SilentlyContinue).Value
  if ([string]::IsNullOrWhiteSpace($adminHost)) {
    $adminHost = (Get-Item "Env:CAB_HOST" -ErrorAction SilentlyContinue).Value
  }
  if ([string]::IsNullOrWhiteSpace($adminHost) -or $adminHost -eq "0.0.0.0") {
    $adminHost = "127.0.0.1"
  }

  $adminPort = (Get-Item "Env:CAB_ADMIN_PORT" -ErrorAction SilentlyContinue).Value
  if ([string]::IsNullOrWhiteSpace($adminPort)) {
    $adminPort = "8788"
  }

  $path = if (Test-BridgeOnboardingRequired) { "/admin/onboarding" } else { "/admin" }
  $url = "http://{0}:{1}{2}" -f $adminHost, $adminPort, $path
  Start-Process $url | Out-Null
}

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
Write-BridgeAuditEvent -Type "service.start.requested" -Actor $Actor -Reason $Reason -Fields @{
  skipBuild = $SkipBuild.IsPresent
  openAdmin = $OpenAdmin.IsPresent
}

$requiredOrOptionalEnvNames = @(
  "CAB_ALLOWED_DIRS",
  "CAB_REPO_ALIASES",
  "CAB_DEFAULT_WORKDIR",
  "CAB_HOST",
  "CAB_PORT",
  "CAB_STATE_FILE",
  "CAB_AUDIT_LOG_FILE",
  "CAB_CODEX_SESSION_INDEX_FILE",
  "CAB_CODEX_MODEL",
  "CAB_CODEX_MAX_TURNS",
  "CAB_CODEX_COMMAND_TIMEOUT_MS",
  "CAB_CODEX_MAX_RUNTIME_MS",
  "CAB_CODEX_MAX_OUTPUT_LENGTH",
  "CAB_DEFAULT_APPROVAL_POLICY",
  "CAB_NETWORK_ACCESS",
  "CAB_SLACK_MODE",
  "SLACK_APP_TOKEN",
  "SLACK_BOT_TOKEN",
  "SLACK_SIGNING_SECRET"
)

Import-BridgeEnvironment -Names $requiredOrOptionalEnvNames

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_ALLOWED_DIRS" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_ALLOWED_DIRS" -Value $repoRoot
}

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_DEFAULT_WORKDIR" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_DEFAULT_WORKDIR" -Value $repoRoot
}

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_STATE_FILE" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_STATE_FILE" -Value (Join-Path $repoRoot "data\slack-run-state.json")
}

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_AUDIT_LOG_FILE" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_AUDIT_LOG_FILE" -Value (Join-Path $repoRoot "data\audit.jsonl")
}

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_HOST" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_HOST" -Value "127.0.0.1"
}

if ([string]::IsNullOrWhiteSpace((Get-Item "Env:CAB_PORT" -ErrorAction SilentlyContinue).Value)) {
  Set-Item -Path "Env:CAB_PORT" -Value "8787"
}

if (Test-Path $pidFile) {
  $existingProcessId = (Get-Content $pidFile -Raw).Trim()
  $process = if ($existingProcessId) { Get-Process -Id $existingProcessId -ErrorAction SilentlyContinue } else { $null }
  if ($process) {
    $otherBridgeProcesses = @(Get-BridgeProcesses | Where-Object { $_.ProcessId -ne [int]$existingProcessId })
    if ($otherBridgeProcesses.Count -eq 0) {
      Write-Output "Bridge is already running with PID $existingProcessId."
      Write-BridgeAuditEvent -Type "service.start.skipped" -Actor $Actor -Reason "already-running" -Fields @{
        processId = [int]$existingProcessId
      }
      if ($OpenAdmin) {
        Open-BridgeAdminPage
      }
      exit 0
    }
  }

  Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}

$staleBridgeProcesses = Get-BridgeProcesses
foreach ($bridgeProcess in $staleBridgeProcesses) {
  Stop-Process -Id $bridgeProcess.ProcessId -Force -ErrorAction SilentlyContinue
  Wait-Process -Id $bridgeProcess.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
  Write-Output "Stopped stale bridge process $($bridgeProcess.ProcessId)."
}

$staleAdminListenerProcessIds = Get-BridgeAdminListenerProcessIds
foreach ($processId in ($staleAdminListenerProcessIds | Select-Object -Unique)) {
  Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
  Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
  Write-Output "Stopped stale admin-listener process $processId."
}

Push-Location $repoRoot
try {
  $nodeCommand = (Get-Command node).Source
  if (!$SkipBuild) {
    $npmCli = Join-Path (Split-Path $nodeCommand) "node_modules\npm\bin\npm-cli.js"
    & $nodeCommand --preserve-symlinks --preserve-symlinks-main $npmCli run build | Out-Host
  }

  $process = Start-Process `
    -FilePath $nodeCommand `
    -ArgumentList "--preserve-symlinks", "--preserve-symlinks-main", "dist/index.js" `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -WindowStyle Hidden `
    -PassThru

  Set-Content -Path $pidFile -Value $process.Id -Encoding ascii
  Start-Sleep -Seconds 2

  if ($process.HasExited) {
    Write-BridgeAuditEvent -Type "service.start.failed" -Actor $Actor -Reason "process-exited-immediately" -Fields @{
      processId = $process.Id
      stderrLog = $stderrLog
    }
    throw "Bridge exited immediately. Check $stderrLog"
  }

  Write-Output "Bridge started with PID $($process.Id)."
  Write-BridgeAuditEvent -Type "service.start.completed" -Actor $Actor -Reason $Reason -Fields @{
    processId = $process.Id
    skipBuild = $SkipBuild.IsPresent
    openAdmin = $OpenAdmin.IsPresent
  }
  Write-Output "stdout: $stdoutLog"
  Write-Output "stderr: $stderrLog"
  if ($OpenAdmin) {
    Open-BridgeAdminPage
  }
} finally {
  Pop-Location
}
