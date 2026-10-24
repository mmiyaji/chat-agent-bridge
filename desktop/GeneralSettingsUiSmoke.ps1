# Focused native UIA smoke for the General settings overview and close behavior.
# Each matrix case uses its own portable --data-dir and artifacts/<guid> profile.
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('both', 'en', 'ja')][string]$Language = 'both',
    [ValidateSet('both', 'light', 'dark')][string]$Theme = 'both'
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory)) { throw 'AppDirectory must be an absolute path.' }
$appRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AppDirectory).Path)
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
    throw 'Use an isolated unpackaged portable publish under desktop/artifacts.'
}
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Desktop executable not found: $exe" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GeneralSettingsSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
'@

$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
New-Item -ItemType Directory -Force -Path $script:root | Out-Null
$script:results = [Collections.Generic.List[object]]::new()
$script:app = $null
$script:window = $null
$script:data = $null

function Save-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Pass([string]$Name, [string]$Detail = '') {
    $script:results.Add(@{ name = $Name; status = 'PASS'; detail = $Detail })
    Write-Output "PASS $Name"
}
function Fail([string]$Name, [string]$Detail) {
    $script:results.Add(@{ name = $Name; status = 'FAIL'; detail = $Detail })
    Write-Output "FAIL $Name : $Detail"
}
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    if (-not $Scope) { return $null }
    $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Element([string]$Id) { Find-Element $script:window $Id }
function Wait-Element([string]$Id, [int]$Seconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Element $Id
        if ($element) { return $element }
        if ($script:app -and $script:app.HasExited) { throw "App exited while waiting for $Id." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for UIA element '$Id'."
}
function Wait-Visible([string]$Id) {
    $until = [DateTime]::UtcNow.AddSeconds(12)
    do {
        $element = Element $Id
        if ($element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Width -gt 0) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "UIA element '$Id' did not become visible."
}
function Invoke-Element([string]$Id) {
    $element = Wait-Element $Id
    if (-not $element.Current.IsEnabled) { throw "UIA element '$Id' is disabled." }
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke()
    } elseif ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select()
    } else { throw "UIA element '$Id' has no InvokePattern or SelectionItemPattern." }
    Start-Sleep -Milliseconds 180
}
function Expand-Element([string]$Id, [bool]$Expand) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            if ($child.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) { break }
        }
        if (-not $pattern) { throw "UIA element '$Id' has no ExpandCollapsePattern." }
    }
    $expandPattern = [Windows.Automation.ExpandCollapsePattern]$pattern
    $desired = if ($Expand) { [Windows.Automation.ExpandCollapseState]::Expanded } else { [Windows.Automation.ExpandCollapseState]::Collapsed }
    if ($expandPattern.Current.ExpandCollapseState -ne $desired) {
        if ($Expand) { $expandPattern.Expand() } else { $expandPattern.Collapse() }
    }
}
function Scroll-To([string]$Id) {
    $target = Wait-Element $Id
    $scroll = Wait-Element 'MainContentScroll'
    $pattern = $null
    if (-not $scroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { throw 'MainContentScroll lacks ScrollPattern.' }
    $scrollPattern = [Windows.Automation.ScrollPattern]$pattern
    if ($scrollPattern.Current.VerticallyScrollable) {
        $scrollPattern.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 0)
        Start-Sleep -Milliseconds 120
    }
    for ($i = 0; $i -lt 30; $i++) {
        $target = Wait-Element $Id 2
        $bounds = $target.Current.BoundingRectangle
        $frame = $script:window.Current.BoundingRectangle
        if (-not $bounds.IsEmpty -and $bounds.Width -gt 0 -and $bounds.Top -ge $frame.Top + 48 -and
            $bounds.Top + [Math]::Min($bounds.Height, 64) -le $frame.Bottom - 16) { return $target }
        if (-not $scrollPattern.Current.VerticallyScrollable) { break }
        if (-not $bounds.IsEmpty -and $bounds.Top -lt $frame.Top + 48) {
            $scrollPattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallDecrement)
        } else {
            $scrollPattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Could not scroll '$Id' into view."
}
function Wait-Preference([string]$Behavior) {
    $path = Join-Path $script:data 'background.json'
    $until = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if (Test-Path -LiteralPath $path) {
            try { $stored = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; if ($stored.CloseBehavior -eq $Behavior) { return } } catch { }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "background.json did not persist CloseBehavior=$Behavior."
}
function Set-CloseBehavior([int]$Index, [string]$Name) {
    $combo = Wait-Element 'CloseBehaviorComboBox'
    $comboPattern = $null
    if (-not $combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$comboPattern)) { throw 'Close behavior combo cannot expand.' }
    ([Windows.Automation.ExpandCollapsePattern]$comboPattern).Expand()
    Start-Sleep -Milliseconds 150
    $ids = @('CloseBehaviorAskEveryTime', 'CloseBehaviorMoveToTray', 'CloseBehaviorExitApp')
    $item = Wait-Element $ids[$Index]
    $select = $null
    if (-not $item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) { throw "No SelectionItemPattern for $($ids[$Index])." }
    ([Windows.Automation.SelectionItemPattern]$select).Select()
    Start-Sleep -Milliseconds 250
    Wait-Preference $Name
}
function Capture([string]$Name) {
    $path = Join-Path $script:caseRoot $Name
    $windowHandle = [IntPtr]$script:window.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) { throw 'Cannot capture screenshot: app window handle is invalid.' }
    & winapp ui screenshot -w $windowHandle -o $path --json | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $path)) { throw "Screenshot capture failed: $Name" }
}
function Start-Case([string]$Locale, [string]$ColorTheme) {
    $script:caseRoot = Join-Path $script:root "$Locale-$ColorTheme"
    $script:data = Join-Path $script:caseRoot 'profile'
    New-Item -ItemType Directory -Force -Path $script:data | Out-Null
    $workspace = Join-Path $script:data 'workspace'
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null
    Save-Json (Join-Path $script:data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'codex'; AgentMode = 'exec'
        AgentExecutable = 'codex-smoke-placeholder.exe'; AgentArguments = ''; AgentModel = 'gpt-smoke-model'
        AgentReasoningEffort = 'high'; AgentPermissionMode = 'inherit'; TrustedWorkspaceRoots = $workspace
        RequireApproval = $true; AllowAgentNetwork = $false; AutoConnectProvider = 'none'
    }
    Save-Json (Join-Path $script:data 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    $policy = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read current onboarding consent policy version.'
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = $Locale }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = $ColorTheme }
    Save-Json (Join-Path $script:data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false; LastAttemptUtc = $null; AvailableRelease = $null }
    Save-Json (Join-Path $script:data 'pricing-auto.json') @{ schemaVersion = 1; autoUpdateEnabled = $false; catalogJson = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw) }
    $start = [Diagnostics.ProcessStartInfo]::new($script:exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($script:data)
    $script:app = [Diagnostics.Process]::Start($start)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $until = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $windows) { if ($candidate.Current.Name -eq 'Chat Agent Bridge' -and $candidate.Current.NativeWindowHandle -ne 0) { $script:window = $candidate; break } }
        if ($script:window) { break }
        if ($script:app.HasExited) { throw "Portable app exited during startup ($($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $until)
    if (-not $script:window) { throw 'No accessible window appeared for isolated profile.' }
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [GeneralSettingsSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    if (-not [GeneralSettingsSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30, [int](1160 * $scale), [int](800 * $scale), 0x0014)) { throw 'Could not size the app to 1160 x 800 DIPs.' }
    Wait-Element 'NavOverview' | Out-Null
    Invoke-Element 'NavOverview'
}
function Stop-OwnedApp([switch]$Force) {
    if ($script:app -and -not $script:app.HasExited) {
        if ($Force) { try { $script:app.Kill($true) } catch { } }
        else {
            # The settings action performs the app's graceful shutdown even when
            # the selected title-bar behavior is to hide in the tray.
            $exitButton = Element 'ExitCompletelyButton'
            if ($exitButton -and $exitButton.Current.IsEnabled) {
                $invoke = $null
                if ($exitButton.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
                    ([Windows.Automation.InvokePattern]$invoke).Invoke()
                }
            } else {
                [void][GeneralSettingsSmokeNative]::PostMessage([IntPtr]$script:window.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            }
        }
        try { $script:app.WaitForExit(7000) | Out-Null } catch { }
    }
    $script:app = $null; $script:window = $null
}
function Wait-WindowVisibility([bool]$Visible, [int]$Seconds = 8) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($Visible -and [GeneralSettingsSmokeNative]::IsWindowVisible([IntPtr]$script:window.Current.NativeWindowHandle)) { return }
        if (-not $Visible -and -not [GeneralSettingsSmokeNative]::IsWindowVisible([IntPtr]$script:window.Current.NativeWindowHandle)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Window visibility did not become $Visible."
}
function Close-OwnedWindow {
    $handle = [IntPtr]$script:window.Current.NativeWindowHandle
    $ownerId = [uint32]0
    [void][GeneralSettingsSmokeNative]::GetWindowThreadProcessId($handle, [ref]$ownerId)
    if ($ownerId -ne [uint32]$script:app.Id) { throw 'Refusing to close a window not owned by the isolated app.' }
    [void][GeneralSettingsSmokeNative]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
}

$locales = if ($Language -eq 'both') { @('en', 'ja') } else { @($Language) }
$themes = if ($Theme -eq 'both') { @('light', 'dark') } else { @($Theme) }
$script:exe = $exe
try {
    foreach ($locale in $locales) {
        foreach ($colorTheme in $themes) {
            try {
                Start-Case $locale $colorTheme
                $expectedNav = if ($locale -eq 'ja') { '全般' } else { 'General' }
                Assert ((Wait-Element 'NavOverview').Current.Name -eq $expectedNav) "General navigation did not localize for $locale."
                $details = Wait-Element 'OverviewDetailsExpander'
                $detailsPattern = $details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
                Assert ($detailsPattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) 'Setup details should start expanded.'
                Assert ((Element 'StartOnboarding') -and (Element 'SetupReloadSettings')) 'Setup reload and onboarding actions are missing.'
                Assert ((Element 'OverviewTerms') -and (Element 'OverviewPrivacy')) 'Terms and privacy links are missing.'
                Pass "$locale/$colorTheme General overview, expanded setup details, actions, and legal links"
                Capture 'general-overview.png'
                Scroll-To 'OverviewTerms' | Out-Null
                Capture 'setup-actions.png'
                Invoke-Element 'OverviewTerms'
                Wait-Element 'PolicyDialog' | Out-Null
                Assert ((Element 'PolicyBody')) 'Terms dialog body is missing.'
                $policyClose = Find-Element $script:window 'CloseButton'
                if (-not $policyClose) {
                    $all = $script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
                    foreach ($candidate in $all) { if ($candidate.Current.Name -match '^(Close|閉じる)$') { $policyClose = $candidate; break } }
                }
                Assert ($policyClose) 'Terms dialog close button was not found.'
                $policyCloseInvoke = $null
                Assert ($policyClose.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$policyCloseInvoke)) 'Terms dialog close button cannot be invoked.'
                ([Windows.Automation.InvokePattern]$policyCloseInvoke).Invoke()
                Start-Sleep -Milliseconds 250
                Assert (-not (Element 'PolicyDialog')) 'Terms dialog did not close.'
                Pass "$locale/$colorTheme terms link opens a dismissible policy dialog"

                $notif = Scroll-To 'PortableIdentityExpander'
                Assert ((Element 'PortableIdentityExpander').Current.IsOffscreen -eq $false) 'Approval notifications expander is not in view.'
                Expand-Element 'PortableIdentityExpander' $true
                $diag = Wait-Element 'NotificationDiagnostics'
                $diagPattern = $diag.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
                Assert ($diagPattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) 'Notification diagnostics should start collapsed.'
                Assert ((Element 'PortableIdentityStatus')) 'Additional registration status is missing.'
                Pass "$locale/$colorTheme approval notification purpose is visible and diagnostics stay collapsed"
                Capture 'notifications-expanded.png'

                Expand-Element 'SavedSettingsDetails' $false
                $savedAgent = Scroll-To 'SavedAgentCard'
                $savedWorkspace = Wait-Element 'SavedWorkspaceCard'
                Assert ((Wait-Element 'SavedAgentName').Current.Name -eq 'Codex') 'Saved agent name is not reflected in the overview.'
                Assert ((Wait-Element 'SavedModel').Current.Name -eq 'gpt-smoke-model') 'Saved model is not reflected in the overview.'
                Assert ((Wait-Element 'SavedReasoning').Current.Name -eq 'high') 'Saved reasoning is not reflected in the overview.'
                Assert ((Wait-Element 'SavedWorkspace').Current.Name -eq (Join-Path $script:data 'workspace')) 'Saved workspace is not reflected in the overview.'
                Pass "$locale/$colorTheme saved agent, model, reasoning, and workspace are displayed"
                Invoke-Element 'SavedAgentEdit'
                Wait-Element 'AgentScopeList' | Out-Null
                Wait-Visible 'AgentExecutable' | Out-Null
                Invoke-Element 'NavOverview'
                Invoke-Element 'SavedWorkspaceEdit'
                Wait-Visible 'WorkspaceRoot' | Out-Null
                Invoke-Element 'NavOverview'
                Expand-Element 'SavedSettingsDetails' $true
                Scroll-To 'SavedAgentCard' | Out-Null
                Assert ((Wait-Element 'SavedExecutable').Current.Name -eq 'codex-smoke-placeholder.exe') 'Saved executable detail is missing.'
                Assert ((Wait-Element 'SettingsLocation').Current.Name -eq $script:data) 'Settings location detail does not show the isolated profile.'
                Pass "$locale/$colorTheme saved cards navigate to their settings and technical details expand"
                Capture 'saved-settings-expanded.png'

                # Verify title-bar close cancellation without changing the saved Ask choice.
                $background = Join-Path $script:data 'background.json'
                Assert (-not (Test-Path -LiteralPath $background)) 'Fresh profile should omit background.json and default to Ask every time.'
                $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
                Close-OwnedWindow
                $closeDialog = Wait-Element 'WindowCloseConfirmation' 15
                Assert (-not $script:app.HasExited) 'Cancel-close confirmation unexpectedly exited the app.'
                $cancel = Find-Element $script:window 'CloseButton'
                if (-not $cancel) { $cancel = Find-Element $script:window 'WindowCloseConfirmationCancel' }
                if (-not $cancel) {
                    $all = $script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
                    foreach ($candidate in $all) { if ($candidate.Current.Name -match '^(Cancel|キャンセル)$') { $cancel = $candidate; break } }
                }
                Assert ($cancel) 'Close confirmation cancel button was not found.'
                $invoke = $null
                Assert ($cancel.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) 'Close confirmation cancel button is not invokable.'
                ([Windows.Automation.InvokePattern]$invoke).Invoke()
                Start-Sleep -Milliseconds 300
                Assert (-not $script:app.HasExited -and [GeneralSettingsSmokeNative]::IsWindowVisible($hwnd)) 'Cancel did not leave the app window visible.'
                Assert (-not (Test-Path -LiteralPath $background)) 'Cancel changed the omitted default close preference.'
                Pass "$locale/$colorTheme title-bar close asks and cancel leaves the app alive with default preference"
                Capture 'close-cancelled.png'

                # Check the 500 x 600 DIP minimum and visible control bounds.
                $scale = [GeneralSettingsSmokeNative]::GetDpiForWindow($hwnd) / 96.0
                [void][GeneralSettingsSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30, [int](500 * $scale), [int](600 * $scale), 0x0014)
                Start-Sleep -Milliseconds 250
                $windowRect = $script:window.Current.BoundingRectangle
                Assert ($windowRect.Width -ge (490 * $scale) -and $windowRect.Height -ge (590 * $scale)) 'Window did not retain approximately 500 x 600 DIPs.'
                $closeCombo = Scroll-To 'CloseBehaviorComboBox'
                $comboRect = $closeCombo.Current.BoundingRectangle
                Assert ($comboRect.Left -ge $windowRect.Left -and $comboRect.Right -le $windowRect.Right) 'Close behavior control bounds exceed the narrow window.'
                Capture 'narrow-500x600.png'
                Pass "$locale/$colorTheme window and close behavior control fit at 500 x 600 DIPs"
                Scroll-To 'SavedAgentCard' | Out-Null
                Capture 'saved-settings-narrow.png'
                Scroll-To 'OverviewTerms' | Out-Null
                Capture 'setup-actions-narrow.png'
                Scroll-To 'MoveToTrayButton' | Out-Null
                Capture 'tray-actions-narrow.png'
                Scroll-To 'CloseBehaviorComboBox' | Out-Null

                Set-CloseBehavior 1 'MoveToSystemTray'
                Pass "$locale/$colorTheme tray close behavior persists"
                Stop-OwnedApp
                Start-Case $locale $colorTheme
                $saved = Get-Content -LiteralPath (Join-Path $script:data 'background.json') -Raw | ConvertFrom-Json
                Assert ($saved.CloseBehavior -eq 'MoveToSystemTray') 'Tray close behavior did not survive restart.'
                $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
                Close-OwnedWindow
                Wait-WindowVisibility $false
                Assert (-not $script:app.HasExited) 'Move to tray closed the process.'
                [void][GeneralSettingsSmokeNative]::ShowWindow($hwnd, 9)
                Wait-WindowVisibility $true
                Pass "$locale/$colorTheme tray mode hides on close and restores its own window"

                Set-CloseBehavior 2 'ExitApp'
                Stop-OwnedApp
                Start-Case $locale $colorTheme
                $saved = Get-Content -LiteralPath (Join-Path $script:data 'background.json') -Raw | ConvertFrom-Json
                Assert ($saved.CloseBehavior -eq 'ExitApp') 'Exit close behavior did not survive restart.'
                Capture 'persisted-snapshot.png'
                $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
                Close-OwnedWindow
                if (-not $script:app.WaitForExit(12000)) { throw 'Exit mode did not gracefully close the isolated app.' }
                Assert ((Get-Content -LiteralPath (Join-Path $script:data 'background.json') -Raw | ConvertFrom-Json).CloseBehavior -eq 'ExitApp') 'Graceful exit changed the saved behavior.'
                Pass "$locale/$colorTheme exit mode gracefully closes and retains the saved choice"
            }
            catch {
                Fail "$locale/$colorTheme General settings smoke" $_.Exception.Message
                if ($script:window -and $script:app -and -not $script:app.HasExited) { try { Capture 'failure.png' } catch { } }
            }
            finally { Stop-OwnedApp -Force }
        }
    }
}
finally {
    Save-Json (Join-Path $script:root 'results.json') @($script:results)
    Write-Output "General settings UI smoke artifacts: $script:root"
}
if (@($script:results | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }
exit 0
