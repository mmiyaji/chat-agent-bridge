param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$EvidenceDirectory,
    [switch]$OnlyRestoredStage,
    [switch]$OnlyJaLight,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$executable = Join-Path $artifactRootFull 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published desktop executable was not found under ArtifactRoot: $executable" }

$evidenceBase = if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    Join-Path (Split-Path -Parent $artifactRootFull) 'release-readiness-ui-evidence'
} else {
    [IO.Path]::GetFullPath($EvidenceDirectory)
}
$artifactPrefix = $artifactRootFull.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$evidencePrefix = $evidenceBase.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($evidenceBase.Equals($artifactRootFull, [StringComparison]::OrdinalIgnoreCase) -or $evidencePrefix.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or $artifactPrefix.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceDirectory must be outside the published application directory.'
}
$testRoot = Join-Path $evidenceBase ('release-readiness-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ReleaseReadinessMouse {
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(System.IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(System.IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(System.IntPtr window);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extraInfo);
    public static bool ActivateWindow(System.IntPtr window) {
        ShowWindow(window, 9);
        BringWindowToTop(window);
        return SetForegroundWindow(window);
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, System.UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, System.UIntPtr.Zero);
    }
}
'@

$script:results = [Collections.Generic.List[object]]::new()
$script:startedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:fatal = $null

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Add-Pass([string]$Name, [string]$Detail = '') {
    $script:results.Add(@{ name = $Name; status = 'PASS'; detail = $Detail })
    Write-Host "PASS $Name"
}

function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId) {
    if ($null -eq $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-RawElement([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId) {
    if ($null -eq $Scope) { return $null }
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [Collections.Generic.Stack[Windows.Automation.AutomationElement]]::new()
    $first = $walker.GetFirstChild($Scope)
    while ($first) {
        $pending.Push($first)
        $first = $walker.GetNextSibling($first)
    }
    while ($pending.Count -gt 0) {
        $element = $pending.Pop()
        if ($element.Current.AutomationId -eq $AutomationId) { return $element }
        $child = $walker.GetFirstChild($element)
        while ($child) {
            $pending.Push($child)
            $child = $walker.GetNextSibling($child)
        }
    }
    return $null
}

function Wait-Element([Windows.Automation.AutomationElement]$Scope, [string]$AutomationId, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $Scope $AutomationId
        if ($element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for AutomationId '$AutomationId'."
}

function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not $Element.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not $Element.Current.IsEnabled) { throw "UI element is disabled: $Description" }
    try {
        $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        ([Windows.Automation.InvokePattern]$pattern).Invoke()
    }
    catch {
        $bounds = $Element.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -lt 1 -or $bounds.Height -lt 1) { throw "UI element has no usable bounds: $Description" }
        $point = [Windows.Point]::new($bounds.Left + ($bounds.Width / 2), $bounds.Top + ($bounds.Height / 2))
        [ReleaseReadinessMouse]::Click([int]$point.X, [int]$point.Y)
    }
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

function Wait-MainWindow([Diagnostics.Process]$Process, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($Process.HasExited) { throw "Desktop process exited during startup with code $($Process.ExitCode)." }
        $window = Get-MainWindow $Process
        if ($window) { return $window }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'No accessible desktop window appeared. This UI smoke requires an interactive Windows desktop.'
}

function Start-TestApp([string]$DataDirectory) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $artifactRootFull
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($DataDirectory)
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Desktop process could not be started.' }
    $script:startedProcesses.Add($process)
    $window = Wait-MainWindow $process
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if ($handle -ne [IntPtr]::Zero) { [ReleaseReadinessMouse]::ActivateWindow($handle) | Out-Null }
    try { $window.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 300
    $script:activeDataDirectory = $DataDirectory
    return $window
}

function Capture-Screenshot([string]$Name) {
    if (-not $script:activeWindow) { throw 'No app window is available for screenshot capture.' }
    $windowBounds = $script:activeWindow.Current.BoundingRectangle
    $left = [int][Math]::Max(0, $windowBounds.Left)
    $top = [int][Math]::Max(0, $windowBounds.Top)
    $right = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Right, $windowBounds.Right)
    $bottom = [int][Math]::Min([Windows.Forms.SystemInformation]::VirtualScreen.Bottom, $windowBounds.Bottom)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -lt 1 -or $height -lt 1) {
        Write-Warning "Skipped screenshot '$Name'; app window bounds are $($windowBounds.Left),$($windowBounds.Top) $($windowBounds.Width)x$($windowBounds.Height)."
        return
    }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($width, $height)) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $testRoot $Name), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Expand-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    if ($Element.Current.IsOffscreen) { Scroll-ToElement $Element }
    try {
        $pattern = [Windows.Automation.ExpandCollapsePattern]$Element.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($pattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) { return }
    }
    catch [InvalidOperationException] { }
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($Element)
    while ($child) {
        if ($child.Current.ControlType -eq [Windows.Automation.ControlType]::Button) {
            try {
                $invoke = [Windows.Automation.InvokePattern]$child.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
                if ($child.Current.IsEnabled) { $invoke.Invoke(); Start-Sleep -Milliseconds 250; return }
            }
            catch [InvalidOperationException] { }
        }
        $child = $walker.GetNextSibling($child)
    }
    try {
        $bounds = $Element.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -lt 1 -or $bounds.Height -lt 1) { throw 'Element has no usable bounds.' }
        $point = [Windows.Point]::new($bounds.Left + ($bounds.Width / 2), $bounds.Top + [Math]::Min(24, $bounds.Height / 4))
        [ReleaseReadinessMouse]::Click([int]$point.X, [int]$point.Y)
    }
    catch { throw "Could not expand ${Description}: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 250
}

function Scroll-ToElement([Windows.Automation.AutomationElement]$Element) {
    $window = $script:activeWindow
    $scroll = Find-Element $window 'MainContentScroll'
    if (-not $scroll -or $scroll.Current.IsOffscreen) { $scroll = Find-Element $window 'AboutScroll' }
    if (-not $scroll) { return }
    try {
        $pattern = [Windows.Automation.ScrollPattern]$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
        for ($i = 0; $i -lt 8 -and $Element.Current.IsOffscreen; $i++) {
            $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
            Start-Sleep -Milliseconds 100
        }
    }
    catch [InvalidOperationException] { }
}

function Scroll-MainContentDown([int]$Increments = 1) {
    $scroll = Find-Element $script:activeWindow 'MainContentScroll'
    if (-not $scroll) { return }
    try {
        $pattern = [Windows.Automation.ScrollPattern]$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern
        )
        for ($i = 0; $i -lt $Increments; $i++) {
            $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
            Start-Sleep -Milliseconds 150
        }
    }
    catch [InvalidOperationException] { }
}

function Close-WithConfirmation([Windows.Automation.AutomationElement]$Window, [Diagnostics.Process]$Process) {
    Invoke-Element (Wait-Element $Window 'NavAbout') 'About navigation'
    Invoke-Element (Wait-Element $Window 'AboutExit') 'explicit exit'
    $confirmation = Wait-Element $Window 'WindowCloseConfirmation' 10
    Invoke-Element (Wait-Element $confirmation 'PrimaryButton' 5) 'exit confirmation'
    if (-not $Process.WaitForExit(15000)) { throw 'Application did not close after explicit exit confirmation.' }
    Add-Pass 'Explicit exit confirmation closes app cleanly'
}

function Run-RestoredStageScenario() {
    $data = Join-Path $testRoot 'data-restored-stage'
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $helper = Join-Path $PSScriptRoot 'test-support/PortableUpdateResumeSmoke/PortableUpdateResumeSmoke.csproj'
    $prepared = & dotnet run --project $helper --configuration Release --no-build -- prepare $artifactRootFull $data
    if ($LASTEXITCODE -ne 0) { throw "Could not create a validated staged-update fixture: $($prepared -join ' ')" }
    $fixture = ($prepared -join "`n") | ConvertFrom-Json
    try {
        $script:activeWindow = Start-TestApp $data
        $process = $script:startedProcesses[$script:startedProcesses.Count - 1]
        $dialog = Find-Element $script:activeWindow 'PortableUpdateApplyConfirmation'
        if (-not $dialog) {
            Invoke-Element (Wait-Element $script:activeWindow 'NavAbout') 'About navigation for restored update'
            $dialog = Wait-Element $script:activeWindow 'PortableUpdateApplyConfirmation' 20
        }
        if (-not (Wait-Element $dialog 'PrimaryButton').Current.IsEnabled) { throw 'Restored-update prompt Apply action remained disabled after the prompt loaded.' }
        Capture-Screenshot 'restored-stage-first-prompt.png'
        Add-Pass 'Persisted stage prompts after restart'
        Invoke-Element (Wait-Element $dialog 'CloseButton') 'choose Later for restored update'
        Wait-ForAbsent $script:activeWindow 'PortableUpdateApplyConfirmation'
        if (-not (Find-Element $script:activeWindow 'PortableApplyUpdate')) { Invoke-Element (Wait-Element $script:activeWindow 'NavAbout') 'return to portable updates after Later' }
        if (-not (Wait-Enabled $script:activeWindow 'PortableApplyUpdate' 10)) { throw 'Apply was not available after choosing Later.' }
        Add-Pass 'Later preserves ready-to-apply state'
        Close-WithConfirmation $script:activeWindow $process

        $script:activeWindow = Start-TestApp $data
        $process = $script:startedProcesses[$script:startedProcesses.Count - 1]
        $dialog = Find-Element $script:activeWindow 'PortableUpdateApplyConfirmation'
        if (-not $dialog) {
            Invoke-Element (Wait-Element $script:activeWindow 'NavAbout') 'About navigation after relaunch'
            $dialog = Wait-Element $script:activeWindow 'PortableUpdateApplyConfirmation' 20
        }
        if (-not (Wait-Element $dialog 'PrimaryButton').Current.IsEnabled) { throw 'Restored-update prompt Apply action remained disabled after the relaunch prompt loaded.' }
        Capture-Screenshot 'restored-stage-relaunch-prompt.png'
        Add-Pass 'Persisted stage prompts again after Later and relaunch'
        Invoke-Element (Wait-Element $dialog 'CloseButton') 'choose Later after relaunch'
        Wait-ForAbsent $script:activeWindow 'PortableUpdateApplyConfirmation'
        if (-not (Find-Element $script:activeWindow 'PortableApplyUpdate')) { Invoke-Element (Wait-Element $script:activeWindow 'NavAbout') 'return to portable updates after second Later' }
        if (-not (Wait-Enabled $script:activeWindow 'PortableApplyUpdate' 10)) { throw 'Apply was not available after the second Later choice.' }
        Add-Pass 'Second Later leaves Apply available; staged payload was never executed'
        Close-WithConfirmation $script:activeWindow $process
    }
    finally {
        foreach ($process in $script:startedProcesses) {
            try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null } } catch { }
        }
        & dotnet run --project $helper --configuration Release --no-build -- cleanup $artifactRootFull $data $fixture.work
        if ($LASTEXITCODE -ne 0) { throw 'The validated staged-update fixture could not be cleaned through its owned work path.' }
        if (Test-Path -LiteralPath $data) {
            $dataFull = [IO.Path]::GetFullPath($data)
            $testRootFull = [IO.Path]::GetFullPath($testRoot)
            if (-not [string]::Equals([IO.Path]::GetDirectoryName($dataFull), $testRootFull, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($dataFull) -ne 'data-restored-stage') {
                throw 'Refusing to remove restored-stage data outside its exact test-owned directory.'
            }
            Assert-NoReparsePointTree $dataFull
            Remove-Item -LiteralPath $dataFull -Recurse -Force
        }
    }
}

function Wait-ForAbsent([Windows.Automation.AutomationElement]$Window, [string]$AutomationId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $element = Find-Element $Window $AutomationId
        if (-not $element -or $element.Current.IsOffscreen) { return }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Dialog '$AutomationId' did not close."
}

function Wait-Enabled([Windows.Automation.AutomationElement]$Window, [string]$AutomationId, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-Element $Window $AutomationId
        if ($element -and $element.Current.IsEnabled) { return $true }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Assert-NoReparsePointTree([string]$Directory) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Directory))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing cleanup because a test-owned path is a reparse point: $current" }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing cleanup because a test-owned child is a reparse point: $($child.FullName)" }
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
    }
}

function Write-CompletedFixture([string]$DataDirectory, [string]$Language, [string]$Theme, [string]$PolicyVersion) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $DataDirectory; AgentExecutable = 'unused'; AgentArguments = ''
        AgentMode = 'exec'; AgentProvider = 'custom'; AutoConnectProvider = 'none';
        TrustedWorkspaceRoots = $DataDirectory; AgentPermissionMode = 'inherit';
        ChatApprovalMode = 'always'; ChatResultMode = 'manual'; FileAttachmentApprovalMode = 'always'
    }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{
        PolicyVersion = $PolicyVersion; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language = $Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme = $Theme }
    Disable-AutomaticUpdateChecks $DataDirectory
}

function Disable-AutomaticUpdateChecks([string]$DataDirectory) {
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{
        schemaVersion = 1; automaticChecksEnabled = $false; includePrereleases = $true
        lastAttemptUtc = $null; availableRelease = $null
    }
}

function Run-CompletedScenario([string]$Language, [string]$Theme, [string]$ExpectedTitle) {
    $scenario = "$Language-$Theme"
    $data = Join-Path $testRoot ("data-" + $scenario)
    Write-CompletedFixture $data $Language $Theme $script:policyVersion
    $script:activeWindow = Start-TestApp $data
    $process = $script:startedProcesses[$script:startedProcesses.Count - 1]

    $wizard = Find-Element $script:activeWindow 'WizardHost'
    if ($wizard -and -not $wizard.Current.IsOffscreen) { throw "Completed onboarding fixture unexpectedly opened the wizard for $scenario." }
    Add-Pass "Completed onboarding fixture skips wizard ($scenario)"

    Invoke-Element (Wait-Element $script:activeWindow 'NavOverview') 'General navigation'
    $title = Wait-Element $script:activeWindow 'GeneralPageTitle'
    if ($title.Current.Name -ne $ExpectedTitle) { throw "Expected '$ExpectedTitle' for $scenario, got '$($title.Current.Name)'." }
    Add-Pass "General page localization ($scenario): $ExpectedTitle"

    $dataGroup = Wait-Element $script:activeWindow 'DataManagementGroup'
    Expand-Element $dataGroup 'data management group'
    Scroll-MainContentDown 2
    Capture-Screenshot "${scenario}-data-management.png"
    $location = Find-RawElement $script:activeWindow 'DataManagementLocation'
    if (-not $location) { $location = Wait-Element $script:activeWindow 'DataManagementLocation' 5 }
    if ($location.Current.Name -notlike "*$data*") { throw "Data-management location did not point to the isolated data directory for $scenario." }
    Add-Pass "Data management group and isolated data path ($scenario)"

    $themeId = if ($Theme -eq 'light') { 'ThemeLight' } else { 'ThemeDark' }
    $themeControl = Wait-Element $script:activeWindow $themeId
    $selectionPattern = [Windows.Automation.SelectionItemPattern]$themeControl.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    if (-not $selectionPattern.Current.IsSelected) { throw "Expected $Theme theme selection for $scenario." }
    Add-Pass "Appearance preference applied ($scenario)"

    Invoke-Element (Wait-Element $script:activeWindow 'NavAbout') 'About navigation'
    $diagnostics = Wait-Element $script:activeWindow 'AboutDiagnostics'
    Expand-Element $diagnostics 'About diagnostics expander'
    Wait-Element $script:activeWindow 'DiagnosticsVersion' | Out-Null
    Add-Pass "About diagnostics renders ($scenario)"

    Invoke-Element (Wait-Element $script:activeWindow 'AboutThirdPartyLicenses') 'third-party licenses'
    $licenseDialog = Wait-Element $script:activeWindow 'ThirdPartyLicensesDialog' 10
    $licenseBody = Wait-Element $licenseDialog 'ThirdPartyLicenseBody' 5
    $licenseText = $licenseBody.Current.Name
    if ([string]::IsNullOrWhiteSpace($licenseText)) {
        try {
            $textPattern = [Windows.Automation.TextPattern]$licenseBody.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern)
            $licenseText = $textPattern.DocumentRange.GetText(256)
        }
        catch [InvalidOperationException] { }
    }
    if ([string]::IsNullOrWhiteSpace($licenseText)) { throw "Bundled third-party license text was empty for $scenario." }
    Capture-Screenshot "${scenario}-licenses.png"
    $closeButton = Find-Element $licenseDialog 'CloseButton'
    if (-not $closeButton) {
        $buttons = $licenseDialog.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
        foreach ($button in $buttons) { if ($button.Current.Name -match 'Close|閉じる') { $closeButton = $button; break } }
    }
    if (-not $closeButton) { throw 'Third-party licenses dialog has no accessible close button.' }
    Invoke-Element $closeButton 'close third-party licenses'
    Add-Pass "Bundled third-party licenses open and close ($scenario)"

    Close-WithConfirmation $script:activeWindow $process
}

try {
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion from OnboardingConsentStore.cs.' }
    $script:policyVersion = $policyMatch.Groups[1].Value

    if ($OnlyJaLight) {
        Run-CompletedScenario 'ja' 'light' '全般'
    }
    elseif (-not $OnlyRestoredStage) {
        # First launch: no settings or consent fixture exists. Verify consent is required before deferring.
        $wizardData = Join-Path $testRoot 'data-first-launch'
        New-Item -ItemType Directory -Force -Path $wizardData | Out-Null
        Disable-AutomaticUpdateChecks $wizardData
        $script:activeWindow = Start-TestApp $wizardData
        $wizardTitle = Wait-Element $script:activeWindow 'OnboardingTitle'
        if (-not $wizardTitle.Current.Name) { throw 'First-launch onboarding wizard title is empty.' }
        if (Test-Path -LiteralPath (Join-Path $wizardData 'settings.json')) { throw 'First launch unexpectedly created settings before user input.' }
        $terms = Wait-Element $script:activeWindow 'WizardAcceptTerms'
        $privacy = Wait-Element $script:activeWindow 'WizardAcceptPrivacy'
        $next = Wait-Element $script:activeWindow 'OnboardingNext'
        if (-not $terms.Current.IsEnabled -or -not $privacy.Current.IsEnabled) { throw 'Consent controls are not available on first launch.' }
        if (-not $next.Current.IsEnabled) { throw 'Onboarding Next should be actionable so it can report the missing consent requirement.' }
        ([Windows.Automation.InvokePattern]$next.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $status = Wait-Element $script:activeWindow 'WizardStatus'
        Start-Sleep -Milliseconds 250
        $wizardTitleAfterAttempt = Wait-Element $script:activeWindow 'OnboardingTitle'
        if ($wizardTitleAfterAttempt.Current.Name -ne $wizardTitle.Current.Name) { throw 'Wizard advanced without the required consent.' }
        $statusDescendants = $status.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $statusNames = @($status.Current.Name) + @($statusDescendants | ForEach-Object { $_.Current.Name })
        if (-not ($statusNames -contains 'Agreement to the terms is required' -or $statusNames -contains '利用条件への同意が必要です')) { throw "Missing-consent status did not expose localized text: '$($statusNames -join ' | ')'" }
        if (Test-Path -LiteralPath (Join-Path $wizardData 'consent.json')) { throw 'Missing-consent attempt unexpectedly persisted consent.' }
        Add-Pass 'First-launch wizard has no settings and blocks progress without consent'
        Capture-Screenshot 'first-launch-wizard.png'
        Invoke-Element (Wait-Element $script:activeWindow 'OnboardingDefer') 'defer onboarding'
        Wait-Element $script:activeWindow 'NavAbout' | Out-Null
        $process = $script:startedProcesses[$script:startedProcesses.Count - 1]
        Close-WithConfirmation $script:activeWindow $process

        Run-CompletedScenario 'ja' 'light' '全般'
        Run-CompletedScenario 'ja' 'dark' '全般'
        Run-CompletedScenario 'en' 'light' 'General'
        Run-CompletedScenario 'en' 'dark' 'General'
    }
    if (-not $OnlyJaLight) { Run-RestoredStageScenario }
    Add-Pass 'Release-readiness UI smoke completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name = 'Release-readiness UI smoke'; status = 'FAIL'; detail = $script:fatal })
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        }
        catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() }
    catch { Write-Warning "Could not save UI smoke results: $_" }
}

if ($script:fatal) { throw $script:fatal }
Write-Output "UI smoke artifacts: $testRoot"
