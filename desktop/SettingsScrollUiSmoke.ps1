param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/live-follow-up-local'),
    [int]$TimeoutSeconds = 25
)

# Isolated regression coverage for fixed settings navigation and independent
# right-pane scrolling. The app is launched only when this script is run.
$ErrorActionPreference = 'Stop'
$appDirectoryFull = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $appDirectoryFull 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published desktop executable was not found in AppDirectory: $executable"
}

$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$testRoot = Join-Path $artifactDirectory ('settings-scroll-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SettingsScrollUiNative {
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
$script:scenarioData = $null
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
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return
    }
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke(); return
    }
    throw "No UI Automation selection or invoke pattern is available: $Description"
}
function Set-WindowWidth([int]$WidthDips, [int]$HeightDips = 768) {
    $handle = [IntPtr]$script:activeWindow.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'App window has no native handle.' }
    [SettingsScrollUiNative]::Activate($handle)
    $scale = [SettingsScrollUiNative]::GetDpiForWindow($handle) / 96.0
    if (-not [SettingsScrollUiNative]::SetWindowPos($handle, [IntPtr]::Zero, 30, 30, [int]($WidthDips * $scale), [int]($HeightDips * $scale), 0x0004 -bor 0x0010)) {
        throw "Could not resize app window to $WidthDips x $HeightDips DIPs."
    }
    Start-Sleep -Milliseconds 350
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
    Set-WindowWidth 1200
    try { $script:activeWindow.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 200
}
function Select-Page([string]$AutomationId) {
    Invoke-Pattern (Wait-Element $script:activeWindow $AutomationId) "select $AutomationId"
    $expected = $AutomationId
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $element = Find-Element $script:activeWindow $expected
        $pattern = $null
        if ($element -and $element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern) -and ([Windows.Automation.SelectionItemPattern]$pattern).Current.IsSelected) { Start-Sleep -Milliseconds 350; $script:activeWindow = Wait-MainWindow $script:activeProcess; return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Page navigation did not select '$AutomationId'."
}
function Select-Scope([string]$AutomationId) {
    $scope = Wait-Element $script:activeWindow $AutomationId
    Invoke-Pattern $scope "select $AutomationId"
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $scope = Find-Element $script:activeWindow $AutomationId
        $pattern = $null
        if ($scope -and $scope.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern) -and ([Windows.Automation.SelectionItemPattern]$pattern).Current.IsSelected) { Start-Sleep -Milliseconds 350; $script:activeWindow = Wait-MainWindow $script:activeProcess; return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Settings scope did not select '$AutomationId'."
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
function Get-ScrollPattern([string]$AutomationId) {
    $element = Wait-Element $script:activeWindow $AutomationId
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { throw "$AutomationId has no ScrollPattern." }
    return ,([Windows.Automation.ScrollPattern]$pattern)
}
function Scroll-ToBottom([string]$AutomationId) {
    $pattern = Get-ScrollPattern $AutomationId
    if (-not $pattern.Current.VerticallyScrollable) { throw "$AutomationId is not vertically scrollable." }
    for ($i = 0; $i -lt 16; $i++) {
        $previous = $pattern.Current.VerticalScrollPercent
        if ($previous -ge 99.9) { break }
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 120
        $pattern = Get-ScrollPattern $AutomationId
        if ([Math]::Abs($pattern.Current.VerticalScrollPercent - $previous) -lt 0.1) { break }
    }
    if ($pattern.Current.VerticalScrollPercent -le 0.1) { throw "$AutomationId remained at its top after scrolling." }
}
function Scroll-ItemIntoView([Windows.Automation.AutomationElement]$Element, [string]$AutomationId) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
        return
    }
    try { $Element.SetFocus() }
    catch { throw "'$AutomationId' exposes no ScrollItemPattern and could not be focused into view: $($_.Exception.Message)" }
}
function Assert-InnerScrollAndFixedNav([string]$ScrollId, [string]$NavItemId, [string]$BottomFieldId, [string]$Label) {
    $scroll = Get-ScrollPattern $ScrollId
    if (-not $scroll.Current.VerticallyScrollable) { throw "$ScrollId should vertically scroll the $Label pane." }
    $viewport = Wait-Element $script:activeWindow $ScrollId
    $nav = Wait-Element $script:activeWindow $NavItemId
    if ($nav.Current.IsOffscreen) { throw "Navigation item '$NavItemId' is not visible before scrolling $Label." }
    # UIA can report unclipped container bounds after resizing. Check the visible
    # intersection and verify the target control itself against the real window.
    $viewportBounds = $viewport.Current.BoundingRectangle
    $windowBounds = $script:activeWindow.Current.BoundingRectangle
    if ($viewportBounds.IsEmpty -or [Math]::Min($viewportBounds.Bottom,$windowBounds.Bottom)-[Math]::Max($viewportBounds.Top,$windowBounds.Top) -lt 100) {
        throw "$ScrollId has no usable visible area ($Label)."
    }
    $stable = 0; $previous = ''; $settleUntil = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $nav = Wait-Element $script:activeWindow $NavItemId
        $current = $nav.Current.BoundingRectangle.ToString()
        if ($current -eq $previous) { $stable++ } else { $stable = 0; $previous = $current }
        Start-Sleep -Milliseconds 200
    } while ($stable -lt 4 -and [DateTime]::UtcNow -lt $settleUntil)
    if ($stable -lt 4) { throw "Navigation layout did not settle ($Label)." }
    $before = $nav.Current.BoundingRectangle
    Capture-Window "$script:activeScenario-$Label-before.png"
    $outer = Find-Element $script:activeWindow 'MainContentScroll'
    if ($outer -and -not $outer.Current.IsOffscreen) {
        throw "MainContentScroll should be absent or collapsed while the $Label settings page is active."
    }
    $scroll.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 120
    $scroll = Get-ScrollPattern $ScrollId
    $initialOffset = $scroll.Current.VerticalScrollPercent
    for ($i = 0; $i -lt 3; $i++) {
        $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 120
        $scroll = Get-ScrollPattern $ScrollId
    }
    if ($scroll.Current.VerticalScrollPercent -le 0.1 -or [Math]::Abs($scroll.Current.VerticalScrollPercent - $initialOffset) -lt 0.1) { throw "$ScrollId did not move after three large vertical scroll increments." }
    $after = (Wait-Element $script:activeWindow $NavItemId).Current.BoundingRectangle
    if ([Math]::Abs($after.Left - $before.Left) -gt 1 -or [Math]::Abs($after.Top - $before.Top) -gt 1) {
        throw "Navigation item '$NavItemId' moved while the $Label detail pane scrolled: before=$($before.ToString()); after=$($after.ToString())."
    }
    Capture-Window "$script:activeScenario-$Label-after-three-increments.png"
    Scroll-ToBottom $ScrollId
    $bottomField = Wait-Element $script:activeWindow $BottomFieldId
    Scroll-ItemIntoView $bottomField $BottomFieldId
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $bottomField = Wait-Element $script:activeWindow $BottomFieldId
        $fieldBounds = $bottomField.Current.BoundingRectangle
        $viewportBounds = (Wait-Element $script:activeWindow $ScrollId).Current.BoundingRectangle
        if (-not $bottomField.Current.IsOffscreen -and $fieldBounds.Height -gt 1 -and
            $fieldBounds.Top -ge ($viewportBounds.Top - 1) -and $fieldBounds.Bottom -le ([Math]::Min($viewportBounds.Bottom,$script:activeWindow.Current.BoundingRectangle.Bottom) + 1)) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($bottomField.Current.IsOffscreen -or $fieldBounds.Height -lt 1 -or $fieldBounds.Top -lt ($viewportBounds.Top - 1) -or $fieldBounds.Bottom -gt ([Math]::Min($viewportBounds.Bottom,$script:activeWindow.Current.BoundingRectangle.Bottom) + 1)) {
        throw "Bottom settings field '$BottomFieldId' was not brought into the inner viewport for $Label."
    }
    $navAfterField = (Wait-Element $script:activeWindow $NavItemId).Current.BoundingRectangle
    if ([Math]::Abs($navAfterField.Left - $before.Left) -gt 1 -or [Math]::Abs($navAfterField.Top - $before.Top) -gt 1) {
        throw "Navigation item '$NavItemId' moved while bringing '$BottomFieldId' into view ($Label)."
    }
    Capture-Window "$script:activeScenario-$Label-scrolled.png"
    Add-Pass "$Label detail pane scrolls with fixed navigation and accessible bottom field" "offset=$($scroll.Current.VerticalScrollPercent); nav=$NavItemId; bottom=$BottomFieldId"
}
function Assert-ResetToTop([string]$ScrollId, [string]$ScopeId, [string]$Label) {
    Select-Scope $ScopeId
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $scroll = Get-ScrollPattern $ScrollId
        if ($scroll.Current.VerticalScrollPercent -le 0.1) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Label did not reset its right pane to the top when switching to $ScopeId (offset=$($scroll.Current.VerticalScrollPercent))."
}
function Run-Scenario([string]$Language, [string]$Theme) {
    $script:activeScenario = "$Language-$Theme"
    $scenarioRoot = Join-Path $testRoot $script:activeScenario
    $script:scenarioData = Join-Path $scenarioRoot 'data'
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    New-SettingsFixture $script:scenarioData (Join-Path $scenarioRoot 'workspace') $Language $Theme $script:policyVersion
    Start-TestApp $script:scenarioData

    Select-Page 'NavAgentSettings'
    Select-Scope 'AgentScopeCommon'
    Assert-InnerScrollAndFixedNav 'AgentSettingsScroll' 'AgentScopeCodex' 'TrustedWorkspaceRoots' 'agent-wide'
    Assert-ResetToTop 'AgentSettingsScroll' 'AgentScopeCodex' 'agent-scope-switch'
    Assert-ResetToTop 'AgentSettingsScroll' 'AgentScopeCommon' 'agent-common-return'
    Capture-Window "$script:activeScenario-agent-wide-top.png"

    Set-WindowWidth 500
    Select-Scope 'AgentScopeCommon'
    Assert-InnerScrollAndFixedNav 'AgentSettingsScroll' 'AgentScopeCommon' 'TrustedWorkspaceRoots' 'agent-narrow'
    Capture-Window "$script:activeScenario-agent-narrow.png"

    Set-WindowWidth 1200
    Select-Page 'NavConnections'
    Select-Scope 'ConnectionScopeMattermost'
    Assert-InnerScrollAndFixedNav 'ConnectionSettingsScroll' 'ConnectionScopeSlack' 'MattermostAllowedThreadIds' 'connections-wide'
    Assert-ResetToTop 'ConnectionSettingsScroll' 'ConnectionScopeCommon' 'connection-common-switch'
    Capture-Window "$script:activeScenario-connections-common.png"

    Set-WindowWidth 500
    Select-Scope 'ConnectionScopeMattermost'
    Assert-InnerScrollAndFixedNav 'ConnectionSettingsScroll' 'ConnectionScopeCommon' 'MattermostAllowedThreadIds' 'connections-narrow'
    Capture-Window "$script:activeScenario-connections-narrow.png"

    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        $script:activeProcess.Kill($true)
        if (-not $script:activeProcess.WaitForExit(7000)) { throw 'Isolated app process did not exit after scenario cleanup.' }
    }
    $script:activeProcess = $null; $script:activeWindow = $null
}

try {
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion from OnboardingConsentStore.cs.' }
    $script:policyVersion = $policyMatch.Groups[1].Value
    foreach ($language in @('en', 'ja')) { foreach ($theme in @('light', 'dark')) { Run-Scenario $language $theme } }
    Add-Pass 'Settings right-pane scroll regression completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name = "Settings scroll UI ($script:activeScenario)"; status = 'FAIL'; detail = $script:fatal })
    try { Capture-Window 'failure.png' } catch { }
    foreach($id in @('MainContentGrid','SettingsViewport','AgentScopeList','AgentSettingsScroll','ConnectionScopeList','ConnectionSettingsScroll')) { try { $e=Find-Element $script:activeWindow $id; if($e){Write-Host ($id+': '+$e.Current.BoundingRectangle.ToString())} } catch {} }
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(7000) | Out-Null } } catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() } catch { Write-Warning "Could not save UI smoke results: $_" }
}
if ($script:fatal) { throw $script:fatal }
Write-Output "Settings scroll UI artifacts: $testRoot"
