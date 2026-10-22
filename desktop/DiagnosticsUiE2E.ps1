param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [switch]$ExerciseTestNotification,
    [string[]]$OnlyScenario = @()
)
$ErrorActionPreference = 'Stop'
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$executable = Join-Path $artifactRootFull 'ChatAgentBridge.Desktop.exe'
if (-not $artifactRootFull.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactRoot must be an isolated build under desktop/artifacts.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The isolated desktop test executable is missing.' }
$testRoot = Join-Path $artifactsRoot ('diagnostics-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DiagnosticsUiNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
 [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$results = [Collections.Generic.List[object]]::new()
$activeProcess = $null
$activeWindow = $null
$currentUiStep = 'setup'
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Record-Pass([string]$Name) { $results.Add(@{ name=$Name; status='PASS' }); Write-Output "PASS $Name" }
function Record-Fail([string]$Name, [string]$Detail = '') {
    $entry = @{ name=$Name; status='FAIL' }
    if (-not [string]::IsNullOrWhiteSpace($Detail)) { $entry.detail=$Detail }
    $results.Add($entry); Write-Output "FAIL $Name"
}
function Check([string]$Name, [scriptblock]$Action) { try { & $Action; Record-Pass $Name } catch { Record-Fail $Name } }
function UI([string[]]$Arguments) {
    $output = winapp ui @Arguments -a $activeProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { [string]$_ }) -join ' '
        $detail = $detail -replace [regex]::Escape($artifactRootFull), '<artifact>' -replace [regex]::Escape($testRoot), '<fixture>'
        $detail = $detail -replace '(?i)Bearer\s+\S+', 'Bearer [redacted]' -replace '(?i)xox[baprs]-[A-Za-z0-9-]+', '[redacted-token]'
        throw "UI command '$($Arguments[0])' failed: $detail"
    }
    return $output
}
function Root { [Windows.Automation.AutomationElement]::FromHandle($activeProcess.MainWindowHandle) }
function Element([string]$Id) {
    (Root).FindFirst([Windows.Automation.TreeScope]::Descendants,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Wait-Element([string]$Id, [int]$Seconds = 8) { UI @('wait-for',$Id,'-t',([string]($Seconds*1000))) | Out-Null }
function Wait-ElementDirect([string]$Id, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Element $Id) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation element '$Id' was not created."
}
function Scroll-TargetIntoView([string]$Id,[string]$ScrollViewerId) {
    Wait-ElementDirect $Id
    $target = Element $Id
    $scroller = Element $ScrollViewerId
    if (-not $target -or -not $scroller) { throw 'Scroll target or viewer is missing from UI Automation.' }
    $pattern = $scroller.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    for ($attempt=0; $attempt -lt 10; $attempt++) {
        $targetRect = $target.Current.BoundingRectangle
        $viewerRect = $scroller.Current.BoundingRectangle
        if ($targetRect.Width -gt 0 -and $targetRect.Top -ge $viewerRect.Top -and $targetRect.Bottom -le $viewerRect.Bottom) { return }
        if ($targetRect.Width -le 0 -or $targetRect.Height -le 0) {
            $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement)
        }
        else {
            $percent = $pattern.Current.VerticalScrollPercent
            if ($targetRect.Top -ge $viewerRect.Bottom) { $next = [Math]::Min(100,$percent + 10) }
            else { $next = [Math]::Max(0,$percent - 10) }
            if ($next -eq $percent) { break }
            $pattern.SetScrollPercent(-1,$next)
        }
        Start-Sleep -Milliseconds 100
        $target = Element $Id
    }
    $target = Element $Id
    $targetRect = $target.Current.BoundingRectangle
    $viewerRect = $scroller.Current.BoundingRectangle
    if ($targetRect.Width -le 0 -or $targetRect.Top -lt $viewerRect.Top -or $targetRect.Bottom -gt $viewerRect.Bottom) {
        throw 'Target could not be moved into its scroll viewer.'
    }
}
function Scroll-ToEnd([string]$Id) {
    $scroller = Element $Id
    if (-not $scroller) { throw 'Expected scroll viewer is missing.' }
    $pattern = $scroller.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $pattern.SetScrollPercent(-1,100)
    Start-Sleep -Milliseconds 250
}
function Navigate([string]$Id) { UI @('invoke',$Id) | Out-Null; Start-Sleep -Milliseconds 180 }
function Get-PickerWindow {
    $mainHandle = $activeProcess.MainWindowHandle
    for ($attempt=0; $attempt -lt 24; $attempt++) {
        # WinUI file pickers can live in a separate PickerHost process, so app-PID
        # filtering misses them. Select only a top-level window owned by our fixture.
        $windows = winapp ui list-windows --json 2>$null | ConvertFrom-Json
        foreach ($window in $windows) {
            if ([long]$window.hwnd -eq $mainHandle.ToInt64() -or $window.title -eq 'PopupHost') { continue }
            $owner = [DiagnosticsUiNative]::GetWindow([IntPtr]([long]$window.hwnd),4)
            if ($owner -eq $mainHandle) { return $window }
        }
        Start-Sleep -Milliseconds 250
    }
    $status = Element 'DataManagementStatus'
    $statusName = if ($status) { $status.Current.Name } else { '' }
    throw "No native picker owned by the fixture appeared. Status: $statusName"
}
function Invoke-PickerCommand($Picker, [int]$CommandId) {
    # Native Common Item Dialog buttons are IDCANCEL=2 and IDOK=1. This avoids
    # English button-name selectors on OS-localized picker windows.
    $handle = [IntPtr]([long]$Picker.hwnd)
    if (-not [DiagnosticsUiNative]::PostMessage($handle,0x0111,[IntPtr]$CommandId,[IntPtr]::Zero)) {
        throw 'Native picker command could not be posted.'
    }
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        if (-not [DiagnosticsUiNative]::IsWindow($handle)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw 'Native picker did not close after its button command.'
}
function Choose-PickerFile([string]$Path, [string]$Action) {
    $picker = Get-PickerWindow
    $handle = [IntPtr]([long]$picker.hwnd)
    [void][DiagnosticsUiNative]::SetForegroundWindow($handle)
    [Windows.Forms.SendKeys]::SendWait('^l')
    Start-Sleep -Milliseconds 200
    if ($Action -eq 'Save') {
        # Navigate to the fixture folder; the picker already has the product's
        # suggested safe filename, so only the directory needs changing.
        $inputPath = Split-Path -Parent $Path
    } elseif ($Action -eq 'Open') { $inputPath = Split-Path -Parent $Path }
    else { throw 'Unexpected picker action.' }
    [Windows.Forms.SendKeys]::SendWait($inputPath)
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 500
    if ($Action -eq 'Open') {
        # Alt+N targets the filename field by its stable common-dialog
        # accelerator (also used by the Japanese OS-localized dialog).
        [Windows.Forms.SendKeys]::SendWait('%n')
        Start-Sleep -Milliseconds 150
        [Windows.Forms.SendKeys]::SendWait((Split-Path -Leaf $Path))
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
        for ($attempt=0; $attempt -lt 40; $attempt++) {
            if (-not [DiagnosticsUiNative]::IsWindow($handle)) { return }
            Start-Sleep -Milliseconds 100
        }
        throw 'Native picker did not open the selected fixture backup.'
    }
    Invoke-PickerCommand $picker 1
}
function Expand([string]$Id) {
    $item = Element $Id
    if (-not $item) { throw 'Expected settings expander is missing.' }
    $header = $item.FindFirst([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button))
    if (-not $header) { throw 'Settings expander header button is missing.' }
    $pattern = $header.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { $pattern.Expand() }
    Start-Sleep -Milliseconds 250
    if ($pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { throw 'Settings expander did not open.' }
}
function Resize-Dips([int]$Width, [int]$Height = 860) {
    $scale = [DiagnosticsUiNative]::GetDpiForWindow($activeProcess.MainWindowHandle) / 96.0
    [void][DiagnosticsUiNative]::SetWindowPos($activeProcess.MainWindowHandle,[IntPtr]::Zero,30,30,[int]($Width*$scale),[int]($Height*$scale),0x0014)
    Start-Sleep -Milliseconds 250
}
function Assert-InWindow([string]$Id) {
    Scroll-TargetIntoView $Id 'AboutScroll'
    $item = Element $Id
    if (-not $item -or $item.Current.IsOffscreen) { throw 'A diagnostics control is not reachable.' }
    $bounds = $item.Current.BoundingRectangle
    $windowBounds = (Root).Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Left -lt $windowBounds.Left -or $bounds.Right -gt $windowBounds.Right) {
        throw 'A diagnostics control is clipped horizontally.'
    }
}
function Start-TestApp([string]$DataDirectory) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $artifactRootFull
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($DataDirectory)
    $script:activeProcess = [Diagnostics.Process]::Start($start)
    if (-not $activeProcess -or $activeProcess.Id -eq 39892) { throw 'Refusing an unexpected app process.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $activeProcess.Refresh()
        if ($activeProcess.HasExited) { throw 'Isolated app exited during startup.' }
        if ($activeProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($activeProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Isolated app window did not appear.' }
    $script:activeWindow = Root
}
function Write-ScenarioFixture([string]$DataDirectory, [string]$Language, [string]$Theme, [string]$PolicyVersion) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion=1; WorkspaceRoot=''; AgentExecutable='unused'; AgentArguments=''; AgentMode='exec'; AgentProvider='custom'
        AutoConnectProvider='none'; TrustedWorkspaceRoots=''; AgentPermissionMode='inherit'; RequireApproval=$true
        ChatApprovalMode='first'; ChatResultMode='manual'; FileAttachmentApprovalMode='always'
    }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion=3; step=4; status='completed' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{ PolicyVersion=$PolicyVersion; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language=$Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme=$Theme }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{ schemaVersion=1; automaticChecksEnabled=$false; includePrereleases=$false; lastAttemptUtc=$null; availableRelease=$null }
    Write-Json (Join-Path $DataDirectory 'pricing-auto.json') @{ schemaVersion=1; autoUpdateEnabled=$false }
}
function Close-TestApp {
    if ($activeProcess -and -not $activeProcess.HasExited) {
        try { $activeProcess.Kill($true); [void]$activeProcess.WaitForExit(6000) } catch { }
    }
}

try {
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not locate current consent policy version.' }
    $policyVersion = $policyMatch.Groups[1].Value

    foreach ($language in @('ja','en')) {
        foreach ($theme in @('light','dark')) {
            $scenario = "$language-$theme"
            if ($OnlyScenario.Count -gt 0 -and $scenario -notin $OnlyScenario) { continue }
            $data = Join-Path $testRoot "data-$scenario"
            Write-ScenarioFixture $data $language $theme $policyVersion
            $currentUiStep = 'launch'
            Start-TestApp $data
            try {
                UI @('screenshot','-o',(Join-Path $testRoot "initial-$scenario.png")) | Out-Null
                $currentUiStep = 'overview navigation'
                Navigate 'NavOverview'
                $themeId = if ($theme -eq 'light') { 'ThemeLight' } else { 'ThemeDark' }
                $currentUiStep = 'theme control discovery'
                Wait-Element $themeId
                $themeControl = Element $themeId
                $currentUiStep = 'theme control state'
                $selection = $themeControl.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
                if (-not $selection.Current.IsSelected) { throw 'Fixture theme was not applied.' }

                $currentUiStep = 'navigate About'
                Navigate 'NavAbout'
                $currentUiStep = 'resize to narrow window'
                Resize-Dips 500
                UI @('screenshot','-o',(Join-Path $testRoot "about-$scenario.png")) | Out-Null
                $currentUiStep = 'wait for About scroll viewer'
                Wait-ElementDirect 'AboutScroll'
                $currentUiStep = 'scroll About diagnostics into view'
                Scroll-TargetIntoView 'AboutDiagnostics' 'AboutScroll'
                Expand 'AboutDiagnostics'
                Wait-ElementDirect 'DiagnosticsVersion'
                $exportButton = Element 'DiagnosticsExportButton'
                $expected = if ($language -eq 'ja') { 'エクスポート' } else { 'Export support report' }
                if (-not $exportButton.Current.Name.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Diagnostics text does not match fixture language.' }
                $currentUiStep = 'diagnostics layout assertions'
                foreach ($id in @('DiagnosticsVersion','DiagnosticsBuild','DiagnosticsWindows','DiagnosticsProvider','DiagnosticsCliVerification','DiagnosticsChatStatus','DiagnosticsUpdaterStatus','DiagnosticsNotificationStatus','DiagnosticsTestNotificationButton','DiagnosticsExportButton')) {
                    Assert-InWindow $id
                }
                $providerText = (Element 'DiagnosticsProvider').Current.Name
                $cliText = (Element 'DiagnosticsCliVerification').Current.Name
                $chatText = (Element 'DiagnosticsChatStatus').Current.Name
                $notificationText = (Element 'DiagnosticsNotificationStatus').Current.Name
                if ($providerText -ne 'Custom') { throw 'Saved provider label was not canonical.' }
                if ($cliText -notmatch 'Not verified|未検証') { throw 'Custom CLI verification was not reported as unverified.' }
                if ($chatText -notmatch 'No chat provider|チャットプロバイダーは保存されていません') { throw 'Chat configuration state was not reported.' }
                if ([string]::IsNullOrWhiteSpace($notificationText)) { throw 'Notification registration state was empty.' }
                UI @('screenshot','-o',(Join-Path $testRoot "diagnostics-$scenario.png")) | Out-Null
                Record-Pass "diagnostics display/layout ($scenario, 500 DIP)"

                $currentUiStep = 'support preview'
                Navigate 'DiagnosticsExportButton'
                Wait-Element 'DiagnosticsExportPreview'
                UI @('screenshot','-o',(Join-Path $testRoot "support-preview-$scenario.png")) | Out-Null
                $previewElement = Element 'DiagnosticsExportPreviewText'
                $previewText = $previewElement.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
                if (-not $previewText.Contains('WindowsVersion') -or -not $previewText.Contains('CliVersion')) { throw "Support preview omitted safe fields (characters=$($previewText.Length), nameLength=$($previewElement.Current.Name.Length), type=$($previewElement.Current.ControlType.ProgrammaticName))." }
                foreach ($forbidden in @('WorkspaceRoot','AgentArguments','SlackBotToken','MattermostToken','xoxb-','Bearer ','Prompt','EnvironmentVariable','user path')) {
                    if ($previewText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw 'Support preview contains a forbidden field marker.' }
                }
                Navigate 'CloseButton'
                UI @('wait-for','DiagnosticsExportPreview','--gone','-t','5000') | Out-Null
                Record-Pass "support preview cancel ($scenario)"

                if ($ExerciseTestNotification -and $scenario -eq 'en-light') {
                    Navigate 'DiagnosticsTestNotificationButton'
                    Start-Sleep -Milliseconds 300
                    $status = (UI @('get-value','DiagnosticsStatus') | ConvertFrom-Json).text
                    if ($status -notmatch 'delivery is not confirmed|配信は確認されていません') { throw 'Test notification status must not claim delivery.' }
                    Record-Pass 'harmless TEST notification enqueue status'
                }

                $currentUiStep = 'data management'
                Resize-Dips 1100
                Navigate 'NavOverview'
                Wait-ElementDirect 'MainContentScroll'
                Scroll-TargetIntoView 'DataManagementGroup' 'MainContentScroll'
                Wait-ElementDirect 'DataManagementGroup'
                Expand 'DataManagementGroup'
                $sizeLabel = Element 'DataManagementSizes'
                if (-not $sizeLabel -or [string]::IsNullOrWhiteSpace($sizeLabel.Current.Name)) { throw 'Data-management size summary was empty.' }
                Scroll-TargetIntoView 'DataClear' 'MainContentScroll'
                Navigate 'DataClear'
                Wait-Element 'DataClearDialog'
                Navigate 'CloseButton'
                UI @('wait-for','DataClearDialog','--gone','-t','5000') | Out-Null
                if (-not (Test-Path -LiteralPath (Join-Path $data 'settings.json'))) { throw 'Canceling clear removed fixture settings.' }
                Record-Pass "data management summary and clear cancel ($scenario)"

                $currentUiStep = 'restore picker cancel'
                Scroll-TargetIntoView 'SettingsBackupImport' 'MainContentScroll'
                Navigate 'SettingsBackupImport'
                $picker = Get-PickerWindow
                Invoke-PickerCommand $picker 2
                if ([DiagnosticsUiNative]::IsWindow([IntPtr]([long]$picker.hwnd))) { throw 'Cancel did not close the restore picker.' }
                $exportButton = Element 'SettingsBackupExport'
                for ($attempt=0; $attempt -lt 30 -and $exportButton -and -not $exportButton.Current.IsEnabled; $attempt++) { Start-Sleep -Milliseconds 100; $exportButton = Element 'SettingsBackupExport' }
                if (-not $exportButton -or -not $exportButton.Current.IsEnabled) { throw 'Restore picker cancellation did not release the data-management busy state.' }
                if (-not (Test-Path -LiteralPath (Join-Path $data 'settings.json'))) { throw 'Canceling restore changed fixture settings.' }
                Record-Pass "restore picker cancel ($scenario)"

                if ($scenario -eq 'en-light') {
                    $currentUiStep = 'backup export picker'
                    $backupPath = Join-Path $data 'chat-agent-bridge-settings.json'
                    Navigate 'SettingsBackupExport'
                    Start-Sleep -Milliseconds 500
                    Choose-PickerFile $backupPath 'Save'
                    for ($attempt=0; $attempt -lt 50 -and (-not (Test-Path -LiteralPath $backupPath -PathType Leaf) -or (Get-Item -LiteralPath $backupPath).Length -eq 0); $attempt++) { Start-Sleep -Milliseconds 100 }
                    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf) -or (Get-Item -LiteralPath $backupPath).Length -eq 0) { throw 'Settings backup save did not write the selected fixture file.' }
                    $backupJson = [IO.File]::ReadAllText($backupPath)
                    foreach ($forbidden in @('AgentArguments','MattermostToken','SlackBotToken','SlackAppToken','AllowedUserIds','AllowedThreadIds','sentinel','Bearer ','xoxb-')) {
                        if ($backupJson.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw 'Settings backup contains a forbidden field marker.' }
                    }
                    Record-Pass 'settings backup export allowlist and local save'

                    $currentUiStep = 'restore cancel flow'
                    $untrustedWorkspace = Join-Path $data 'untrusted-workspace'
                    New-Item -ItemType Directory -Force -Path $untrustedWorkspace | Out-Null
                    Write-Json $backupPath @{
                        SchemaVersion=1; CreatedAt=[DateTimeOffset]::UtcNow.ToString('o'); Provider='custom'; Executable='unused';
                        Mode='exec'; Model=$null; ReasoningEffort=$null; Workspace=$untrustedWorkspace;
                        MattermostServer=''; MattermostChannel=''; MattermostBypassProxy=$false; SlackChannel=''
                    }
                    $settingsPath = Join-Path $data 'settings.json'
                    $beforeCancel = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
                    $currentUiStep = 'restore confirm flow'
                    Navigate 'SettingsBackupImport'
                    Start-Sleep -Milliseconds 500
                    Choose-PickerFile $backupPath 'Open'
                    Wait-Element 'BackupRestoreDialog'
                    $trust = Element 'BackupRestoreTrustWorkspace'
                    $trustState = $trust.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
                    if ($trustState -ne [Windows.Automation.ToggleState]::Off) { throw 'Workspace trust default was not unchecked.' }
                    Navigate 'CloseButton'
                    UI @('wait-for','BackupRestoreDialog','--gone','-t','5000') | Out-Null
                    $afterCancel = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
                    if ($beforeCancel -ne $afterCancel) { throw 'Canceling restore changed fixture settings.' }
                    Record-Pass 'restore preview unchecked trust and cancel preserves settings'

                    Navigate 'SettingsBackupImport'
                    Start-Sleep -Milliseconds 500
                    Choose-PickerFile $backupPath 'Open'
                    Wait-Element 'BackupRestoreDialog'
                    $trust = Element 'BackupRestoreTrustWorkspace'
                    if ($trust.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Workspace trust default changed on repeated restore.' }
                    Navigate 'PrimaryButton'
                    Start-Sleep -Seconds 2
                    $restored = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                    if ($restored.WorkspaceRoot -ne '' -or $restored.TrustedWorkspaceRoots -ne '' -or $restored.AutoConnectProvider -ne 'none' -or $restored.RequireApproval -ne $true) {
                        throw 'Restore did not clear trust/auto-connect and preserve approval defaults.'
                    }
                    foreach ($field in @('MattermostToken','SlackBotToken','SlackAppToken','SlackAllowedUserIds','MattermostAllowedUserIds')) {
                        if ($restored.$field) { throw 'Restore unexpectedly retained an authorization field.' }
                    }
                    if ($restored.WorkspaceRoot -eq $untrustedWorkspace -or $restored.TrustedWorkspaceRoots -eq $untrustedWorkspace) { throw 'Untrusted workspace path was restored.' }
                    Record-Pass 'restore confirmation clears trust and chat authorizations'
                }
            }
            catch {
                $failureDetail = "$($_.Exception.GetType().FullName): $($_.Exception.Message) line=$($_.InvocationInfo.ScriptLineNumber) $($_.ScriptStackTrace)"
                $failureDetail = $failureDetail -replace [regex]::Escape($artifactRootFull), '<artifact>' -replace [regex]::Escape($testRoot), '<fixture>'
                $failureDetail = $failureDetail -replace '(?i)Bearer\s+\S+', 'Bearer [redacted]' -replace '(?i)xox[baprs]-[A-Za-z0-9-]+', '[redacted-token]'
                Record-Fail "diagnostics scenario ($($scenario): $currentUiStep)" $failureDetail
            }
            finally { Close-TestApp }
        }
    }
}
catch { Record-Fail 'diagnostics UI suite setup' }

Write-Json (Join-Path $testRoot 'results.json') $results.ToArray()
$failed = @($results | Where-Object status -eq 'FAIL').Count
Write-Output "Passed: $($results.Count - $failed) | Failed: $failed"
Write-Output "Isolated screenshots and results: $testRoot"
if ($failed -gt 0) { exit 1 }
