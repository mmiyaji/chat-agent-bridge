param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/live-follow-up-local'),
    [int]$TimeoutSeconds = 25
)

# Isolated UI regression coverage for ChatApprovalMode and the switch-command
# approval opt-in. The app is launched only when this script is explicitly run.
$ErrorActionPreference = 'Stop'
$appDirectoryFull = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $appDirectoryFull 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published desktop executable was not found in AppDirectory: $executable"
}

$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$testRoot = Join-Path $artifactDirectory ('switch-approval-settings-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SwitchApprovalUiNative {
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
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ProcessElement([int]$ProcessId, [string]$AutomationId) {
    $id = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $process = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new($id, $process))
}
function Get-MainWindow([Diagnostics.Process]$Process) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)) {
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
        Start-Sleep -Milliseconds 120
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
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke(); return
    }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.TogglePattern]$pattern).Toggle(); return
    }
    throw "No UI Automation invoke or toggle pattern is available: $Description"
}
function Set-WindowWidth([int]$WidthDips, [int]$HeightDips = 768) {
    $handle = [IntPtr]$script:activeWindow.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'App window has no native handle.' }
    [SwitchApprovalUiNative]::Activate($handle)
    $scale = [SwitchApprovalUiNative]::GetDpiForWindow($handle) / 96.0
    if (-not [SwitchApprovalUiNative]::SetWindowPos($handle, [IntPtr]::Zero, 30, 30, [int]($WidthDips * $scale), [int]($HeightDips * $scale), 0x0004 -bor 0x0010)) {
        throw "Could not resize app window to $WidthDips x $HeightDips DIPs."
    }
    Start-Sleep -Milliseconds 300
}
function Capture-Window([string]$Name) {
    $rect = $script:activeWindow.Current.BoundingRectangle
    $left = [int][Math]::Max(0, $rect.Left); $top = [int][Math]::Max(0, $rect.Top)
    $right = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Right, $rect.Right)
    $bottom = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Bottom, $rect.Bottom)
    $width = $right - $left; $height = $bottom - $top
    if ($width -lt 1 -or $height -lt 1) { throw 'App window has no visible area to capture.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($width, $height)) } finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $testRoot $Name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}
function Start-TestApp([string]$DataDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $appDirectoryFull
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add([IO.Path]::GetFullPath($DataDirectory))
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Desktop process could not be started.' }
    $script:startedProcesses.Add($process); $script:activeProcess = $process
    $script:activeWindow = Wait-MainWindow $process
    Set-WindowWidth 900
    try { $script:activeWindow.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 200
}
function Stop-TestApp {
    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        $script:activeProcess.Kill($true)
        if (-not $script:activeProcess.WaitForExit(7000)) { throw 'Isolated app process did not exit after cleanup.' }
    }
    $script:activeProcess = $null; $script:activeWindow = $null
}
function Wait-ForAbsent([string]$AutomationId, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:activeWindow $AutomationId
        if (-not $element -or $element.Current.IsOffscreen) { return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Dialog or warning '$AutomationId' did not close."
}
function Wait-StoredSettings([string]$ExpectedMode, [bool]$ExpectedSwitch, [int]$Seconds = 12) {
    $settingsFile = Join-Path $script:scenarioData 'settings.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Test-Path -LiteralPath $settingsFile) {
            try {
                $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
                if ($settings.ChatApprovalMode -eq $ExpectedMode -and [bool]$settings.AutoApproveSwitchCommands -eq $ExpectedSwitch) { return $settings }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    $actual = if (Test-Path -LiteralPath $settingsFile) { Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json } else { $null }
    throw "Expected persisted ChatApprovalMode='$ExpectedMode', AutoApproveSwitchCommands=$ExpectedSwitch; got mode='$($actual.ChatApprovalMode)', switch='$($actual.AutoApproveSwitchCommands)'."
}
function Assert-Toggle([bool]$Expected, [bool]$Enabled) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
    $toggle = Wait-Element $script:activeWindow 'AutoApproveSwitchCommands'
    $pattern = $null
    if (-not $toggle.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { throw 'AutoApproveSwitchCommands has no TogglePattern.' }
    $state = ([Windows.Automation.TogglePattern]$pattern).Current.ToggleState
    $expectedState = if ($Expected) { [Windows.Automation.ToggleState]::On } else { [Windows.Automation.ToggleState]::Off }
    if ($state -eq $expectedState -and $toggle.Current.IsEnabled -eq $Enabled) { return $toggle }
    Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($state -ne $expectedState) { throw "Expected AutoApproveSwitchCommands=$Expected, found $state." }
    if ($toggle.Current.IsEnabled -ne $Enabled) { throw "Expected AutoApproveSwitchCommands IsEnabled=$Enabled, found $($toggle.Current.IsEnabled)." }
    return $toggle
}
function Assert-InViewport([string]$AutomationId) {
    $scroll = Wait-Element $script:activeWindow 'MainContentScroll'
    $element = Wait-Element $script:activeWindow $AutomationId
    $bounds = $element.Current.BoundingRectangle; $viewport = $scroll.Current.BoundingRectangle; $window = $script:activeWindow.Current.BoundingRectangle
    $left = [Math]::Max($viewport.Left, $window.Left); $top = [Math]::Max($viewport.Top, $window.Top)
    $right = [Math]::Min($viewport.Right, $window.Right); $bottom = [Math]::Min($viewport.Bottom, $window.Bottom)
    if ($element.Current.IsOffscreen -or $bounds.IsEmpty -or $bounds.Width -lt 1 -or $bounds.Height -lt 1 -or $bounds.Left -lt ($left - 2) -or $bounds.Top -lt ($top - 2) -or $bounds.Right -gt ($right + 2) -or $bounds.Bottom -gt ($bottom + 2)) {
        throw "'$AutomationId' is clipped or outside the visible page area: element=$($bounds.ToString()); viewport=$($viewport.ToString()); window=$($window.ToString())."
    }
    return $element
}
function Scroll-IntoViewport([string]$AutomationId) {
    $scroll = Wait-Element $script:activeWindow 'MainContentScroll'
    $pattern = $null
    if (-not $scroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { throw 'MainContentScroll has no ScrollPattern.' }
    for ($i = 0; $i -lt 12; $i++) {
        $element = Wait-Element $script:activeWindow $AutomationId
        $bounds = $element.Current.BoundingRectangle; $viewport = $scroll.Current.BoundingRectangle
        if (-not $element.Current.IsOffscreen -and $bounds.Top -ge $viewport.Top -and $bounds.Bottom -le $viewport.Bottom) { return }
        $state = ([Windows.Automation.ScrollPattern]$pattern).Current
        if (-not $state.VerticallyScrollable) { break }
        if ($bounds.Top -lt $viewport.Top) {
            if ($state.VerticalScrollPercent -le 0) { break }
            ([Windows.Automation.ScrollPattern]$pattern).Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeDecrement)
        } else {
            if ($state.VerticalScrollPercent -ge 100) { break }
            ([Windows.Automation.ScrollPattern]$pattern).Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Could not scroll '$AutomationId' into the visible viewport."
}
function Set-ApprovalMode([string]$Tag) {
    $combo = Wait-Element $script:activeWindow 'ChatApprovalMode'
    $expand = $null
    if (-not $combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { throw 'ChatApprovalMode has no ExpandCollapsePattern.' }
    if (([Windows.Automation.ExpandCollapsePattern]$expand).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { ([Windows.Automation.ExpandCollapsePattern]$expand).Expand() }
    $optionId = switch ($Tag) { 'always' { 'chatAgentBridgeDesktopMainPageComboBoxItem006' } 'first' { 'chatAgentBridgeDesktopMainPageComboBoxItem007' } 'auto' { 'chatAgentBridgeDesktopMainPageComboBoxItem008' } default { throw "Unsupported chat approval mode '$Tag'." } }
    $option = Wait-ProcessElement $optionId 5
    $selection = $null
    if (-not $option.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) { throw "Chat approval option '$Tag' has no SelectionItemPattern." }
    ([Windows.Automation.SelectionItemPattern]$selection).Select()
    Start-Sleep -Milliseconds 200
}
function Toggle-Switch {
    $toggle = Assert-Toggle $false $true
    Invoke-Pattern $toggle 'turn on automatic switch-command approval'
}
function Accept-SwitchWarning {
    $dialog = Wait-Element $script:activeWindow 'SwitchApprovalWarningDialog' 10
    Invoke-Pattern (Wait-Element $dialog 'PrimaryButton' 5) 'accept switch-command warning'
    Wait-ForAbsent 'SwitchApprovalWarningDialog'
}
function Assert-SensitiveDialogAndAccept {
    $dialog = Wait-Element $script:activeWindow 'SensitiveSettingsConfirmation' 15
    Invoke-Pattern (Wait-Element $dialog 'PrimaryButton' 5) 'confirm sensitive setting'
    Wait-ForAbsent 'SensitiveSettingsConfirmation'
}
function Select-ConnectionsPage {
    $nav = Wait-Element $script:activeWindow 'NavConnections'
    $selection = $null
    if (-not $nav.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) { throw 'NavConnections has no SelectionItemPattern.' }
    ([Windows.Automation.SelectionItemPattern]$selection).Select()
    Start-Sleep -Milliseconds 200
}
function New-SettingsFixture([string]$DataDirectory, [string]$Workspace, [string]$Language, [string]$Theme, [string]$PolicyVersion) {
    New-Item -ItemType Directory -Force -Path $DataDirectory, $Workspace | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $Workspace; AgentExecutable = 'codex'; AgentArguments = 'exec --json -'
        AgentMode = 'app-server'; AgentProvider = 'codex'; AutoConnectProvider = 'none'
        RequireApproval = $true; AgentPermissionMode = 'inherit'; ChatApprovalMode = 'first'; AutoApproveSwitchCommands = $false
        ChatResultMode = 'manual'; ChatFollowUpMode = 'queue'; FileAttachmentApprovalMode = 'always'
    }
    Write-Json (Join-Path $DataDirectory 'tips.json') @{ Seen = @('chat-connect') }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{ PolicyVersion = $PolicyVersion; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language = $Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme = $Theme }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{ schemaVersion = 1; automaticChecksEnabled = $false; includePrereleases = $true; lastAttemptUtc = $null; availableRelease = $null }
}

function Run-Scenario([string]$Language, [string]$Theme) {
    $scenario = "$Language-$Theme"; $script:activeScenario = $scenario
    $scenarioRoot = Join-Path $testRoot $scenario
    $script:scenarioData = Join-Path $scenarioRoot 'data'
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    New-SettingsFixture $script:scenarioData (Join-Path $scenarioRoot 'workspace') $Language $Theme $script:policyVersion
    Start-TestApp $script:scenarioData
    Select-ConnectionsPage

    Scroll-IntoViewport 'ChatApprovalMode'
    $mode = Assert-InViewport 'ChatApprovalMode'
    $switch = Assert-InViewport 'AutoApproveSwitchCommands'
    $help = Assert-InViewport 'AutoApproveSwitchCommandsHelp'
    if ($help.Current.Name -notmatch '\S' -or $help.Current.BoundingRectangle.Height -lt 20) { throw "Switch approval help is empty or not wrapped ($scenario)." }
    if (($Language -eq 'ja' -and $help.Current.Name -notmatch '会話の最初の依頼') -or
        ($Language -eq 'en' -and $help.Current.Name -notmatch 'conversation.s first request')) {
        throw "Switch approval help is not localized for ${scenario}: '$($help.Current.Name)'."
    }
    if (-not $switch.Current.IsEnabled) { throw "Switch approval must be enabled while ChatApprovalMode is first ($scenario)." }
    Assert-Toggle $false $true | Out-Null
    Wait-StoredSettings 'first' $false | Out-Null
    Capture-Window "$scenario-first-default.png"
    Add-Pass "Common chat approval controls and first-mode default ($scenario)" $help.Current.Name

    # Check the narrower common settings layout and its viewport bounds.
    Set-WindowWidth 500
    $header = Wait-Element $script:activeWindow 'chatAgentBridgeDesktopMainPageTextBlock047'
    $headerBounds = $header.Current.BoundingRectangle
    $windowBounds = $script:activeWindow.Current.BoundingRectangle
    if ($headerBounds.Right -gt $windowBounds.Right -or $headerBounds.Width -gt $windowBounds.Width) { throw "Page explanation extends beyond the narrow window ($scenario)." }
    Scroll-IntoViewport 'ChatApprovalMode'
    $mode = Assert-InViewport 'ChatApprovalMode'
    Scroll-IntoViewport 'AutoApproveSwitchCommands'
    $switch = Assert-InViewport 'AutoApproveSwitchCommands'
    Scroll-IntoViewport 'AutoApproveSwitchCommandsHelp'
    $help = Assert-InViewport 'AutoApproveSwitchCommandsHelp'
    Capture-Window "$scenario-common-500.png"
    Add-Pass "Common settings remain inside the 500-DIP viewport ($scenario)"
    foreach ($helpId in @('chatAgentBridgeDesktopMainPageTextBlock032', 'chatAgentBridgeDesktopMainPageTextBlock033', 'ChatFollowUpModeHelp')) {
        Scroll-IntoViewport $helpId
        Assert-InViewport $helpId | Out-Null
        Capture-Window "$scenario-$helpId-500.png"
    }
    Add-Pass "Informational explanations wrap inside the narrow viewport ($scenario)"
    Set-WindowWidth 900
    Scroll-IntoViewport 'AutoApproveSwitchCommands'

    Toggle-Switch
    $warningDialog = Wait-Element $script:activeWindow 'SwitchApprovalWarningDialog' 10
    Capture-Window "$scenario-switch-warning.png"
    $warningText = Wait-Element $warningDialog 'SwitchApprovalWarningBody' 5
    if ($warningText.Current.Name -notmatch '\S') { throw "Switch approval warning body is empty ($scenario)." }
    Invoke-Pattern (Wait-Element $warningDialog 'CloseButton' 5) 'cancel switch-command warning'
    Wait-ForAbsent 'SwitchApprovalWarningDialog'
    Assert-Toggle $false $true | Out-Null
    Wait-StoredSettings 'first' $false | Out-Null
    Add-Pass "Canceling the warning restores off and persisted first mode ($scenario)"

    Toggle-Switch
    Accept-SwitchWarning
    Assert-SensitiveDialogAndAccept
    Assert-Toggle $true $true | Out-Null
    Scroll-IntoViewport 'AutoApproveSwitchWarning'
    Assert-InViewport 'AutoApproveSwitchWarning' | Out-Null
    Capture-Window "$scenario-switch-approval-enabled.png"
    Wait-StoredSettings 'first' $true | Out-Null
    Add-Pass "Accepting warning and sensitive confirmation persists first plus switch approval ($scenario)"

    Stop-TestApp
    Start-TestApp $script:scenarioData
    Select-ConnectionsPage
    Assert-Toggle $true $true | Out-Null
    Wait-StoredSettings 'first' $true | Out-Null
    Capture-Window "$scenario-first-after-restart.png"
    Add-Pass "First mode and switch approval persist after restart ($scenario)"

    Set-ApprovalMode 'always'
    Assert-SensitiveDialogAndAccept
    Assert-Toggle $false $false | Out-Null
    Wait-StoredSettings 'always' $false | Out-Null
    Add-Pass "Selecting always clears switch approval and disables its toggle ($scenario)"

    Set-ApprovalMode 'auto'
    Assert-SensitiveDialogAndAccept
    Assert-Toggle $false $true | Out-Null
    Toggle-Switch
    Accept-SwitchWarning
    Assert-SensitiveDialogAndAccept
    Assert-Toggle $true $true | Out-Null
    Wait-StoredSettings 'auto' $true | Out-Null
    Add-Pass "Re-enabling auto mode requires warning and confirmation ($scenario)"
    Stop-TestApp
}

try {
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion from OnboardingConsentStore.cs.' }
    $script:policyVersion = $policyMatch.Groups[1].Value
    foreach ($language in @('ja', 'en')) { foreach ($theme in @('light', 'dark')) { Run-Scenario $language $theme } }
    Add-Pass 'Switch approval settings UI smoke completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name = "Switch approval settings UI ($script:activeScenario)"; status = 'FAIL'; detail = $script:fatal })
    try { Capture-Window "failure.png" } catch { }
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(7000) | Out-Null } } catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() } catch { Write-Warning "Could not save UI smoke results: $_" }
}
if ($script:fatal) { throw $script:fatal }
Write-Output "Switch approval settings UI artifacts: $testRoot"
