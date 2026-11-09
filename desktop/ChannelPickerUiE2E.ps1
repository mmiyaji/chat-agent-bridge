# Exercises Mattermost channel search, single selection, cancellation, and saving against a local API fixture.
param([Parameter(Mandatory)][string]$AppDirectory)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) { throw 'Use an isolated unpackaged app publish under desktop/artifacts.' }
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Desktop executable was not found: $exe" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ChannelPickerWindowSizing {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
'@
$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data = Join-Path $script:root 'data'
$script:app = $null
$script:server = $null
$script:window = $null
$script:results = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $script:data | Out-Null
Set-Content -LiteralPath (Join-Path $script:root '.cab-channel-picker-e2e') -Value 'Isolated Mattermost channel picker E2E' -Encoding utf8

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Add-Pass([string]$Name) { $script:results.Add($Name); Write-Output "PASS $Name" }
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    if (-not $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ProcessElement([string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
    foreach ($candidate in $windows) { $found = Find-Element $candidate $Id; if ($found) { return $found } }
    return $null
}
function Wait-Element([string]$Id, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:window $Id
        if (-not $element) { $element = Find-ProcessElement $Id }
        if ($element) { return $element }
        if ($script:app -and $script:app.HasExited) { throw "App exited waiting for '$Id' (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for '$Id'."
}
function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    Assert ($null -ne $Element -and $Element.Current.IsEnabled) "Missing or disabled UI element: $Description"
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.InvokePattern]$pattern).Invoke(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.TogglePattern]$pattern).Toggle(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        $expander = [Windows.Automation.ExpandCollapsePattern]$pattern
        if ($expander.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $expander.Expand() } else { $expander.Collapse() }
        return
    }
    throw "No Invoke or Toggle pattern for $Description."
}
function Set-Value([string]$Id, [string]$Value) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw "$Id has no ValuePattern." }
    ([Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
function Save-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }

try {
    Add-Type -AssemblyName System.Security
    $workspace = Join-Path $script:data 'workspace'
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null
    $protected = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('channel-picker-fixture-token'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policy = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read onboarding consent policy version.'

    $portFile = Join-Path $script:root 'fixture-port.txt'
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixtureScript = Join-Path $PSScriptRoot 'fixtures/channel-picker-server.cjs'
    $script:server = Start-Process -FilePath $node -ArgumentList @('"' + $fixtureScript + '"', '"' + $portFile + '"') -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath $portFile } 'local Mattermost fixture startup'
    $endpoint = 'http://127.0.0.1:' + (Get-Content -LiteralPath $portFile -Raw).Trim()

    Save-Json (Join-Path $script:data 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; TrustedWorkspaceRoots = $workspace
        AgentProvider = 'codex'; AgentMode = 'exec'; AgentExecutable = 'codex'; AgentArguments = 'exec --json -'
        MattermostServerUrl = $endpoint; MattermostChannelId = ''; MattermostToken = $protected
        MattermostBypassProxy = $true; AutoConnectProvider = 'none'; ChatApprovalMode = 'auto'; ChatResultMode = 'manual'
    }
    Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = 'en' }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = 'light' }

    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Arguments = '--data-dir "' + $script:data + '"'
    [void]$start.EnvironmentVariables.Remove('CAB_WINUI_DATA_DIR')
    $script:app = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw "App exited during startup (exit $($script:app.ExitCode))." }
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $windows) { if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break } }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($null -ne $script:window) 'No accessible app window appeared.'

    Invoke-Element (Wait-Element 'NavConnections') 'open chat connection settings'
    Invoke-Element (Wait-Element 'ConnectionScopeConfigure_mattermost') 'open Mattermost settings'
    Wait-For { (Wait-Element 'ConnectionScopeHeading').Current.Name -eq 'Mattermost' } 'Mattermost settings scope to become selected'
    $scale = [ChannelPickerWindowSizing]::GetDpiForWindow($script:app.MainWindowHandle) / 96.0
    Assert ([ChannelPickerWindowSizing]::SetWindowPos($script:app.MainWindowHandle, [IntPtr]::Zero, 40, 40, [int](500*$scale), [int](700*$scale), 0x0010)) 'Could not resize the app to its minimum width.'
    Start-Sleep -Milliseconds 250
    $settingsScroll = Wait-Element 'ConnectionSettingsScroll'
    $scrollPattern = $null
    if ($settingsScroll.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern) -and
        ([Windows.Automation.ScrollPattern]$scrollPattern).Current.VerticallyScrollable) {
        ([Windows.Automation.ScrollPattern]$scrollPattern).SetScrollPercent(-1, 100)
        Start-Sleep -Milliseconds 250
    }
    # Destination controls are collapsed until a channel ID is configured.
    Invoke-Element (Wait-Element 'MattermostDestinationsGroup') 'expand optional destination settings'
    Invoke-Element (Wait-Element 'SearchMattermostChannelsButton') 'open channel picker'
    Wait-Element 'ChannelPickerSearchBox' | Out-Null
    Wait-Element 'ChannelPickerOption_channel-cab-release' | Out-Null
    Wait-Element 'ChannelPickerOption_channel-cab-ops' | Out-Null
    $selectedCount = Wait-Element 'ChannelPickerSelectedCount'
    Wait-For { $selectedCount.Current.Name -eq '0 selected' } 'zero selection count for an unset Mattermost channel'
    Assert ($selectedCount.Current.Name -eq '0 selected') 'An unset channel must not be counted as selected.'
    Add-Pass 'At minimum width, the picker loads channels and shows zero selected before the user chooses one'

    Set-Value 'ChannelPickerSearchBox' 'cab-release'
    Wait-Element 'ChannelPickerOption_channel-cab-release' | Out-Null
    Wait-For { $null -eq (Find-ProcessElement 'ChannelPickerOption_channel-cab-ops') } 'nonmatching channel to be filtered out'
    Add-Pass 'Typing a channel name filters the popup results'

    $releaseOption = Wait-Element 'ChannelPickerOption_channel-cab-release'
    Invoke-Element $releaseOption 'select filtered channel'
    Invoke-Element (Wait-Element 'PrimaryButton') 'apply selected channel'
    Wait-For { $null -eq (Find-ProcessElement 'ChannelPickerSearchBox') } 'channel picker to close after applying'
    $channelField = Wait-Element 'MattermostChannelId'
    $valuePattern = $null
    Assert ($channelField.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) 'Mattermost channel field has no ValuePattern.'
    Wait-For { ([Windows.Automation.ValuePattern]$valuePattern).Current.Value -eq 'channel-cab-release' } 'selected channel ID to appear in the setting field'
    Assert (([Windows.Automation.ValuePattern]$valuePattern).Current.Value -eq 'channel-cab-release') 'Selected channel ID was not applied to the setting field.'
    Add-Pass 'Selecting a name applies its channel ID to Mattermost'

    $settingsPath = Join-Path $script:data 'settings.json'
    $isSaved = { try { (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).MattermostChannelId -eq 'channel-cab-release' } catch { $false } }
    Wait-For { (& $isSaved) -or $null -ne (Find-ProcessElement 'SensitiveSettingsConfirmation') } 'auto-save or sensitive-setting confirmation'
    $confirmation = Find-ProcessElement 'SensitiveSettingsConfirmation'
    if ($confirmation) {
        Invoke-Element (Find-Element $confirmation 'PrimaryButton') 'confirm selected Mattermost channel'
        Add-Pass 'Sensitive connection change is presented for confirmation'
    }
    Wait-For { & $isSaved } 'auto-saved channel selection to persist' 15
    Add-Pass 'Selected channel ID persists to isolated settings data'

    Invoke-Element (Wait-Element 'SearchMattermostChannelsButton') 'reopen channel picker'
    Wait-Element 'ChannelPickerOption_channel-cab-ops' | Out-Null
    Invoke-Element (Wait-Element 'ChannelPickerOption_channel-cab-ops') 'change pending channel selection'
    Invoke-Element (Wait-Element 'CloseButton') 'cancel channel selection'
    $channelField = Wait-Element 'MattermostChannelId'
    $valuePattern = $null; [void]$channelField.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)
    Assert (([Windows.Automation.ValuePattern]$valuePattern).Current.Value -eq 'channel-cab-release') 'Cancel changed the channel setting.'
    Add-Pass 'Cancel closes the picker without changing the setting'

    Save-Json (Join-Path $script:root 'results.json') @{ status = 'passed'; scenario = 'mattermost-channel-picker'; checks = @($script:results) }
    Write-Output "E2E_ROOT $script:root"
    $script:results | ForEach-Object { Write-Output "RESULT $_" }
}
catch {
    Write-Output $_.ScriptStackTrace
    Write-Output $_
    throw
}
finally {
    if ($script:app -and -not $script:app.HasExited) { try { $script:app.Kill(); $script:app.WaitForExit(7000) | Out-Null } catch { } }
    if ($script:server -and -not $script:server.HasExited) { try { $script:server.Kill(); $script:server.WaitForExit(5000) | Out-Null } catch { } }
}
