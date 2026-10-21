param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/manual-workspace-portable-v2'),
    [Parameter(Mandatory)][switch]$LayoutReady
)

# Offline, isolated UI smoke. Never invokes RunAgent or the trust dialog's primary action.
# Call only after the reviewed portable layout is published. Evidence is intentionally retained.
# Known limitation in the reviewed v2 layout: at 500x600, the settings link opens
# Agent Settings but does not scroll the trusted folders field into view. Keep that
# assertion failing until the product behavior is fixed; warning/review/cancel work.
$ErrorActionPreference = 'Stop'
if (-not $LayoutReady) { throw 'The reviewed layout must be ready before launching the isolated smoke.' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AppDirectory must be below desktop/artifacts.'
}
$appExe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) { throw 'Published portable executable is absent.' }
if (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml')) { throw 'Use an unpackaged portable layout.' }
$runRoot = Join-Path $artifactRoot ('manual-workspace-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Write-Output "Evidence: $runRoot"
$checks = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[object]]::new()
$script:app = $null
$script:hwnd = [IntPtr]::Zero
$script:variant = ''
$script:size = ''
$script:batchFinished = $false

function Save-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Save-Results {
    Save-Json (Join-Path $runRoot 'results.json') @{
        checks = $checks.ToArray(); completed = $script:batchFinished
        passed = @($checks | Where-Object passed).Count
        failed = @($checks | Where-Object { -not $_.passed }).Count; evidence = $runRoot
    }
}
function Check([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $checks.Add(@{ variant = $script:variant; size = $script:size; name = $Name; passed = $true })
        Write-Output "PASS $script:variant $script:size $Name"
    } catch {
        $checks.Add(@{ variant = $script:variant; size = $script:size; name = $Name; passed = $false; detail = $_.Exception.Message })
        Write-Warning "$script:variant $script:size $Name failed: $_"
        try { Save-Screenshot ('failure-' + $checks.Count); Save-ElementEvidence ('failure-' + $checks.Count) } catch { Write-Warning "Failure evidence capture: $_" }
    }
    Save-Results
}
function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Seconds = 12) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        if ($script:app -and $script:app.HasExited) { throw "Isolated app exited waiting for $Description." }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out waiting for $Description."
}
function Find-Element([string]$Id) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Require-Element([string]$Id) {
    Wait-Until { $null -ne (Find-Element $Id) } $Id
    return Find-Element $Id
}
function Invoke-Element([string]$Id) {
    if ($Id.StartsWith('Nav') -and $null -eq (Find-Element $Id)) {
        $toggle = Require-Element 'TogglePaneButton'
        $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
    }
    $element = Require-Element $Id
    Assert ($element.Current.IsEnabled) "$Id is disabled."
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    } elseif ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    } else { throw "$Id does not expose InvokePattern or SelectionItemPattern." }
    Start-Sleep -Milliseconds 300
}
function Element-Visible([string]$Id) {
    $element = Find-Element $Id
    if ($null -eq $element -or $element.Current.IsOffscreen) { return $false }
    $rect = $element.Current.BoundingRectangle
    $windowRect = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd).Current.BoundingRectangle
    return $rect.Width -gt 0 -and $rect.Height -gt 0 -and $rect.Left -ge $windowRect.Left -and $rect.Right -le $windowRect.Right -and $rect.Top -ge ($windowRect.Top + 30) -and $rect.Bottom -le $windowRect.Bottom
}
function Save-Screenshot([string]$Name) {
    $path = Join-Path $script:evidence ($script:size + '-' + $Name + '.png')
    $output = & winapp ui screenshot -a $script:app.Id -w $script:hwnd.ToInt64() -o $path 2>&1
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $path)) "Screenshot failed: $output"
}
function Save-ElementEvidence([string]$Name) {
    $details = foreach ($id in @('ManualWorkspaceError', 'ManualReviewTrust', 'ManualWorkspaceSettings', 'AgentCommonSettings', 'TrustedWorkspaceCard', 'TrustedWorkspaceRoots', 'MainContentScroll')) {
        $element = Find-Element $id
        if ($element) {
            $rect = $element.Current.BoundingRectangle
            @{ id = $id; name = $element.Current.Name; offscreen = $element.Current.IsOffscreen; x = $rect.X; y = $rect.Y; width = $rect.Width; height = $rect.Height }
        }
    }
    Save-Json (Join-Path $script:evidence ($script:size + '-' + $Name + '-elements.json')) @($details)
}
function Assert-Unchanged {
    Assert ((Get-FileHash -LiteralPath $script:settingsPath).Hash -eq $script:settingsHash) 'settings.json bytes changed.'
    $saved = Get-Content -LiteralPath $script:settingsPath -Raw | ConvertFrom-Json
    Assert ($saved.TrustedWorkspaceRoots -eq $script:trusted) 'Trusted roots changed.'
    Assert (@((Get-Content -LiteralPath (Join-Path $script:data 'history.json') -Raw | ConvertFrom-Json).Records).Count -eq 0) 'Manual history was created.'
    Assert (-not (Test-Path -LiteralPath $script:fakeCli)) 'Unexpected fake CLI file was created.'
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($script:app.Id)")
    Assert ($children.Count -eq 0) ('Unexpected child process: ' + ($children.Name -join ', '))
}
function Stop-OwnedApp {
    if (-not $script:app -or $script:app.HasExited) { return }
    $current = Get-Process -Id $script:app.Id -ErrorAction SilentlyContinue
    if ($current -and $current.Path -eq $appExe -and $current.StartTime -eq $script:app.StartTime -and $current.Id -ne 44416) {
        Stop-Process -InputObject $current
        $current.WaitForExit(5000) | Out-Null
    } else { throw 'Owned-process cleanup identity check failed; no process was stopped.' }
}

try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ManualWorkspaceSmokeWindow {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
}
'@
    $core = [Reflection.Assembly]::LoadFrom((Join-Path $appRoot 'ChatAgentBridge.Core.dll'))
    $consentStoreType = $core.GetType('ChatAgentBridge.Core.OnboardingConsentStore', $true)
    $consentType = $core.GetType('ChatAgentBridge.Core.OnboardingConsent', $true)
    $policy = $consentStoreType.GetField('CurrentPolicyVersion').GetRawConstantValue()
    foreach ($language in @('ja', 'en')) {
        foreach ($theme in @('light', 'dark')) {
            $script:variant = "$language-$theme"
            $script:size = 'setup'
            $area = Join-Path $runRoot $script:variant
            $script:data = Join-Path $area 'data'
            $script:evidence = Join-Path $area 'evidence'
            $workspace = Join-Path $area 'outside-trust'
            $script:trusted = Join-Path $area 'trusted'
            $script:fakeCli = Join-Path $area 'nonexistent-cli.exe'
            New-Item -ItemType Directory -Path $script:data, $script:evidence, $workspace, $script:trusted | Out-Null
            $script:settingsPath = Join-Path $script:data 'settings.json'
            Save-Json $script:settingsPath @{
                SchemaVersion = 1; WorkspaceRoot = $workspace; TrustedWorkspaceRoots = $script:trusted
                AgentProvider = 'codex'; AgentMode = 'exec'; AgentExecutable = $script:fakeCli
                AgentArguments = 'exec --json -'; AutoConnectProvider = 'none'; RequireApproval = $true
                AgentPermissionMode = 'inherit'; AllowAgentNetwork = $false
            }
            Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
            $consentStore = $consentStoreType.GetConstructor([type[]]@([string])).Invoke([object[]]@([string]$script:data))
            $consent = $consentType.GetConstructor([type[]]@([string], [DateTimeOffset])).Invoke([object[]]@([string]$policy, [DateTimeOffset]::UtcNow))
            $consentStore.SaveAsync($consent, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
            Save-Json (Join-Path $script:data 'language.json') @{ Language = $language }
            Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = $theme }
            Save-Json (Join-Path $script:data 'portable-update.json') @{ SchemaVersion = 1; AutomaticChecksEnabled = $false; IncludePrereleases = $false; LastAttemptUtc = $null; AvailableRelease = $null }
            Save-Json (Join-Path $script:data 'history.json') @{ SchemaVersion = 1; Records = @() }
            Save-Json (Join-Path $script:data 'sessions.json') @{ SchemaVersion = 1; Bindings = @(); Workspaces = @() }
            $script:settingsHash = (Get-FileHash -LiteralPath $script:settingsPath).Hash
            Save-Json (Join-Path $area 'fixture.json') @{ language = $language; theme = $theme; workspace = $workspace; trusted = $script:trusted; executable = $script:fakeCli; settingsSha256 = $script:settingsHash }
            $start = [Diagnostics.ProcessStartInfo]::new($appExe)
            $start.UseShellExecute = $false
            $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            $start.WorkingDirectory = $area
            $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($script:data)
            $start.Environment['CAB_WINUI_DATA_DIR'] = $script:data
            $script:app = [Diagnostics.Process]::Start($start)
            $owned.Add(@{ pid = $script:app.Id; executable = $appExe; data = $script:data; started = $script:app.StartTime })
            Save-Json (Join-Path $runRoot 'owned-processes.json') $owned.ToArray()
            try {
                Wait-Until { $script:app.Refresh(); $script:hwnd = $script:app.MainWindowHandle; $script:hwnd -ne [IntPtr]::Zero } 'owned main window' 25
                foreach ($bounds in @(@{ name = '1160x800'; width = 1160; height = 800 }, @{ name = '500x600'; width = 500; height = 600 })) {
                    try {
                    $script:size = $bounds.name
                    $scale = [ManualWorkspaceSmokeWindow]::GetDpiForWindow($script:hwnd) / 96.0
                    Assert ([ManualWorkspaceSmokeWindow]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 30, 30, [int]($bounds.width * $scale), [int]($bounds.height * $scale), 0x14)) 'Window resize failed.'
                    Start-Sleep -Milliseconds 300
                    Invoke-Element 'NavWorkbench'
                    Save-Screenshot 'warning'
                    Save-ElementEvidence 'warning'
                    Check 'warning visible before any execution' {
                        Wait-Until { Element-Visible 'ManualWorkspaceError' } 'visible workspace warning'
                        $warning = Require-Element 'ManualWorkspaceError'
                        $texts = @($warning.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join "`n"
                        Assert ($texts.Contains($workspace)) 'Warning omits the saved invalid workspace path.'
                        Assert (Element-Visible 'ManualReviewTrust') 'Trust review action is clipped or offscreen.'
                        Assert (Element-Visible 'ManualWorkspaceSettings') 'Workspace settings link is clipped or offscreen.'
                        Assert-Unchanged
                    }
                    Check 'warning actions have localized labels' {
                        $review = Require-Element 'ManualReviewTrust'
                        $settingsLink = Require-Element 'ManualWorkspaceSettings'
                        Assert (-not [string]::IsNullOrWhiteSpace($review.Current.Name)) 'Trust review label is empty.'
                        Assert (-not [string]::IsNullOrWhiteSpace($settingsLink.Current.Name)) 'Workspace settings label is empty.'
                    }
                    Check 'trust review opens with cancel focus and persists nothing' {
                        Invoke-Element 'ManualReviewTrust'
                        $dialog = Require-Element 'CliTrustDialog'
                        Assert (-not [string]::IsNullOrWhiteSpace($dialog.Current.Name)) 'Trust review title is empty.'
                        $target = Require-Element 'CliTrustTargetPath'
                        Assert ($target.Current.Name.Contains($workspace)) 'Review target does not match the invalid workspace.'
                        $close = Require-Element 'CloseButton'
                        Assert ($close.Current.IsEnabled) 'Cancel is disabled.'
                        $focus = [System.Windows.Automation.AutomationElement]::FocusedElement
                        Assert ($focus.Current.AutomationId -eq 'CloseButton') ('Cancel is not initially focused; focus=' + $focus.Current.AutomationId)
                        Save-Screenshot 'trust-review'
                        Invoke-Element 'CloseButton'
                        Wait-Until { $null -eq (Find-Element 'CliTrustDialog') } 'trust review dismissal'
                        Assert-Unchanged
                    }
                    Check 'settings link expands collapsed Common and brings trusted roots into view' {
                        Invoke-Element 'NavAgentSettings'
                        $common = Require-Element 'AgentCommonSettings'
                        $expand = $common.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                        if ($expand.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $expand.Collapse() }
                        Assert ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) 'Common did not collapse.'
                        Invoke-Element 'NavWorkbench'
                        Invoke-Element 'ManualWorkspaceSettings'
                        Wait-Until { Element-Visible 'TrustedWorkspaceRoots' } 'visible trusted roots after settings link'
                        $common = Require-Element 'AgentCommonSettings'
                        $expand = $common.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                        Assert ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) 'Common did not expand.'
                        $roots = Require-Element 'TrustedWorkspaceRoots'
                        Assert ($roots.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $script:trusted) 'Trusted roots field changed.'
                        Assert-Unchanged
                    }
                    Save-Screenshot 'trusted-settings'
                    Save-ElementEvidence 'trusted-settings'
                    } catch {
                        Save-Screenshot 'failure'
                        Save-ElementEvidence 'failure'
                        $tree = & winapp ui inspect -a $script:app.Id -w $script:hwnd.ToInt64() --depth 12 --json 2>&1
                        $tree | Set-Content -LiteralPath (Join-Path $script:evidence ($script:size + '-failure-tree.json')) -Encoding utf8
                        $checks.Add(@{ variant = $script:variant; size = $script:size; name = 'scenario completion'; passed = $false; detail = $_.Exception.Message })
                        Save-Results
                        Write-Warning "$script:variant $script:size failed: $_"
                    }
                    # Create this sentinel in the printed evidence directory to stop after a scenario.
                    if (Test-Path -LiteralPath (Join-Path $runRoot 'stop-requested')) { throw 'Smoke stopped by caller after the current scenario.' }
                }
            } catch {
                try {
                    Save-Screenshot 'failure'
                    $tree = & winapp ui inspect -a $script:app.Id -w $script:hwnd.ToInt64() --json 2>&1
                    $tree | Set-Content -LiteralPath (Join-Path $script:evidence ($script:size + '-failure-tree.json')) -Encoding utf8
                } catch { Write-Warning "Failure evidence capture: $_" }
                throw
            } finally { Stop-OwnedApp; $script:app = $null }
        }
    }
    $script:batchFinished = $true
} catch {
    $checks.Add(@{ variant = $script:variant; size = $script:size; name = 'batch completion'; passed = $false; detail = $_.Exception.ToString() })
    Write-Error $_ -ErrorAction Continue
} finally {
    if ($script:app) { Stop-OwnedApp }
    Save-Results
    Write-Output "Evidence: $runRoot"
}
if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
