param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/live-follow-up-local'),
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$appDirectoryFull = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $appDirectoryFull 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published desktop executable was not found in AppDirectory: $executable"
}

$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$testRoot = Join-Path $artifactDirectory ('follow-up-settings-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FollowUpUiNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    public static void Activate(IntPtr window) { ShowWindow(window, 9); BringWindowToTop(window); SetForegroundWindow(window); }
}
'@

$script:startedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:results = [Collections.Generic.List[object]]::new()
$script:fatal = $null
$script:activeProcess = $null
$script:activeWindow = $null
$script:activeScenario = ''

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

function Add-Pass([string]$Name, [string]$Detail = '') {
    $script:results.Add(@{ name = $Name; status = 'PASS'; detail = $Detail })
    Write-Host "PASS $Name"
}

function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId) {
    if (-not $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ProcessElement([int]$ProcessId, [string]$AutomationId) {
    $idCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $condition = [Windows.Automation.AndCondition]::new($idCondition, $processCondition)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-MainWindow([Diagnostics.Process]$Process) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        if ($window.Current.Name -notmatch 'PopupHost') { return $window }
    }
    return $null
}

function Wait-MainWindow([Diagnostics.Process]$Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) { throw "App exited during startup with code $($Process.ExitCode)." }
        $window = Get-MainWindow $Process
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'No accessible app window appeared on the interactive Windows desktop.'
}

function Wait-Element([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $Scope $AutomationId
        if ($element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for AutomationId '$AutomationId'."
}

function Wait-ProcessElement([string]$AutomationId, [int]$Seconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-ProcessElement $script:activeProcess.Id $AutomationId
        if ($element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for process UI element '$AutomationId'."
}

function Invoke-Pattern([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    if (-not $Element.Current.IsEnabled) { throw "UI element is disabled: $Description" }
    $invoke = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        ([Windows.Automation.InvokePattern]$invoke).Invoke()
        return
    }
    $selection = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        ([Windows.Automation.SelectionItemPattern]$selection).Select()
        return
    }
    throw "No UI Automation invoke or selection pattern is available: $Description"
}

function Set-WindowSize([Diagnostics.Process]$Process) {
    $handle = [IntPtr]$script:activeWindow.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'App window has no native handle.' }
    [FollowUpUiNative]::Activate($handle)
    $scale = [FollowUpUiNative]::GetDpiForWindow($handle) / 96.0
    if (-not [FollowUpUiNative]::SetWindowPos($handle, [IntPtr]::Zero, 30, 30,
        [int](1024 * $scale), [int](768 * $scale), 0x0004 -bor 0x0010)) {
        throw 'Could not resize the app window to 1024 x 768 DIPs.'
    }
    Start-Sleep -Milliseconds 350
}

function Capture-Window([string]$Name) {
    $rect = $script:activeWindow.Current.BoundingRectangle
    $left = [int][Math]::Max(0, $rect.Left)
    $top = [int][Math]::Max(0, $rect.Top)
    $right = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Right, $rect.Right)
    $bottom = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Bottom, $rect.Bottom)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -lt 1 -or $height -lt 1) { throw 'App window has no visible area to capture.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($width, $height)) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $testRoot $Name), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Start-TestApp([string]$DataDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $appDirectoryFull
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add([IO.Path]::GetFullPath($DataDirectory))
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Desktop process could not be started.' }
    $script:startedProcesses.Add($process)
    $script:activeProcess = $process
    $script:activeWindow = Wait-MainWindow $process
    Set-WindowSize $process
    try { $script:activeWindow.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 250
}

function Stop-TestApp {
    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        $script:activeProcess.Kill($true)
        if (-not $script:activeProcess.WaitForExit(7000)) { throw 'Isolated app process did not exit after cleanup.' }
    }
    $script:activeProcess = $null
    $script:activeWindow = $null
}

function Wait-ForAbsent([string]$AutomationId, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:activeWindow $AutomationId
        if (-not $element -or $element.Current.IsOffscreen) { return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Dialog '$AutomationId' did not close."
}

function Wait-ForStoredMode([string]$Expected) {
    $settingsFile = Join-Path $script:scenarioData 'settings.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        if (Test-Path -LiteralPath $settingsFile) {
            try {
                $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
                if ($settings.ChatFollowUpMode -eq $Expected) { return }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    $actual = if (Test-Path -LiteralPath $settingsFile) { (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ChatFollowUpMode } else { '<missing>' }
    throw "Expected persisted ChatFollowUpMode '$Expected', got '$actual'."
}

function Get-SelectedChoiceName([Windows.Automation.AutomationElement]$Combo) {
    $selection = $null
    if ($Combo.TryGetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern, [ref]$selection)) {
        $selected = ([Windows.Automation.SelectionPattern]$selection).Current.GetSelection()
        if ($selected.Count -gt 0) { return $selected[0].Current.Name }
    }
    $value = $null
    if ($Combo.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value)) {
        return ([Windows.Automation.ValuePattern]$value).Current.Value
    }
    return $Combo.Current.Name
}

function Assert-SelectedMode([string]$ExpectedTag, [string]$ExpectedLabel) {
    $combo = Wait-Element $script:activeWindow 'ChatFollowUpMode' 5
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $selectedName = Get-SelectedChoiceName $combo
        if ($selectedName -eq $ExpectedLabel -or $selectedName -like "*$ExpectedLabel*") { break }
        Start-Sleep -Milliseconds 120
        $combo = Wait-Element $script:activeWindow 'ChatFollowUpMode' 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($selectedName -ne $ExpectedLabel -and $selectedName -notlike "*$ExpectedLabel*") {
        throw "ComboBox did not expose the $ExpectedTag selection '$ExpectedLabel'; current UIA name was '$selectedName'."
    }
    Wait-ForStoredMode $ExpectedTag
}

function Set-FollowUpMode([string]$Tag, [string]$Label) {
    $combo = Wait-Element $script:activeWindow 'ChatFollowUpMode'
    $expand = $null
    if (-not $combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
        throw 'ChatFollowUpMode does not expose ExpandCollapsePattern.'
    }
    if (([Windows.Automation.ExpandCollapsePattern]$expand).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) {
        ([Windows.Automation.ExpandCollapsePattern]$expand).Expand()
    }
    $optionId = if ($Tag -eq 'steer') { 'ChatFollowUpModeSteer' } else { 'ChatFollowUpModeQueue' }
    $option = Wait-ProcessElement $optionId 6
    if ($option.Current.Name -ne $Label) { throw "Expected '$Label' for $optionId, found '$($option.Current.Name)'." }
    $selection = $null
    if (-not $option.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        throw "ComboBox option '$optionId' has no SelectionItemPattern."
    }
    ([Windows.Automation.SelectionItemPattern]$selection).Select()
}

function Assert-VisibleInsideViewport([string]$AutomationId, [Windows.Automation.AutomationElement]$Viewport,
    [Windows.Automation.AutomationElement]$Window) {
    $element = Wait-Element $Window $AutomationId
    $bounds = $element.Current.BoundingRectangle
    $viewportBounds = $Viewport.Current.BoundingRectangle
    $windowBounds = $Window.Current.BoundingRectangle
    $left = [Math]::Max($viewportBounds.Left, $windowBounds.Left)
    $top = [Math]::Max($viewportBounds.Top, $windowBounds.Top)
    $right = [Math]::Min($viewportBounds.Right, $windowBounds.Right)
    $bottom = [Math]::Min($viewportBounds.Bottom, $windowBounds.Bottom)
    if ($element.Current.IsOffscreen -or $bounds.IsEmpty -or $bounds.Width -lt 1 -or $bounds.Height -lt 1 -or
        $bounds.Left -lt ($left - 2) -or $bounds.Top -lt ($top - 2) -or
        $bounds.Right -gt ($right + 2) -or $bounds.Bottom -gt ($bottom + 2)) {
        throw "'$AutomationId' is clipped or outside the visible page area: element=$($bounds.ToString()); viewport=$($viewportBounds.ToString()); window=$($windowBounds.ToString())."
    }
    return $element
}

function Scroll-ToVisible([Windows.Automation.AutomationElement]$Target) {
    $scroll = Wait-Element $script:activeWindow 'MainContentScroll'
    $pattern = [Windows.Automation.ScrollPattern]$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $viewport = $scroll.Current.BoundingRectangle
    $window = $script:activeWindow.Current.BoundingRectangle
    $bottom = [Math]::Min($viewport.Bottom, $window.Bottom)
    for ($i = 0; $i -lt 12; $i++) {
        $bounds = $Target.Current.BoundingRectangle
        if (-not $Target.Current.IsOffscreen -and $bounds.Height -gt 0 -and
            $bounds.Top -ge $viewport.Top -and $bounds.Bottom -le $bottom) { return }
        if (-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100) { break }
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 100
        $Target = Wait-Element $script:activeWindow $Target.Current.AutomationId 3
    }
    throw "Could not scroll '$($Target.Current.AutomationId)' fully into the visible page."
}

function New-SettingsFixture([string]$DataDirectory, [string]$Workspace, [string]$Language, [string]$Theme, [string]$PolicyVersion) {
    New-Item -ItemType Directory -Force -Path $DataDirectory, $Workspace | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $Workspace; AgentExecutable = 'codex'; AgentArguments = 'exec --json -'
        AgentMode = 'app-server'; AgentProvider = 'codex'; AutoConnectProvider = 'none'
        RequireApproval = $true; AgentPermissionMode = 'inherit'; ChatApprovalMode = 'first'
        ChatResultMode = 'manual'; ChatFollowUpMode = 'queue'; FileAttachmentApprovalMode = 'always'
    }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{
        PolicyVersion = $PolicyVersion; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language = $Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme = $Theme }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{
        schemaVersion = 1; automaticChecksEnabled = $false; includePrereleases = $true
        lastAttemptUtc = $null; availableRelease = $null
    }
}

function Run-Scenario([string]$Language, [string]$Theme) {
    $scenario = "$Language-$Theme"
    $script:activeScenario = $scenario
    $scenarioRoot = Join-Path $testRoot $scenario
    $script:scenarioData = Join-Path $scenarioRoot 'data'
    $workspace = Join-Path $scenarioRoot 'workspace'
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    New-SettingsFixture $script:scenarioData $workspace $Language $Theme $script:policyVersion
    Start-TestApp $script:scenarioData

    $nav = Wait-Element $script:activeWindow 'NavConnections'
    $navSelection = $null
    if (-not $nav.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$navSelection)) {
        throw 'NavConnections has no SelectionItemPattern.'
    }
    ([Windows.Automation.SelectionItemPattern]$navSelection).Select()
    $script:activeWindow = Wait-MainWindow $script:activeProcess

    $combo = Wait-Element $script:activeWindow 'ChatFollowUpMode'
    Scroll-ToVisible $combo
    $scroll = Wait-Element $script:activeWindow 'MainContentScroll'
    $help = Assert-VisibleInsideViewport 'ChatFollowUpModeHelp' $scroll $script:activeWindow
    $combo = Assert-VisibleInsideViewport 'ChatFollowUpMode' $scroll $script:activeWindow
    if ($combo.Current.Name -notlike '*Messages received during execution*' -and
        $combo.Current.Name -notlike '*実行中に届いたメッセージ*') {
        throw "Localized follow-up setting label was not exposed on the ComboBox: '$($combo.Current.Name)'."
    }
    if ($help.Current.Name -notmatch '\S' -or $help.Current.BoundingRectangle.Height -lt 25) {
        throw "Follow-up help text is empty or did not wrap at 1024 x 768: '$($help.Current.Name)'."
    }

    $queueLabel = if ($Language -eq 'ja') { '順番に実行（既定）' } else { 'Run in order (default)' }
    $steerLabel = if ($Language -eq 'ja') { '実行中の依頼へ追加' } else { 'Add to the running request' }
    $modeHelp = if ($Language -eq 'ja') { 'Codex App ServerとClaude Codeで利用できます。' } else { 'Available with Codex App Server and Claude Code.' }

    $expand = $null
    if (-not $combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
        throw 'ChatFollowUpMode does not expose ExpandCollapsePattern.'
    }
    ([Windows.Automation.ExpandCollapsePattern]$expand).Expand()
    $queueOption = Wait-ProcessElement 'ChatFollowUpModeQueue' 5
    $steerOption = Wait-ProcessElement 'ChatFollowUpModeSteer' 5
    if ($queueOption.Current.Name -ne $queueLabel -or $steerOption.Current.Name -ne $steerLabel) {
        throw "Follow-up options are not localized for $scenario."
    }
    ([Windows.Automation.ExpandCollapsePattern]$expand).Collapse()
    Start-Sleep -Milliseconds 120
    Capture-Window "${scenario}-follow-up-settings.png"
    Add-Pass "Localized follow-up label, options, and wrapped help ($scenario)" $modeHelp

    Set-FollowUpMode 'steer' $steerLabel
    $steerDialog = Wait-Element $script:activeWindow 'FollowUpSteerDialog' 15
    Capture-Window "${scenario}-steer-explanation.png"
    Invoke-Pattern (Wait-Element $steerDialog 'CloseButton' 5) 'decline the live follow-up explanation'
    Wait-ForAbsent 'FollowUpSteerDialog'
    Assert-SelectedMode 'queue' $queueLabel
    Add-Pass "Declining the explanation keeps queue mode ($scenario)"

    Set-FollowUpMode 'steer' $steerLabel
    $steerDialog = Wait-Element $script:activeWindow 'FollowUpSteerDialog' 15
    Invoke-Pattern (Wait-Element $steerDialog 'PrimaryButton' 5) 'agree to the live follow-up explanation'
    Wait-ForAbsent 'FollowUpSteerDialog'
    $dialog = Wait-Element $script:activeWindow 'SensitiveSettingsConfirmation' 15
    Capture-Window "${scenario}-steer-confirmation.png"
    Invoke-Pattern (Wait-Element $dialog 'CloseButton' 5) 'cancel sensitive follow-up confirmation'
    Wait-ForAbsent 'SensitiveSettingsConfirmation'
    Assert-SelectedMode 'queue' $queueLabel
    Add-Pass "Cancel restores queue mode and keeps persisted queue ($scenario)"

    Set-FollowUpMode 'steer' $steerLabel
    $steerDialog = Wait-Element $script:activeWindow 'FollowUpSteerDialog' 15
    Invoke-Pattern (Wait-Element $steerDialog 'PrimaryButton' 5) 'agree to the live follow-up explanation'
    Wait-ForAbsent 'FollowUpSteerDialog'
    $dialog = Wait-Element $script:activeWindow 'SensitiveSettingsConfirmation' 15
    Invoke-Pattern (Wait-Element $dialog 'PrimaryButton' 5) 'approve sensitive follow-up confirmation'
    Wait-ForAbsent 'SensitiveSettingsConfirmation'
    Assert-SelectedMode 'steer' $steerLabel
    Add-Pass "Approve persists steer mode ($scenario)"

    Stop-TestApp
    Start-TestApp $script:scenarioData
    $nav = Wait-Element $script:activeWindow 'NavConnections'
    $navSelection = $null
    if (-not $nav.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$navSelection)) {
        throw 'NavConnections has no SelectionItemPattern after restart.'
    }
    ([Windows.Automation.SelectionItemPattern]$navSelection).Select()
    $combo = Wait-Element $script:activeWindow 'ChatFollowUpMode'
    Scroll-ToVisible $combo
    Assert-SelectedMode 'steer' $steerLabel
    Capture-Window "${scenario}-steer-after-restart.png"
    Add-Pass "Steer mode persists after app restart ($scenario)"

    Set-FollowUpMode 'queue' $queueLabel
    $dialog = Wait-Element $script:activeWindow 'SensitiveSettingsConfirmation' 15
    Invoke-Pattern (Wait-Element $dialog 'PrimaryButton' 5) 'restore default queue mode'
    Wait-ForAbsent 'SensitiveSettingsConfirmation'
    Assert-SelectedMode 'queue' $queueLabel
    Stop-TestApp
}

try {
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion from OnboardingConsentStore.cs.' }
    $script:policyVersion = $policyMatch.Groups[1].Value

    foreach ($language in @('ja', 'en')) {
        foreach ($theme in @('light', 'dark')) { Run-Scenario $language $theme }
    }
    Add-Pass 'Follow-up settings UI smoke completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name = "Follow-up settings UI ($script:activeScenario)"; status = 'FAIL'; detail = $script:fatal })
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(7000) | Out-Null }
        }
        catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() }
    catch { Write-Warning "Could not save UI smoke results: $_" }
}

if ($script:fatal) { throw $script:fatal }
Write-Output "Follow-up settings UI artifacts: $testRoot"
