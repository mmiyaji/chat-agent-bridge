param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$install = (Resolve-Path -LiteralPath $AppDirectory).Path
if (-not $install.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an isolated portable publish under desktop/artifacts.'
}
$executable = Join-Path $install 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The isolated desktop test executable is missing.' }
$root = Join-Path $artifacts ('startup-failure-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.HashSet[int]]::new()
$activeProcess = $null
$passed = $false
$failure = $null

function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Write-Json([string]$Path,$Value) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
}
function Find([string]$Id,[Diagnostics.Process]$Process=$activeProcess) {
    if (-not $Process) { return $null }
    $Process.Refresh()
    if ($Process.HasExited -or $Process.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$Process.Id))
    foreach ($window in $windows) {
        $item = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
        if ($item) { return $item }
    }
    return $null
}
function Visible([string]$Id) {
    $element = Find $Id
    return $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0
}
function Wait([scriptblock]$Condition,[string]$Description,[int]$Seconds=15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($activeProcess -and $activeProcess.HasExited) { throw "App exited while waiting for $Description" }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 60
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description"
}
function Invoke-Id([string]$Id) {
    $item = Find $Id
    if (-not $item) { throw "Missing UI automation element: $Id" }
    if (-not $item.Current.IsEnabled) { throw "UI automation element is disabled: $Id" }
    $pattern = $null
    if ($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke(); return }
    throw "No invoke pattern: $Id"
}
function Write-Profile([string]$Data,[string]$Language,[string]$Theme) {
    New-Item -ItemType Directory -Force -Path $Data | Out-Null
    $settings = @{
        SchemaVersion=1; WorkspaceRoot=''; AgentExecutable='unused'; AgentArguments=''; AgentMode='exec'; AgentProvider='custom'
        AutoConnectProvider='none'; TrustedWorkspaceRoots=''; AgentPermissionMode='inherit'; RequireApproval=$true
        ChatApprovalMode='first'; ChatResultMode='manual'; FileAttachmentApprovalMode='always'
    }
    Write-Json (Join-Path $Data 'settings.json') $settings
    Write-Json (Join-Path $Data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
    Write-Json (Join-Path $Data 'language.json') @{Language=$Language}
    Write-Json (Join-Path $Data 'appearance.json') @{Theme=$Theme}
    Write-Json (Join-Path $Data 'portable-update.json') @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$false;availableRelease=$null}
    Write-Json (Join-Path $Data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false}
}
function Set-Fixture([string]$Data,[string]$Mode) {
    # The timeout is deliberately short so both startup timeout paths are practical in the smoke run.
    $timeout = if ($Mode -eq 'none') { 30 } else { 1 }
    Write-Json (Join-Path $Data 'startup-fixture.json') @{mode=$Mode;timeoutSeconds=$timeout}
}
function Launch([string]$Data) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $install
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($Data)
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process -or $process.Id -eq 39892) { throw 'Refusing an unexpected app process.' }
    [void]$owned.Add($process.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Isolated app exited during startup (PID $($process.Id))." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw "Isolated app window did not appear (PID $($process.Id))." }
    $script:activeProcess = $process
}
function Find-RestartedProcess([string]$Data,[int]$OldPid) {
    $expectedData = [IO.Path]::GetFullPath($Data)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        # A unique fixture data path scopes discovery to this test. Never attach to or
        # clean up any process using the user's normal profile.
        $processRows = Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" |
            Where-Object { $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),$executable,[StringComparison]::OrdinalIgnoreCase) -and
                $_.CommandLine -and $_.CommandLine.IndexOf($expectedData,[StringComparison]::OrdinalIgnoreCase) -ge 0 -and [int]$_.ProcessId -ne $OldPid }
        foreach ($row in $processRows) {
            try {
                $candidate = [Diagnostics.Process]::GetProcessById([int]$row.ProcessId)
                if (-not $candidate.HasExited) {
                    [void]$owned.Add($candidate.Id)
                    $candidate.Refresh()
                    if ($candidate.MainWindowHandle -ne [IntPtr]::Zero) { return $candidate }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Restart did not create a new app process for the fixture data directory.'
}
function Restart-WithRecovery([string]$Data) {
    $old = $activeProcess
    $oldPid = $old.Id
    # AppInstance.Restart can terminate the UIA provider before Invoke returns.
    try { Invoke-Id 'StartupMigrationRetry' } catch { }
    $script:activeProcess = $null
    if (-not $old.WaitForExit(12000)) { throw 'Old startup process did not exit after Retry requested a full restart.' }

    $script:activeProcess = Find-RestartedProcess $Data $oldPid
    Wait { Visible 'NavOverview' } 'overview after full-process retry' 25
    Assert (-not (Visible 'StartupMigrationError')) 'Startup error remained visible after successful retry.'
    return $oldPid
}
function Capture([string]$Name) {
    if (-not $activeProcess -or $activeProcess.HasExited) { throw 'Cannot capture a screenshot without the active fixture process.' }
    $output = winapp ui screenshot -o (Join-Path $root ($Name+'.png')) -a $activeProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Screenshot failed: $($output -join ' ')" }
}
function Stop-FixtureProcess([Diagnostics.Process]$Process) {
    if (-not $Process) { return }
    try {
        $Process.Refresh()
        if (-not $Process.HasExited -and $owned.Contains($Process.Id)) {
            $Process.Kill($true)
            [void]$Process.WaitForExit(6000)
        }
    } catch { }
}
function Assert-ErrorState([string]$Case) {
    Wait { Visible 'StartupMigrationError' } "$Case startup error" 15
    Assert (-not (Visible 'StartupSplashMessage')) "$Case left the startup splash visible with the error state."
    foreach ($id in @('StartupMigrationRetry','StartupMigrationExit')) {
        $button = Find $id
        Assert ($button -and -not $button.Current.IsOffscreen -and $button.Current.IsEnabled) "$Case did not expose enabled $id."
    }
    if (Visible 'NavOverview') { throw "$Case constructed normal navigation while startup was blocked." }
    Capture "$Case-error"
}
function Run-RetryCase([string]$Case,[string]$Mode,[string]$Language,[string]$Theme) {
    $data = Join-Path $root ($Case+'-data')
    Write-Profile $data $Language $Theme
    Set-Fixture $data $Mode
    Launch $data
    Assert-ErrorState $Case
    if ($Mode -eq 'page-late') {
        Start-Sleep -Seconds 3
        Assert-ErrorState ($Case+'-late-completion')
        Assert (-not (Visible 'SettingsSearchBox')) 'Late initialization exposed search behind the startup error.'
    }
    Set-Fixture $data 'none'
    $oldPid = Restart-WithRecovery $data
    Capture "$Case-recovered"
    $checks.Add(@{name=$Case;status='PASS';mode=$Mode;language=$Language;theme=$Theme;oldPid=$oldPid;newPid=$activeProcess.Id})
    Write-Output "PASS $Case shows startup error and Retry restarts into Overview"
    Stop-FixtureProcess $activeProcess
    $script:activeProcess = $null
}

try {
    # Cover both user-facing locale/theme combinations for page construction errors
    # and the bounded startup waits (a hung page and a hung migration coordinator).
    Run-RetryCase 'page-error-ja-dark' 'page-error' 'ja' 'dark'
    Run-RetryCase 'page-error-en-light' 'page-error' 'en' 'light'
    Run-RetryCase 'page-hang-ja-dark' 'page-hang' 'ja' 'dark'
    Run-RetryCase 'page-hang-en-light' 'page-hang' 'en' 'light'
    Run-RetryCase 'page-late-ja-dark' 'page-late' 'ja' 'dark'
    Run-RetryCase 'migration-hang-ja-dark' 'migration-hang' 'ja' 'dark'
    Run-RetryCase 'migration-hang-en-light' 'migration-hang' 'en' 'light'

    $exitData = Join-Path $root 'exit-error-data'
    Write-Profile $exitData 'en' 'light'
    Set-Fixture $exitData 'page-error'
    Launch $exitData
    Assert-ErrorState 'exit-error'
    $exitProcess = $activeProcess
    Invoke-Id 'StartupMigrationExit'
    Assert ($exitProcess.WaitForExit(10000)) 'Exit did not shut down the blocked startup process.'
    Assert ($exitProcess.ExitCode -eq 0) "Exit from startup error returned $($exitProcess.ExitCode)."
    $checks.Add(@{name='exit-error';status='PASS';exitCode=$exitProcess.ExitCode})
    Write-Output 'PASS Exit shuts down cleanly from startup error'
    Stop-FixtureProcess $exitProcess
    $script:activeProcess = $null

    # A clean profile with no startup fixture must follow the normal path.
    $normalData = Join-Path $root 'normal-data'
    Write-Profile $normalData 'en' 'light'
    Launch $normalData
    Wait { Visible 'NavOverview' } 'normal startup without fixture' 25
    Assert (-not (Visible 'StartupMigrationError')) 'Normal startup without a fixture showed a startup error.'
    Capture 'normal-startup'
    $checks.Add(@{name='normal-startup';status='PASS';pid=$activeProcess.Id})
    Write-Output 'PASS normal startup without a fixture reaches Overview'
    Stop-FixtureProcess $activeProcess
    $script:activeProcess = $null
    $script:passed = $true
} catch {
    $script:failure = $_.Exception.Message
    try { if ($activeProcess -and -not $activeProcess.HasExited) { Capture 'failure' } } catch { }
    throw
} finally {
    foreach ($ownedPid in @($owned)) {
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$ownedPid)
            if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(6000) }
        } catch { }
    }
    $summary = @{passed=($script:passed -eq $true);checks=@($checks);root=$root}
    if ($script:failure) { $summary.error=$script:failure }
    Write-Json (Join-Path $root 'results.json') $summary
    Write-Output "Evidence: $root"
}
