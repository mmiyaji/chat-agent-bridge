param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$EvidenceDirectory,
    [int]$MinimumWidth = 500,
    [int]$MinimumHeight = 600,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$executable = Join-Path $artifactRootFull 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Portable desktop executable was not found: $executable"
}
if ($MinimumWidth -lt 500 -or $MinimumHeight -lt 600) {
    throw 'The minimum test size cannot be smaller than the app-enforced 500 x 600 logical pixels.'
}

$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $evidenceRoot = Join-Path $artifactDirectory ('localization-ui-' + [guid]::NewGuid().ToString('N'))
} else {
    $evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
}
$artifactPrefix = $artifactDirectory.TrimEnd('\') + '\'
$evidencePrefix = $evidenceRoot.TrimEnd('\') + '\'
if (-not $evidencePrefix.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "EvidenceDirectory must be a new directory below $artifactDirectory."
}
if (Test-Path -LiteralPath $evidenceRoot) {
    throw "EvidenceDirectory already exists; choose a new GUID directory: $evidenceRoot"
}
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LocalizationUiSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

$script:results = [Collections.Generic.List[object]]::new()
$script:ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:activeProcess = $null
$script:activeWindow = $null
$script:activeData = $null

function Add-Result([string]$Name, [string]$Status, [string]$Detail = '') {
    $script:results.Add([ordered]@{ name = $Name; status = $Status; detail = $Detail })
    Write-Host "$Status $Name$(if ($Detail) { ': ' + $Detail })"
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId) {
    if ($null -eq $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Element([string]$AutomationId, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $revealed = $false
    do {
        $element = Find-Element $script:activeWindow $AutomationId
        if ($element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) { return $element }
        if ($element -and -not $revealed -and $AutomationId -notin @('CloseButton','ArchitectureViewerDialog','PolicyBody')) {
            Reveal-Element $AutomationId
            $revealed = $true
            $element = Find-Element $script:activeWindow $AutomationId
            if ($element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) { return $element }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for visible AutomationId '$AutomationId'."
}

function Reveal-Element([string]$AutomationId) {
    $target = Find-Element $script:activeWindow $AutomationId
    if (-not $target -or -not $target.Current.IsOffscreen) { return }
    $scroll = Find-Element $script:activeWindow 'MainContentScroll'
    if (-not $scroll) { $scroll = Find-Element $script:activeWindow 'AboutScroll' }
    if (-not $scroll) { $scroll = Find-Element $script:activeWindow 'WizardScroll' }
    if (-not $scroll) { return }
    $pattern = $null
    if (-not $scroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { return }
    $scrollPattern = [Windows.Automation.ScrollPattern]$pattern
    for ($i = 0; $i -lt 16; $i++) {
        $target = Find-Element $script:activeWindow $AutomationId
        if ($target -and -not $target.Current.IsOffscreen -and $target.Current.BoundingRectangle.Height -gt 0) { return }
        if (-not $scrollPattern.Current.VerticallyScrollable -or $scrollPattern.Current.VerticalScrollPercent -ge 100) { break }
        $scrollPattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 120
    }
}

function Find-ElementById([string]$AutomationId) {
    return Find-Element $script:activeWindow $AutomationId
}

function Get-ElementText([Windows.Automation.AutomationElement]$Element) {
    if ($null -eq $Element) { return '' }
    $text = [string]$Element.Current.Name
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        $value = [string]([Windows.Automation.ValuePattern]$pattern).Current.Value
        if (-not [string]::IsNullOrWhiteSpace($value)) { $text = $value }
    }
    return $text
}

function Assert-Localized([string]$AutomationId, [string]$Language) {
    $element = Wait-Element $AutomationId
    $text = Get-ElementText $element
    Assert-That (-not [string]::IsNullOrWhiteSpace($text)) "Accessible text is empty for $AutomationId."
    if ($Language -eq 'ja') {
        Assert-That ($text -match '[\u3040-\u30ff\u3400-\u9fff]') "Expected Japanese accessible text for ${AutomationId}; got '$text'."
    } else {
        Assert-That ($text -notmatch '[\u3040-\u30ff\u3400-\u9fff]') "Unexpected Japanese accessible text for ${AutomationId}: '$text'."
    }
    return $element
}

function Scroll-ToTop([string]$ScrollAutomationId) {
    $scroll = Find-ElementById $ScrollAutomationId
    if (-not $scroll) { return }
    $pattern = $null
    if ($scroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and
        ([Windows.Automation.ScrollPattern]$pattern).Current.VerticallyScrollable) {
        ([Windows.Automation.ScrollPattern]$pattern).SetScrollPercent(-1, 0)
        Start-Sleep -Milliseconds 150
    }
}

function Select-Scope([string]$AutomationId) {
    $scope = Wait-Element $AutomationId
    Invoke-Element $scope "select settings scope $AutomationId"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $scope = Find-ElementById $AutomationId
        $pattern = $null
        if ($scope -and $scope.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern) -and
            ([Windows.Automation.SelectionItemPattern]$pattern).Current.IsSelected) { return $scope }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Settings scope did not select: $AutomationId"
}

function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    Assert-That ($null -ne $Element) "Missing UI element: $Description"
    Assert-That $Element.Current.IsEnabled "UI element is disabled: $Description"
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select()
    } elseif ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke()
    } else {
        throw "UI element has no supported invoke or selection pattern: $Description"
    }
    Start-Sleep -Milliseconds 350
}

function Invoke-Id([string]$AutomationId, [string]$Description = $AutomationId) {
    Invoke-Element (Find-ElementById $AutomationId) $Description
}

function Expand-Element([string]$AutomationId) {
    $element = Wait-Element $AutomationId
    Assert-That ($null -ne $element) "Missing expander: $AutomationId"
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        $expand = [Windows.Automation.ExpandCollapsePattern]$pattern
        if ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $expand.Expand() }
    } elseif ($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $toggle = [Windows.Automation.TogglePattern]$pattern
        if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    } else {
        $rect = $element.Current.BoundingRectangle
        Assert-That ($rect.Width -gt 0 -and $rect.Height -gt 0 -and -not $element.Current.IsOffscreen) "Expander '$AutomationId' has no visible clickable bounds."
        $cursor = [Windows.Forms.Cursor]::Position
        [void][LocalizationUiSmokeNative]::SetCursorPos([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
        [LocalizationUiSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [LocalizationUiSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        [void][LocalizationUiSmokeNative]::SetCursorPos($cursor.X, $cursor.Y)
    }
    Start-Sleep -Milliseconds 300
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
        $Process.Refresh()
        if ($Process.HasExited) { throw "Desktop process exited with code $($Process.ExitCode)." }
        $window = Get-MainWindow $Process
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'No accessible desktop window appeared. Run this smoke in an interactive Windows session.'
}

function Resize-Window([int]$Width, [int]$Height) {
    $windowHandle = [IntPtr]$script:activeWindow.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) { throw 'The main window has no native handle.' }
    $scale = [LocalizationUiSmokeNative]::GetDpiForWindow($windowHandle) / 96.0
    $ok = [LocalizationUiSmokeNative]::SetWindowPos($windowHandle, [IntPtr]::Zero, 24, 24,
        [int]($Width * $scale), [int]($Height * $scale), 0x0010)
    if (-not $ok) { throw "Could not resize the app to ${Width}x${Height}." }
    Start-Sleep -Milliseconds 350
    $script:activeWindow = Get-MainWindow $script:activeProcess
}

function Activate-AppWindow {
    $windowHandle = [IntPtr]$script:activeWindow.Current.NativeWindowHandle
    [void][LocalizationUiSmokeNative]::ShowWindow($windowHandle, 9)
    [void][LocalizationUiSmokeNative]::BringWindowToTop($windowHandle)
    try { $script:activeWindow.SetFocus() } catch { }
    $foreground = [LocalizationUiSmokeNative]::GetForegroundWindow()
    $foregroundPid = 0
    $foregroundThread = [LocalizationUiSmokeNative]::GetWindowThreadProcessId($foreground, [ref]$foregroundPid)
    $windowPid = 0
    $windowThread = [LocalizationUiSmokeNative]::GetWindowThreadProcessId($windowHandle, [ref]$windowPid)
    $currentThread = [LocalizationUiSmokeNative]::GetCurrentThreadId()
    $attachedForeground = $false
    $attachedWindow = $false
    if ($foregroundThread -and $foregroundThread -ne $currentThread) {
        $attachedForeground = [LocalizationUiSmokeNative]::AttachThreadInput($currentThread, $foregroundThread, $true)
    }
    if ($windowThread -and $windowThread -ne $currentThread) {
        $attachedWindow = [LocalizationUiSmokeNative]::AttachThreadInput($currentThread, $windowThread, $true)
    }
    try {
        [void][LocalizationUiSmokeNative]::SetWindowPos($windowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
        [void][LocalizationUiSmokeNative]::SetForegroundWindow($windowHandle)
    }
    finally {
        if ($attachedWindow) { [void][LocalizationUiSmokeNative]::AttachThreadInput($currentThread, $windowThread, $false) }
        if ($attachedForeground) { [void][LocalizationUiSmokeNative]::AttachThreadInput($currentThread, $foregroundThread, $false) }
    }
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $foreground = [LocalizationUiSmokeNative]::GetForegroundWindow()
        $foregroundProcessId = 0
        [void][LocalizationUiSmokeNative]::GetWindowThreadProcessId($foreground, [ref]$foregroundProcessId)
        if ($foregroundProcessId -eq $script:activeProcess.Id) { return }
        Start-Sleep -Milliseconds 100
        [void][LocalizationUiSmokeNative]::SetForegroundWindow($windowHandle)
    }
    throw "Could not foreground the app window for PID $($script:activeProcess.Id); screenshot would capture another app."
}

function Capture-Screenshot([string]$Name) {
    Activate-AppWindow
    $rect = $script:activeWindow.Current.BoundingRectangle
    $left = [int][Math]::Max(0, $rect.Left)
    $top = [int][Math]::Max(0, $rect.Top)
    $right = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Right, $rect.Right)
    $bottom = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Bottom, $rect.Bottom)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -lt 1 -or $height -lt 1) { throw "App window is outside the visible desktop during screenshot '$Name'." }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($width, $height)) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $script:scenarioDirectory ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Capture-ElementCloseup([string]$AutomationId, [string]$Name, [int]$Padding = 8) {
    Activate-AppWindow
    $element = Wait-Element $AutomationId
    $rect = $element.Current.BoundingRectangle
    Assert-That ($rect.Width -gt 0 -and $rect.Height -gt 0) "Element '$AutomationId' has no visible bounds for closeup."
    $left = [int][Math]::Max(0, $rect.Left - $Padding)
    $top = [int][Math]::Max(0, $rect.Top - $Padding)
    $right = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Right, $rect.Right + $Padding)
    $bottom = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Bottom, $rect.Bottom + $Padding)
    $bitmap = [Drawing.Bitmap]::new($right - $left, $bottom - $top)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($bitmap.Width, $bitmap.Height)) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $script:scenarioDirectory ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Open-CompactNavigationPane {
    $badge = Find-ElementById 'NavAboutUpdateBadge'
    if ($badge -and -not $badge.Current.IsOffscreen -and $badge.Current.BoundingRectangle.Width -gt 0) { return }
    $toggle = Find-ElementById 'PaneToggleButton'
    if (-not $toggle) {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
        foreach ($candidate in $script:activeWindow.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($candidate.Current.Name -match 'navigation|menu|ナビゲーション|メニュー') { $toggle = $candidate; break }
        }
    }
    Assert-That ($null -ne $toggle) 'Could not locate the compact navigation pane toggle.'
    Invoke-Element $toggle 'open compact navigation pane'
    Start-Sleep -Milliseconds 350
    $badge = Wait-Element 'NavAboutUpdateBadge'
    Assert-That (-not $badge.Current.IsOffscreen) 'Compact navigation pane did not reveal the About update badge.'
}

function Start-Scenario([string]$Language, [string]$Theme) {
    $scenarioName = "$Language-$Theme"
    $script:scenarioDirectory = Join-Path $evidenceRoot $scenarioName
    $script:activeData = Join-Path $script:scenarioDirectory 'data'
    New-Item -ItemType Directory -Path $script:activeData | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:activeData 'language.json'), "{`"Language`":`"$Language`"}", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $script:activeData 'appearance.json'), "{`"Theme`":`"$Theme`"}", [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $artifactRootFull
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($script:activeData)
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw "Could not start scenario $scenarioName." }
    $script:ownedProcesses.Add($process)
    $script:activeProcess = $process
    $script:activeWindow = Wait-MainWindow $process
    Activate-AppWindow
    Start-Sleep -Milliseconds 800
}

function Assert-Contained([string]$AutomationId) {
    $element = Wait-Element $AutomationId
    $item = $element.Current.BoundingRectangle
    $window = $script:activeWindow.Current.BoundingRectangle
    Assert-That ($item.Left -ge ($window.Left - 2) -and $item.Right -le ($window.Right + 2) -and
        $item.Top -ge ($window.Top - 2) -and $item.Bottom -le ($window.Bottom + 2)) `
        "Visible control '$AutomationId' extends outside its window bounds."
}

function Verify-DialogButtons([string]$Language) {
    foreach ($id in @('ArchitectureZoomOut','ArchitectureZoomIn','ArchitectureFit','ArchitectureActualSize','CloseButton')) {
        $button = Wait-Element $id
        Assert-That (-not [string]::IsNullOrWhiteSpace($button.Current.Name)) "Dialog button '$id' has no accessible name."
        if ($Language -eq 'ja' -and $id -in @('ArchitectureFit','CloseButton')) {
            Assert-That ($button.Current.Name -match '[\u3040-\u30ff\u3400-\u9fff]') "Expected Japanese dialog button name for $id; got '$($button.Current.Name)'."
        }
        if ($Language -eq 'en' -and $id -in @('ArchitectureFit','CloseButton')) {
            Assert-That ($button.Current.Name -notmatch '[\u3040-\u30ff\u3400-\u9fff]') "Expected English dialog button name for $id; got '$($button.Current.Name)'."
        }
        Assert-That $button.Current.IsEnabled "Dialog button '$id' is disabled."
    }
}

function Run-Scenario([string]$Language, [string]$Theme) {
    $scenario = "$Language-$Theme"
    Start-Scenario $Language $Theme
    $expectedTitle = if ($Language -eq 'ja') { 'はじめに' } else { 'Introduction' }
    $intro = Assert-Localized 'OnboardingTitle' $Language
    Assert-That ($intro.Current.Name.Contains($expectedTitle)) "Onboarding heading did not match ${Language}: '$($intro.Current.Name)'."
    foreach ($id in @('OnboardingDefer','WizardTerms','WizardPrivacy','WizardAcceptTerms','WizardAcceptPrivacy')) {
        $control = Wait-Element $id
        Assert-That (-not [string]::IsNullOrWhiteSpace($control.Current.Name)) "Onboarding control '$id' has no accessible caption."
        Assert-Contained $id
    }
    Scroll-ToTop 'WizardScroll'
    Capture-Screenshot '01-onboarding-default'
    Add-Result "$scenario onboarding first page captions" 'PASS' $intro.Current.Name

    $wizardDiagram = Wait-Element 'OpenArchitectureViewer'
    Invoke-Element $wizardDiagram 'open onboarding diagram'
    [void](Wait-Element 'ArchitectureViewerDialog')
    $image = Wait-Element 'ArchitectureViewerImage'
    Assert-That (-not [string]::IsNullOrWhiteSpace($image.Current.Name)) 'Enlarged architecture diagram has no accessible name.'
    Verify-DialogButtons $Language
    Capture-Screenshot '02-onboarding-diagram-lightbox'
    Add-Result "$scenario onboarding diagram and lightbox captions" 'PASS' $image.Current.Name
    Invoke-Id 'CloseButton' 'close architecture viewer'
    Start-Sleep -Milliseconds 250
    Assert-That (-not (Find-ElementById 'ArchitectureViewerDialog')) 'Architecture viewer did not close.'

    # Exercise the existing policy dialogs without changing consent or settings.
    Invoke-Id 'WizardTerms' 'open terms dialog'
    $policy = Wait-Element 'PolicyBody'
    $policyText = Get-ElementText $policy
    if ($Language -eq 'ja') {
        Assert-That ($policyText -match '[\u3040-\u30ff\u3400-\u9fff]') 'Terms dialog body is not Japanese.'
    } else {
        Assert-That ($policyText -notmatch '[\u3040-\u30ff\u3400-\u9fff]') 'Terms dialog body contains Japanese in English mode.'
    }
    $policyClose = Wait-Element 'CloseButton'
    Assert-That (-not [string]::IsNullOrWhiteSpace($policyClose.Current.Name)) 'Terms dialog close button has no accessible caption.'
    Capture-Screenshot '03-terms-dialog'
    Invoke-Id 'CloseButton' 'close terms dialog'

    Invoke-Id 'WizardPrivacy' 'open privacy dialog'
    $privacy = Wait-Element 'PolicyBody'
    $privacyText = Get-ElementText $privacy
    if ($Language -eq 'ja') {
        Assert-That ($privacyText -match '[\u3040-\u30ff\u3400-\u9fff]') 'Privacy dialog body is not Japanese.'
    } else {
        Assert-That ($privacyText -notmatch '[\u3040-\u30ff\u3400-\u9fff]') 'Privacy dialog body contains Japanese in English mode.'
    }
    Capture-Screenshot '03-privacy-dialog'
    Invoke-Id 'CloseButton' 'close privacy dialog'

    Resize-Window $MinimumWidth $MinimumHeight
    Assert-Contained 'OnboardingTitle'
    Assert-Contained 'OnboardingDefer'
    Capture-Screenshot '04-onboarding-minimum-window'
    Add-Result "$scenario onboarding minimum-size key controls contained" 'PASS' "${MinimumWidth}x${MinimumHeight}"

    Resize-Window 1160 800
    Invoke-Id 'OnboardingDefer' 'defer onboarding in isolated profile'
    [void](Wait-Element 'NavOverview')
    Resize-Window 1280 820

    $routes = @(
        @{ nav = 'NavOverview'; content = 'GeneralPageTitle'; shot = 'overview' },
        @{ nav = 'NavAgentSettings'; content = 'AgentScopeCommon'; shot = 'agent-settings' },
        @{ nav = 'NavConnections'; content = 'ConnectionScopeCommon'; shot = 'chat-settings' },
        @{ nav = 'NavChat'; content = 'InboxEmptyList'; shot = 'chat-inbox' },
        @{ nav = 'NavHistory'; content = 'ActivityTab'; shot = 'history' },
        @{ nav = 'NavAbout'; content = 'AboutVersion'; shot = 'about' }
    )
    foreach ($route in $routes) {
        $navigation = Assert-Localized $route.nav $Language
        Invoke-Element $navigation "navigate to $($route.nav)"
        $pageContent = Wait-Element $route.content
        if ($pageContent.Current.ControlType -eq [Windows.Automation.ControlType]::Button) {
            Assert-That (-not [string]::IsNullOrWhiteSpace($pageContent.Current.Name)) "Primary control '$($route.content)' has no accessible name."
        } else {
            $pageText = Get-ElementText $pageContent
            if (-not [string]::IsNullOrWhiteSpace($pageText) -and $route.content -ne 'AboutVersion') {
                if ($Language -eq 'ja') {
                    Assert-That ($pageText -match '[\u3040-\u30ff\u3400-\u9fff]') "Expected Japanese page caption for $($route.nav); got '$pageText'."
                } else {
                    Assert-That ($pageText -notmatch '[\u3040-\u30ff\u3400-\u9fff]') "Unexpected Japanese page caption for $($route.nav): '$pageText'."
                }
            }
        }
        Assert-Contained $route.content
        Capture-Screenshot ("05-$($route.shot)-default")
        Add-Result "$scenario $($route.shot) localization and visible anchor" 'PASS' $navigation.Current.Name
        if ($route.shot -eq 'about') {
            $badge = Wait-Element 'NavAboutUpdateBadge'
            Assert-Contained 'NavAboutUpdateBadge'
            Capture-ElementCloseup 'NavAboutUpdateBadge' '17-update-badge-expanded-closeup'
            $noticeAction = Wait-Element 'PortableUpdateNoticeAction'
            $notice = Wait-Element 'PortableUpdateNotice'
            $actionRect = $noticeAction.Current.BoundingRectangle
            $noticeRect = $notice.Current.BoundingRectangle
            Assert-That ($actionRect.Right -le ($noticeRect.Right + 8) -and $actionRect.Left -gt $noticeRect.Left) `
                'Portable update primary action is not aligned at the right edge of its notice.'
            Assert-Contained 'PortableUpdateNoticeAction'
            Capture-Screenshot '18-about-update-notice-default'
            Resize-Window $MinimumWidth $MinimumHeight
            [void](Wait-Element 'PortableUpdateNoticeAction')
            $actionRect = (Find-ElementById 'PortableUpdateNoticeAction').Current.BoundingRectangle
            $noticeRect = (Find-ElementById 'PortableUpdateNotice').Current.BoundingRectangle
            Assert-That ($actionRect.Right -le ($noticeRect.Right + 8) -and $actionRect.Left -gt $noticeRect.Left) `
                'Portable update primary action is not right-aligned at minimum width.'
            Assert-Contained 'PortableUpdateNoticeAction'
            Capture-Screenshot '19-about-update-notice-minimum'
            Resize-Window 1160 800
            Add-Result "$scenario update badge and notice action alignment" 'PASS' 'Expanded pane and default/minimum update notice.'
        }
        Resize-Window $MinimumWidth $MinimumHeight
        [void](Wait-Element $route.content)
        Assert-Contained $route.content
        Capture-Screenshot ("06-$($route.shot)-minimum")
        Add-Result "$scenario $($route.shot) minimum-size anchor contained" 'PASS' "${MinimumWidth}x${MinimumHeight}"
        Resize-Window 1160 800
    }

    Invoke-Id 'NavAgentSettings' 'inspect agent profile settings'
    foreach ($scope in @(
        @{ id = 'AgentScopeCommon'; anchor = 'WorkspaceRoot'; capture = '10-agent-common' },
        @{ id = 'AgentScopeCodex'; anchor = 'AgentModel'; capture = '11-agent-codex' },
        @{ id = 'AgentScopeClaude'; anchor = 'AgentModel'; capture = '12-agent-claude' }
    )) {
        $selectedScope = Select-Scope $scope.id
        Assert-That (-not [string]::IsNullOrWhiteSpace($selectedScope.Current.Name)) "Agent profile scope '$($scope.id)' has no accessible caption."
        [void](Wait-Element $scope.anchor)
        Assert-Contained $scope.anchor
        Capture-Screenshot $scope.capture
        Add-Result "$scenario $($scope.id) localized profile content" 'PASS'
    }

    Invoke-Id 'NavOverview' 'return to General overview shortcuts'
    Start-Sleep -Milliseconds 500
    Scroll-ToTop 'MainContentScroll'
    $startupTile = Wait-Element 'OverviewStartupTile'
    Assert-That (-not [string]::IsNullOrWhiteSpace($startupTile.Current.Name)) 'Startup shortcut has no accessible caption.'
    Invoke-Element $startupTile 'open startup settings from General overview'
    [void](Wait-Element 'StartWithWindows')
    Capture-Screenshot '20-general-startup-shortcut'
    Invoke-Id 'NavOverview' 'restore General overview for auto-connect shortcut'
    Start-Sleep -Milliseconds 500
    Scroll-ToTop 'MainContentScroll'
    $autoConnectTile = Wait-Element 'OverviewAutoConnectTile'
    Assert-That (-not [string]::IsNullOrWhiteSpace($autoConnectTile.Current.Name)) 'Auto-connect shortcut has no accessible caption.'
    Invoke-Element $autoConnectTile 'open auto-connect settings from General overview'
    [void](Wait-Element 'AutoConnectProvider')
    Capture-Screenshot '21-general-autoconnect-shortcut'
    Add-Result "$scenario General startup and auto-connect shortcuts" 'PASS' 'Opened settings only; did not toggle or connect.'

    Invoke-Id 'NavConnections' 'inspect chat provider settings scopes'
    foreach ($scope in @(
        @{ id = 'ConnectionScopeCommon'; anchor = 'ChatApprovalMode'; capture = '13-chat-common' },
        @{ id = 'ConnectionScopeMattermost'; anchor = 'MattermostServerUrl'; capture = '14-chat-mattermost' },
        @{ id = 'ConnectionScopeSlack'; anchor = 'SlackAppToken'; capture = '15-chat-slack' },
        @{ id = 'ConnectionScopeDiscord'; anchor = 'DiscordBotToken'; capture = '16-chat-discord' }
    )) {
        $selectedScope = Select-Scope $scope.id
        Assert-That (-not [string]::IsNullOrWhiteSpace($selectedScope.Current.Name)) "Chat provider scope '$($scope.id)' has no accessible caption."
        [void](Wait-Element $scope.anchor)
        Assert-Contained $scope.anchor
        Capture-Screenshot $scope.capture
        Add-Result "$scenario $($scope.id) localized provider content" 'PASS'
    }

    # Inspect localized notification status and preview affordance without enqueueing an OS toast.
    Invoke-Id 'NavChat' 'open chat inbox notification status'
    $notificationStatus = Find-ElementById 'NotificationStatus'
    $statusText = if ($notificationStatus) { Get-ElementText $notificationStatus } else { '' }
    if ($statusText) {
        if ($Language -eq 'ja') { Assert-That ($statusText -match '[\u3040-\u30ff\u3400-\u9fff]') 'Notification status copy is not Japanese.' }
        else { Assert-That ($statusText -notmatch '[\u3040-\u30ff\u3400-\u9fff]') 'Notification status copy contains Japanese in English mode.' }
    }
    Invoke-Id 'NavAbout' 'open notification diagnostics preview affordance'
    Expand-Element 'AboutDiagnostics'
    $notificationPreview = Assert-Localized 'DiagnosticsTestNotificationButton' $Language
    $notificationStatus = Assert-Localized 'DiagnosticsNotificationStatus' $Language
    Capture-Screenshot '06-diagnostics-notification-preview'
    Add-Result "$scenario notification status and preview affordance" 'PASS' 'No Windows notification banner was requested.'

    # Capture both the regular and enlarged diagram from the main overview when it is present.
    Invoke-Id 'NavOverview' 'return to overview diagram'
    Reveal-Element 'OpenArchitectureViewer'
    $diagramPreview = Find-ElementById 'OpenArchitectureViewer'
    if ($diagramPreview -and -not $diagramPreview.Current.IsOffscreen) {
        Assert-That (-not [string]::IsNullOrWhiteSpace($diagramPreview.Current.Name)) 'Architecture preview has no accessible name.'
    Capture-Screenshot '07-overview-diagram-thumbnail'
        Invoke-Element $diagramPreview 'open overview diagram'
        [void](Wait-Element 'ArchitectureViewerDialog')
        [void](Wait-Element 'ArchitectureViewerImage')
        Verify-DialogButtons $Language
        Capture-Screenshot '08-overview-diagram-lightbox'
        Invoke-Id 'CloseButton' 'close overview architecture viewer'
        Add-Result "$scenario overview diagram thumbnail and lightbox" 'PASS' $diagramPreview.Current.Name
    } else {
        Add-Result "$scenario overview diagram thumbnail and lightbox" 'FAIL' 'Overview architecture preview was not visible.'
    }

    Resize-Window $MinimumWidth $MinimumHeight
    Open-CompactNavigationPane
    Assert-Contained 'NavAboutUpdateBadge'
    Capture-ElementCloseup 'NavAboutUpdateBadge' '22-update-badge-compact-pane-closeup'
    Capture-Screenshot '09-navigation-minimum-window'
    Add-Result "$scenario compact navigation update badge" 'PASS' '500x600 compact pane opened; full badge bounds are in-window.'
    Add-Result "$scenario navigation at minimum window width" 'PASS' "${MinimumWidth}x${MinimumHeight}"
    Stop-Process -Id $script:activeProcess.Id -Force
    $script:activeProcess = $null
    $script:activeWindow = $null
}

try {
    foreach ($language in @('en','ja')) {
        foreach ($theme in @('light','dark')) { Run-Scenario $language $theme }
    }
} catch {
    Add-Result 'Localization UI smoke execution' 'FAIL' $_.Exception.Message
    throw
} finally {
    foreach ($process in $script:ownedProcesses) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
    $summary = [ordered]@{
        createdUtc = [DateTime]::UtcNow.ToString('o')
        artifactRoot = $artifactRootFull
        evidenceDirectory = $evidenceRoot
        results = @($script:results)
    }
    [IO.File]::WriteAllText((Join-Path $evidenceRoot 'results.json'), ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

$failed = @($script:results | Where-Object status -eq 'FAIL')
Write-Host "Evidence: $evidenceRoot"
if ($failed.Count -gt 0) { exit 1 }
