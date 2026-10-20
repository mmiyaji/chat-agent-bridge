param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'

$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload = [IO.Path]::GetFullPath($AppDirectory)
if (-not $payload.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $payload 'AppxManifest.xml'))) {
    throw 'Use an unpackaged portable payload below desktop/artifacts.'
}
$exe = Join-Path $payload 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Portable app executable not found: $exe" }

$testRoot = Join-Path $artifacts ('portable-update-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PortableUpdateUiNative {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@

$script:app = $null
$script:hwnd = [IntPtr]::Zero
$checks = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Pass([string]$Name) { $checks.Add($Name); Write-Output "PASS $Name" }
function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Element([string]$Id) {
    if ($null -eq $script:app -or $script:app.HasExited -or $script:hwnd -eq [IntPtr]::Zero) { return $null }
    $root = [Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Wait-Element([string]$Id, [int]$TimeoutMs = 5000) {
    $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $element = Element $Id
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "UI element did not appear: $Id"
}
function Wait-Name([string]$Name, [int]$TimeoutMs = 5000) {
    $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        if ($script:hwnd -ne [IntPtr]::Zero) {
            $root = [Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
            $element = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name))
            if ($null -ne $element) { return $element }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "UI element with localized name did not appear: $Name"
}
function Wait-Enabled([string]$Id, [bool]$Expected, [int]$TimeoutMs = 5000) {
    $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $element = Element $Id
        if ($null -ne $element -and $element.Current.IsEnabled -eq $Expected) { return $element }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $until)
    throw "UI element $Id did not become IsEnabled=$Expected"
}
function Read-Name([string]$Id) {
    $element = Element $Id
    if ($null -eq $element) { return '' }
    return $element.Current.Name
}
function Is-Hidden([string]$Id) {
    $element = Element $Id
    return $null -eq $element -or $element.Current.IsOffscreen -or $element.Current.BoundingRectangle.IsEmpty
}
function Scroll-IntoView([string]$Id) {
    $output = & winapp ui scroll-into-view $Id -a $script:app.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not scroll element into view: $Id ($($output -join ' '))" }
}
function Ensure-Expanded([string]$GroupId, [string]$TargetId) {
    $target = Element $TargetId
    if ($null -ne $target) {
        try { Scroll-IntoView $TargetId } catch { }
        if (-not (Is-Hidden $TargetId)) { return }
    }
    $group = Element $GroupId
    Assert ($null -ne $group) "Settings expander was not found: $GroupId"
    $header = $group.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button))
    Assert ($null -ne $header) "Settings expander header button was not found: $GroupId"
    $pattern = $null
    $acted = $false
    if ($header.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        if ($pattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $pattern.Expand() }
        $acted = $true
    }
    if (-not $acted -and $header.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        if ($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) { $pattern.Toggle() }
        $acted = $true
    }
    if (-not $acted -and $header.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        if ($null -eq $target) { $pattern.Invoke() }
        $acted = $true
    }
    Assert $acted "Settings expander header has no supported UIA pattern: $GroupId"
    Scroll-IntoView $TargetId
    $until = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (-not (Is-Hidden $TargetId)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Settings control did not become visible: $TargetId"
}
function Resize-And-Capture([string]$Name) {
    $scale = [PortableUpdateUiNative]::GetDpiForWindow($script:hwnd) / 96.0
    [void][PortableUpdateUiNative]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 60, 60,
        [int][Math]::Round(720 * $scale), [int][Math]::Round(900 * $scale), 0x0014)
    Start-Sleep -Milliseconds 400
    $path = Join-Path $testRoot ($Name + '.png')
    $output = & winapp ui screenshot -a $script:app.Id -o $path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Screenshot failed: $($output -join ' ')" }
}
function New-Fixture([string]$Language, [string]$Theme) {
    $data = Join-Path $testRoot ($Language + '-data')
    New-Item -ItemType Directory -Path $data | Out-Null
    Write-Json (Join-Path $data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $data; AgentExecutable = 'unused'; AgentProvider = 'custom'
        AgentMode = 'exec'; AgentArguments = ''; TrustedWorkspaceRoots = $data; AutoConnectProvider = 'none'
    }
    Write-Json (Join-Path $data 'agent-connections.json') @{ SchemaVersion = 1; Profiles = @() }
    Write-Json (Join-Path $data 'history.json') @{ SchemaVersion = 1; Records = @() }
    Write-Json (Join-Path $data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
    Write-Json (Join-Path $data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policy = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Current consent policy version was not found.'
    Write-Json (Join-Path $data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $data 'language.json') @{ Language = $Language }
    Write-Json (Join-Path $data 'appearance.json') @{ Theme = $Theme }
    Write-Json (Join-Path $data 'pricing-auto.json') @{
        schemaVersion = 1; autoUpdateEnabled = $false
        catalogJson = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw
    }
    return $data
}
function Start-Scenario([string]$Language, [string]$Theme) {
    $data = New-Fixture $Language $Theme
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $payload
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($data)
    $script:app = [Diagnostics.Process]::Start($start)
    $null = $script:app.WaitForInputIdle(10000)
    $until = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $script:app.Refresh()
        if ($script:app.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    Assert ($script:app.MainWindowHandle -ne [IntPtr]::Zero) 'Portable app main window did not appear.'
    $script:hwnd = $script:app.MainWindowHandle
    $null = Wait-Element 'NavAbout' 20000
    & winapp ui invoke NavAbout -a $script:app.Id --json | Out-Null
    if ($LASTEXITCODE) { throw 'Could not navigate to About.' }
    $null = Wait-Element 'PortableCheckUpdates' 7000
    $expectedHeading = if ($Language -eq 'ja') { 'ポータブル版の更新' } else { 'Portable version updates' }
    $null = Wait-Name $expectedHeading 7000
    return $data
}
function Stop-Scenario {
    if ($script:app -and -not $script:app.HasExited) {
        try {
            ([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).GetCurrentPattern(
                [Windows.Automation.WindowPattern]::Pattern).Close()
        } catch { }
        if (-not $script:app.WaitForExit(5000)) { Stop-Process -Id $script:app.Id -Force }
    }
    $script:app = $null
    $script:hwnd = [IntPtr]::Zero
}

try {
    $data = Start-Scenario 'ja' 'dark'
    $heading = (Wait-Name 'ポータブル版の更新').Current.Name
    Assert ($heading -eq 'ポータブル版の更新') "Japanese heading was not applied: $heading"
    Assert (Is-Hidden 'StoreUpdateCard') 'Store update card is visible in the portable build.'
    Pass 'Japanese dark About page shows portable updater and hides Store update card'

    Ensure-Expanded 'PortableUpdatePreferences' 'PortableUpdateIncludePrereleases'
    $preview = Wait-Element 'PortableUpdateIncludePrereleases'
    $previewPattern = $preview.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $initialPreviewState = $previewPattern.Current.ToggleState
    $previewPattern.Toggle()
    Assert ($previewPattern.Current.ToggleState -ne $initialPreviewState) 'Preview toggle did not change state.'
    $previewPattern.Toggle()
    Assert ($previewPattern.Current.ToggleState -eq $initialPreviewState) 'Preview toggle did not restore its initial state.'
    Pass 'Preview release setting toggles and restores its default'

    Resize-And-Capture 'ja-dark-720'
    $check = Wait-Enabled 'PortableCheckUpdates' $true
    $check.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $check = Wait-Enabled 'PortableCheckUpdates' $false 4000
    $secondInvocationAccepted = $false
    try {
        $check.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $secondInvocationAccepted = $true
    } catch { }
    Assert (-not $secondInvocationAccepted) 'A second update check was accepted while the first check was busy.'
    Pass 'Update check disables its command while busy and rejects a duplicate invoke'

    $until = [DateTime]::UtcNow.AddSeconds(45)
    $status = ''
    do {
        $status = Read-Name 'PortableUpdateStatus'
        if ($status -and $status -notmatch '確認しています') { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $until)
    $completed = $status -match '対応する新しいポータブル版は見つかりませんでした|No newer compatible portable release is available|ポータブル版 .+ が利用できます|Portable version .+ is available|ポータブル版を更新できませんでした: .+|Could not update the portable app: .+|更新ヘルパーが見つかりません|portable update helper is missing|このビルドではポータブル版|Portable updates are not available'
    Assert $completed "GitHub update check did not reach an informative terminal state: '$status'"
    $null = Wait-Enabled 'PortableCheckUpdates' $true 5000
    Assert (Is-Hidden 'PortableCancelUpdate') 'Cancel remained visible after update checking completed.'
    $download = Element 'PortableDownloadUpdate'
    if ($status -match 'ポータブル版 .+ が利用できます') {
        Assert ($null -ne $download -and $download.Current.IsEnabled) 'Available update did not enable download.'
        Assert (Is-Hidden 'PortableApplyUpdate') 'Apply was visible before the update was downloaded.'
    } else {
        Assert (Is-Hidden 'PortableDownloadUpdate') 'Download was visible without an available update.'
    }
    Pass 'Manual GitHub check reports an offer, current version, or informative error and restores idle controls'
    Resize-And-Capture 'ja-dark-720-final'
    Stop-Scenario

    $null = Start-Scenario 'en' 'light'
    $heading = (Wait-Name 'Portable version updates').Current.Name
    Assert ($heading -eq 'Portable version updates') "English heading was not applied: $heading"
    Assert (Is-Hidden 'StoreUpdateCard') 'Store update card is visible in the portable build.'
    Resize-And-Capture 'en-light-720'
    Pass 'English light About page is localized and fits at 720 DIP width'
    Stop-Scenario
} finally {
    Stop-Scenario
    @{ Checks = @($checks); ArtifactDirectory = $testRoot } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
    Write-Output "Portable update UI results: $testRoot"
}
