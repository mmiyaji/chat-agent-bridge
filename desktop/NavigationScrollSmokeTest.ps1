param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $AppPid -ErrorAction Stop
if ($process.ProcessName -ne 'ChatAgentBridge.Desktop') { throw "PID $AppPid is not ChatAgentBridge.Desktop." }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ScrollSmokeNative {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@

$script:uiCommandCount = 0
function Invoke-UI([string[]]$Arguments, [int]$TimeoutMs = 8000) {
    $script:uiCommandCount++
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command winapp -ErrorAction Stop).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    [void]$start.ArgumentList.Add('-a'); [void]$start.ArgumentList.Add("$AppPid")
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $start
    [void]$child.Start()
    $stdoutTask = $child.StandardOutput.ReadToEndAsync(); $stderrTask = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit($TimeoutMs)) { $child.Kill($true); throw "winapp ui timed out: $($Arguments -join ' ')" }
    $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw "winapp ui exit $($child.ExitCode): $stderr $stdout" }
    return $stdout.Trim()
}
function Wait-Element([string]$Id, [int]$TimeoutMs = 5000) {
    [void](Invoke-UI @('ui','wait-for',$Id,'-t',"$TimeoutMs") ($TimeoutMs + 1000))
}
function Get-MainWindow {
    $current = Get-Process -Id $AppPid -ErrorAction Stop
    if ($current.MainWindowHandle -ne [IntPtr]::Zero) { return [IntPtr]$current.MainWindowHandle }
    $windows = Invoke-UI @('ui','list-windows','--json') | ConvertFrom-Json
    $entry = @($windows | Where-Object { $_.title -ne 'PopupHost' } | Select-Object -First 1)
    if (-not $entry.Count -or -not $entry[0].hwnd) { throw 'Could not locate the app main window.' }
    return [IntPtr][long]$entry[0].hwnd
}
function Get-ScrollPattern {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:windowHandle)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MainContentScroll')
    $viewer = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $viewer) { throw 'MainContentScroll was not found by UI Automation.' }
    try { return $viewer.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) }
    catch { throw "MainContentScroll does not expose ScrollPattern: $($_.Exception.Message)" }
}
function Set-CurrentPageToBottom([string]$PageName) {
    $pattern = Get-ScrollPattern
    if ($pattern.Current.VerticallyScrollable -ne $true) { throw "$PageName is not vertically scrollable at the current window size; cannot verify a non-zero departure offset." }
    $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100.0)
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $percent = (Get-ScrollPattern).Current.VerticalScrollPercent
    } while ($percent -lt 1.0 -and [DateTime]::UtcNow -lt $deadline)
    if ($percent -lt 1.0) { throw "$PageName did not scroll away from the top (VerticalScrollPercent=$percent)." }
    return [double]$percent
}
function Assert-Top([string]$DestinationName) {
    $pattern = Get-ScrollPattern
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 100
        $percent = (Get-ScrollPattern).Current.VerticalScrollPercent
    } while ($percent -gt 0.1 -and [DateTime]::UtcNow -lt $deadline)
    if ($percent -gt 0.1) { throw "$DestinationName retained scroll offset $percent%; expected 0%." }
    return [double]$percent
}
function Set-WindowSize([int]$Width, [int]$Height) {
    $dpi = [ScrollSmokeNative]::GetDpiForWindow($script:windowHandle)
    $scale = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
    if (-not [ScrollSmokeNative]::SetWindowPos($script:windowHandle, [IntPtr]::Zero, 40, 40,
        [int]($Width * $scale), [int]($Height * $scale), 0x0010)) { throw "SetWindowPos failed for ${Width}x${Height}." }
    Start-Sleep -Milliseconds 250
}
function Go-Page([string]$NavId, [string]$Anchor) {
    [void](Invoke-UI @('ui','invoke',$NavId))
    Wait-Element $Anchor
    Start-Sleep -Milliseconds 150
}
function Test-Transition([string]$FromName, [string]$ToName, [string]$ToNav, [string]$ToAnchor, [System.Collections.Generic.List[object]]$Records) {
    $departure = Set-CurrentPageToBottom $FromName
    Go-Page $ToNav $ToAnchor
    $arrival = Assert-Top $ToName
    $Records.Add(@{ viewport=$script:currentViewport; from=$FromName; to=$ToName; departurePercent=$departure; arrivalPercent=$arrival; result='PASS' })
}
function Test-ReturnFromOverlay([string]$OverlayName, [string]$OverlayNav, [string]$OverlayAnchor, [System.Collections.Generic.List[object]]$Records) {
    $departure = Set-CurrentPageToBottom 'overview'
    Go-Page $OverlayNav $OverlayAnchor
    Go-Page $targets.overview.nav $targets.overview.anchor
    $destinationName = "overview (return from $OverlayName)"
    $arrival = Assert-Top $destinationName
    $Records.Add(@{ viewport=$script:currentViewport; from='overview'; via=$OverlayName; to='overview'; departurePercent=$departure; arrivalPercent=$arrival; result='PASS' })
}
function Test-SamePageRetainsOffset([System.Collections.Generic.List[object]]$Records) {
    $departure = Set-CurrentPageToBottom 'overview'
    Go-Page $targets.overview.nav $targets.overview.anchor
    $percent = (Get-ScrollPattern).Current.VerticalScrollPercent
    if ($percent -lt 1.0) { throw "Selecting the current page unexpectedly reset its scroll position (was $departure%, now $percent%)." }
    $Records.Add(@{ viewport=$script:currentViewport; from='overview'; to='overview'; departurePercent=$departure; finalPercent=$percent; result='PASS' })
}

$targets = @{
    overview = @{ nav='NavOverview'; anchor='SetupCompletionSummary' }
    workbench = @{ nav='NavWorkbench'; anchor='Prompt' }
    agent = @{ nav='NavAgentSettings'; anchor='AgentProvider' }
    connections = @{ nav='NavConnections'; anchor='MattermostServerUrl' }
    chat = @{ nav='NavChat'; anchor='ConnectMattermost' }
    history = @{ nav='NavHistory'; anchor='HistoryStatus' }
}
$records = [Collections.Generic.List[object]]::new()
$runDirectory = Join-Path $PSScriptRoot 'artifacts'
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$resultPath = Join-Path $runDirectory "scroll-smoke-$runId.json"
$screenshots = [Collections.Generic.List[string]]::new()
$script:windowHandle = Get-MainWindow
$testError = $null

try {
    [void](Invoke-UI @('ui','invoke','NavOverview'))
    Wait-Element 'SetupCompletionSummary'
    if ($DataDirectory) { [void](Invoke-UI @('ui','wait-for','SettingsLocation','--value',[IO.Path]::GetFullPath($DataDirectory),'-t','5000') 6000) }
    foreach ($size in @(@(720,600), @(1160,800))) {
        $script:currentViewport = "$($size[0])x$($size[1])"
        Set-WindowSize $size[0] $size[1]
        Go-Page $targets.workbench.nav $targets.workbench.anchor
        $testPrompt = ("Scroll regression sample — 日本語の長い入力🙂`r`n" * 80) + "width=$($size[0])"
        [void](Invoke-UI @('ui','set-value','Prompt',$testPrompt))
        Go-Page $targets.overview.nav $targets.overview.anchor
        Test-SamePageRetainsOffset $records
        Test-Transition 'overview' 'agent' $targets.agent.nav $targets.agent.anchor $records
        $agentShot = Join-Path $runDirectory "scroll-smoke-$runId-agent-top-$($size[0])x$($size[1]).png"
        [void](Invoke-UI @('ui','screenshot','-o',$agentShot) 12000)
        $screenshots.Add($agentShot)
        Test-Transition 'agent' 'connections' $targets.connections.nav $targets.connections.anchor $records
        Test-Transition 'connections' 'workbench' $targets.workbench.nav $targets.workbench.anchor $records
        $workbenchScroll = Get-ScrollPattern
        if ($workbenchScroll.Current.VerticallyScrollable) {
            Test-Transition 'workbench' 'overview' $targets.overview.nav $targets.overview.anchor $records
        }
        else {
            Go-Page $targets.overview.nav $targets.overview.anchor
            $arrival = Assert-Top 'overview (from non-scrollable workbench)'
            $records.Add(@{ viewport=$script:currentViewport; from='workbench'; to='overview'; departurePercent=$null; arrivalPercent=$arrival; result='NOT_APPLICABLE'; reason='Workbench has no outer vertical scroll range at this viewport; its top arrival was still checked.' })
        }
        Test-ReturnFromOverlay 'history' $targets.history.nav $targets.history.anchor $records
        Test-ReturnFromOverlay 'chat' $targets.chat.nav $targets.chat.anchor $records
    }
    Go-Page $targets.overview.nav $targets.overview.anchor
}
catch {
    $testError = $_.Exception.Message
    throw
}
finally {
    $summary = @{ appPid=$AppPid; dataDirectory=$DataDirectory; uiCommandCount=$script:uiCommandCount; transitions=$records; screenshots=$screenshots; error=$testError }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "Scroll smoke results: $resultPath"
}
