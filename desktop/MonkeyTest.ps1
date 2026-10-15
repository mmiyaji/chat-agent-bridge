param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [int[]]$Seeds = @(18743, 90211),
    [ValidateRange(20, 250)][int]$ActionsPerSeed = 120
)
$ErrorActionPreference = 'Stop'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$process = Get-Process -Id $AppPid -ErrorAction Stop
if ($process.ProcessName -ne 'ChatAgentBridge.Desktop') { throw "PID $AppPid is not ChatAgentBridge.Desktop." }
if ($Seeds.Count -ne 2) { throw 'Provide exactly two deterministic seeds.' }
$dataMarker = Join-Path $DataDirectory '.cab-monkey-test'
if (-not (Test-Path -LiteralPath $DataDirectory)) { [IO.Directory]::CreateDirectory($DataDirectory) | Out-Null }
if (-not (Test-Path -LiteralPath $dataMarker)) {
    foreach ($name in @('settings.json', 'history.json', 'sessions.json', 'audit.json')) {
        if (Test-Path -LiteralPath (Join-Path $DataDirectory $name)) { throw 'Monkey test requires a fresh, isolated --data-dir on first use.' }
    }
    Set-Content -LiteralPath $dataMarker -Value 'Dedicated isolated UI monkey test directory' -Encoding utf8
}
elseif ((Get-Content -LiteralPath $dataMarker -Raw).Trim() -ne 'Dedicated isolated UI monkey test directory') {
    throw 'Data directory marker does not belong to this monkey test.'
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
$runDirectory = Join-Path $artifactDirectory ("monkey-{0}" -f [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$resultsPath = Join-Path $runDirectory 'results.json'
$logPath = Join-Path $runDirectory 'actions.json'
$actions = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[object]]::new()
$coverage = @{}
$coverageBySeed = @{}
$script:currentCoverage = $null
$script:uiCommandCount = 0
$currentSeed = 0
$currentIndex = 0
$windowHwnd = 0L

function Invoke-UI([string[]]$Arguments, [int]$TimeoutMs = 8000) {
    $script:uiCommandCount++
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command winapp -ErrorAction Stop).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    [void]$start.ArgumentList.Add('-a'); [void]$start.ArgumentList.Add("$AppPid")
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    [void]$child.Start()
    $stdoutTask = $child.StandardOutput.ReadToEndAsync()
    $stderrTask = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit($TimeoutMs)) {
        $child.Kill($true)
        throw "winapp ui timed out after ${TimeoutMs}ms: $($Arguments -join ' ')"
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw "winapp ui exit $($child.ExitCode): $($Arguments -join ' ') $stderr $stdout" }
    return $stdout.Trim()
}
function Wait-UI([string]$Id, [string]$Value = '', [int]$TimeoutMs = 5000, [switch]$Contains, [string]$Property = '') {
    $args = @('ui', 'wait-for', $Id)
    if ($Property) { $args += @('-p', $Property) }
    if ($Value -ne '') { $args += @('--value', $Value); if ($Contains) { $args += '--contains' } }
    $args += @('-t', "$TimeoutMs")
    [void](Invoke-UI $args ($TimeoutMs + 1000))
}
function Open-Page([string]$NavId, [string]$Anchor) {
    [void](Invoke-UI @('ui', 'invoke', $NavId))
    Wait-UI $Anchor
    if ($script:currentCoverage) {
        $page = @($targets | Where-Object nav -eq $NavId | Select-Object -First 1)
        if ($page.Count) { $name = $page[0].name; $script:currentCoverage.pages[$name] = 1 + [int]$script:currentCoverage.pages[$name] }
    }
}
function Go-Overview { Open-Page 'NavOverview' 'SetupCompletionSummary' }
function Get-SavedProgress {
    $path = Join-Path $DataDirectory 'settings.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return 0 }
    $saved = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $agentReady = -not [string]::IsNullOrWhiteSpace($saved.AgentExecutable) -and
        -not [string]::IsNullOrWhiteSpace($saved.WorkspaceRoot) -and (Test-Path -LiteralPath $saved.WorkspaceRoot -PathType Container)
    $mattermostReady = -not [string]::IsNullOrWhiteSpace($saved.MattermostServerUrl) -and
        -not [string]::IsNullOrWhiteSpace($saved.MattermostChannelId) -and -not [string]::IsNullOrWhiteSpace($saved.MattermostToken)
    $slackReady = -not [string]::IsNullOrWhiteSpace($saved.SlackBotToken) -and -not [string]::IsNullOrWhiteSpace($saved.SlackAppToken)
    return [int]$agentReady + [int]($mattermostReady -or $slackReady)
}
function Assert-SavedProgress {
    Go-Overview
    $expected = Get-SavedProgress
    Wait-UI 'SetupCompletionSummary' "$expected / 3 完了"
    $temporary = @(Get-ChildItem -LiteralPath $DataDirectory -Filter 'settings.*.tmp' -File -ErrorAction SilentlyContinue)
    if ($temporary.Count) { throw 'A partial settings temp file was left after save/reload.' }
}
function Resize-App([int]$Width, [int]$Height) {
    $dpi = [MonkeyNative]::GetDpiForWindow([IntPtr]$windowHwnd)
    $scale = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
    if (-not [MonkeyNative]::SetWindowPos([IntPtr]$windowHwnd, [IntPtr]::Zero, 40, 40,
            [int]($Width * $scale), [int]($Height * $scale), 0x0010)) { throw 'SetWindowPos failed.' }
}
function Record-Action([int]$Seed, [int]$Index, [string]$Kind, [string]$Detail, [string]$Status, [string]$Error = '') {
    $entry = @{ seed = $Seed; index = $Index; kind = $Kind; detail = $Detail; status = $Status }
    if ($Error) { $entry.error = $Error }
    $actions.Add($entry)
    $actions | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $logPath -Encoding utf8
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MonkeyNative {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@

# Confirm this PID reads exactly the caller-provided test directory before any input changes.
[void](Invoke-UI @('ui', 'invoke', 'NavOverview'))
Wait-UI 'SettingsLocation' $DataDirectory
$windowList = Invoke-UI @('ui', 'list-windows', '--json') | ConvertFrom-Json
$mainWindow = @($windowList | Where-Object { $_.title -ne 'PopupHost' } | Select-Object -First 1)
if ($mainWindow.Count -ne 1 -or -not $mainWindow[0].hwnd) { throw 'Could not identify the app main window for resize coverage.' }
$windowHwnd = [long]$mainWindow[0].hwnd

$targets = @(
    @{ nav='NavOverview'; anchor='SetupCompletionSummary'; name='overview' },
    @{ nav='NavWorkbench'; anchor='Prompt'; name='workbench' },
    @{ nav='NavAgentSettings'; anchor='AgentProvider'; name='agent' },
    @{ nav='NavConnections'; anchor='MattermostServerUrl'; name='connections' },
    @{ nav='NavChat'; anchor='ConnectMattermost'; name='chat' },
    @{ nav='NavHistory'; anchor='HistoryStatus'; name='history' }
)
$workspaceChoices = @($DataDirectory, '', (Join-Path $DataDirectory 'mock-雪-😀'))
[IO.Directory]::CreateDirectory($workspaceChoices[2]) | Out-Null
$providerChoices = @(@('codex','ProviderOptionCodex'), @('claude','ProviderOptionClaude'), @('custom','ProviderOptionCustom'))

try {
    foreach ($seed in $Seeds) {
        $currentSeed = $seed
        if ((Get-SavedProgress) -ne 0 -or (Test-Path -LiteralPath (Join-Path $DataDirectory 'settings.json'))) {
            Remove-Item -LiteralPath (Join-Path $DataDirectory 'settings.json') -Force -ErrorAction SilentlyContinue
            Go-Overview
            [void](Invoke-UI @('ui', 'invoke', 'SetupReloadSettings'))
            Wait-UI 'SetupCompletionSummary' '0 / 3 完了'
        }
        $rng = [Random]::new($seed)
        $script:currentCoverage = @{ pages = @{ overview=0; workbench=0; agent=0; connections=0; chat=0; history=0 }; categories=@{} }
        $pageCounts = @{}
        foreach ($target in $targets) { Open-Page $target.nav $target.anchor }
        $categoryOrder = @()
        $resizes = @(@(720,600), @(1160,800))
        for ($i = 1; $i -le $ActionsPerSeed; $i++) {
            $currentIndex = $i
            if ((($i - 1) % 10) -eq 0) { $categoryOrder = @(0..9 | Sort-Object { $rng.Next() }) }
            $category = $categoryOrder[($i - 1) % 10]
            $status = 'PASS'
            $detail = ''
            try {
                if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'The app process exited.' }
                switch ($category) {
                    0 {
                        $target = if ($i -le $targets.Count) { $targets[$i-1] } else { $targets[$rng.Next($targets.Count)] }
                        Open-Page $target.nav $target.anchor
                        $detail = $target.name
                        $pageCounts[$target.name] = 1 + [int]$pageCounts[$target.name]
                        $coverage.nav = 1 + [int]$coverage.nav
                    }
                    1 {
                        Go-Overview
                        $links = @(@('SetupAgentLink','NavAgentSettings','AgentProvider'), @('SetupConnectionsLink','NavConnections','MattermostServerUrl'), @('SetupChatLink','NavChat','ConnectMattermost'))
                        $link = $links[$rng.Next($links.Count)]
                        [void](Invoke-UI @('ui','invoke',$link[0])); Wait-UI $link[2]
                        $detail = $link[0]
                        $coverage.setupLinks = 1 + [int]$coverage.setupLinks
                    }
                    2 {
                        Open-Page 'NavAgentSettings' 'AgentProvider'
                        $field = @('AgentModel','AgentExecutable','AgentArguments','WorkspaceRoot')[$rng.Next(4)]
                        if ($field -eq 'AgentModel') {
                            [void](Invoke-UI @('ui','invoke','AgentProvider'))
                            [void](Invoke-UI @('ui','invoke','ProviderOptionCodex'))
                            Wait-UI 'AgentModel' 'True' 3000 -Property 'IsEnabled'
                        }
                        elseif ($field -eq 'AgentArguments') {
                            [void](Invoke-UI @('ui','invoke','AgentProvider'))
                            [void](Invoke-UI @('ui','invoke','ProviderOptionCustom'))
                            Wait-UI 'AgentArguments' 'True' 3000 -Property 'IsEnabled'
                        }
                        $value = switch ($field) {
                            'AgentModel' { "model-雪-$seed-$i" }
                            'AgentExecutable' { "never-run-$seed-$i.exe" }
                            'AgentArguments' { "--fixture 雪-$seed-$i" }
                            'WorkspaceRoot' { $workspaceChoices[$rng.Next($workspaceChoices.Count)] }
                        }
                        [void](Invoke-UI @('ui','set-value',$field,$value))
                        Assert-SavedProgress
                        $detail = "$field (draft only)"
                        $coverage.agentDrafts = 1 + [int]$coverage.agentDrafts
                    }
                    3 {
                        Open-Page 'NavConnections' 'MattermostServerUrl'
                        $field = @('MattermostServerUrl','MattermostChannelId','MattermostToken','SlackBotToken','SlackAppToken')[$rng.Next(5)]
                        $value = switch ($field) {
                            'MattermostServerUrl' { @('', "https://example.invalid/$seed/$i", "http://localhost.invalid/雪/$i")[$rng.Next(3)] }
                            'MattermostChannelId' { @('', "channel-$seed-$i", "チャンネル-$i")[$rng.Next(3)] }
                            default { @('', "fake-$field-$seed-$i-雪")[$rng.Next(2)] }
                        }
                        [void](Invoke-UI @('ui','set-value',$field,$value))
                        Assert-SavedProgress
                        $detail = "$field (fake/draft only)"
                        $coverage.connectionDrafts = 1 + [int]$coverage.connectionDrafts
                    }
                    4 {
                        Open-Page 'NavAgentSettings' 'AgentProvider'
                        $choice = $providerChoices[$rng.Next($providerChoices.Count)]
                        [void](Invoke-UI @('ui','invoke','AgentProvider'))
                        [void](Invoke-UI @('ui','invoke',$choice[1]))
                        if ($choice[0] -eq 'custom') {
                            Wait-UI 'AgentModel' 'False' 3000 -Property 'IsEnabled'
                            Wait-UI 'AgentArguments' 'True' 3000 -Property 'IsEnabled'
                        }
                        Assert-SavedProgress
                        $detail = "provider=$($choice[0]) (draft only)"
                        $coverage.providerChoices = 1 + [int]$coverage.providerChoices
                    }
                    5 {
                        $target = if ($rng.Next(2) -eq 0) { $targets[2] } else { $targets[3] }
                        Open-Page $target.nav $target.anchor
                        $saveButton = if ($target.name -eq 'agent') { 'SaveSettings' } else { 'SaveConnections' }
                        [void](Invoke-UI @('ui','invoke',$saveButton))
                        Wait-UI 'Title' '設定を保存しました' 5000 -Contains
                        Assert-SavedProgress
                        $saved = Get-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Raw | ConvertFrom-Json
                        if ($null -eq $saved.SchemaVersion) { throw 'Saved settings JSON is missing SchemaVersion.' }
                        $detail = "$target.name saved and reloaded"
                        $coverage.saves = 1 + [int]$coverage.saves
                    }
                    6 {
                        Go-Overview
                        [void](Invoke-UI @('ui','invoke','SetupReloadSettings'))
                        Assert-SavedProgress
                        $detail = 'reload and saved-state progress invariant'
                        $coverage.reloads = 1 + [int]$coverage.reloads
                    }
                    7 {
                        $size = $resizes[$rng.Next($resizes.Count)]
                        Resize-App $size[0] $size[1]
                        Wait-UI 'NavOverview' '' 4000
                        $detail = "$($size[0])x$($size[1])"
                        $coverage.resizes = 1 + [int]$coverage.resizes
                    }
                    8 {
                        $order = @($targets | Sort-Object { $rng.Next() })
                        foreach ($target in ($order | Select-Object -First 3)) { [void](Invoke-UI @('ui','invoke',$target.nav)) }
                        $target = $order[2]
                        Wait-UI $target.anchor
                        $detail = 'rapid navigation: ' + (($order | Select-Object -First 3 | ForEach-Object name) -join ',')
                        $coverage.rapidNav = 1 + [int]$coverage.rapidNav
                    }
                    9 {
                        if (($i % 20) -eq 9) {
                            Open-Page 'NavWorkbench' 'Prompt'
                            $prompt = ('長文の動作確認・雪🙂 ' * 120) + " seed=$seed action=$i"
                            [void](Invoke-UI @('ui','set-value','Prompt',$prompt))
                            Assert-SavedProgress
                            $coverage.longUnicodeDrafts = 1 + [int]$coverage.longUnicodeDrafts
                        }
                        $target = $targets[$rng.Next($targets.Count)]
                        Open-Page $target.nav $target.anchor
                        $scrollTarget = switch ($target.name) {
                            'overview' { 'SettingsLocation' }
                            'workbench' { 'AgentOutput' }
                            'agent' { 'AutoConnectProvider' }
                            'connections' { 'SlackAppToken' }
                            'chat' { 'ChatUsageSummary' }
                            'history' { 'HistoryStatus' }
                        }
                        [void](Invoke-UI @('ui','scroll-into-view',$scrollTarget))
                        $detail = "scroll $($target.name)"
                        $coverage.scrolls = 1 + [int]$coverage.scrolls
                    }
                }
                Record-Action $seed $i $category $detail $status
            }
            catch {
                $status = 'FAIL'
                $failure = @{ seed=$seed; index=$i; category=$category; detail=$detail; error=$_.Exception.Message }
                $failures.Add($failure)
                Record-Action $seed $i $category $detail $status $_.Exception.Message
                $shot = Join-Path $runDirectory "failure-seed-$seed-action-$i.png"
                try { [void](Invoke-UI @('ui','screenshot','-o',$shot) 12000); $failure.screenshot = $shot } catch { $failure.screenshotError = $_.Exception.Message }
            }
            if (($i % 20) -eq 0) { Write-Output "seed=$seed action=$i/$ActionsPerSeed failures=$($failures.Count)" }
        }
        foreach ($target in $targets) { if (-not $script:currentCoverage.pages[$target.name]) { $failures.Add(@{ seed=$seed; index=$ActionsPerSeed; category='coverage'; error="Page not exercised: $($target.name)" }) } }
        $coverageBySeed["$seed"] = $script:currentCoverage
        Resize-App 1160 800
        Go-Overview
        Assert-SavedProgress
        [void](Invoke-UI @('ui','screenshot','-o',(Join-Path $runDirectory "final-seed-$seed.png")) 12000)
        if ($seed -ne $Seeds[-1]) {
            Remove-Item -LiteralPath (Join-Path $DataDirectory 'settings.json') -Force -ErrorAction SilentlyContinue
            [void](Invoke-UI @('ui','invoke','SetupReloadSettings'))
            Wait-UI 'SetupCompletionSummary' '0 / 3 完了'
        }
    }
}
finally {
    $summary = @{
        appPid = $AppPid; dataDirectory = $DataDirectory; seeds = $Seeds; actionsPerSeed = $ActionsPerSeed
        randomizedActionCount = $actions.Count; uiCommandCount = $script:uiCommandCount; failures = $failures; coverage = $coverage; coverageBySeed=$coverageBySeed; actionLog = $logPath
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultsPath -Encoding utf8
    Write-Output "Monkey results: $resultsPath"
}
if ($failures.Count) { exit 1 }
