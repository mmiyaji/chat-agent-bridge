param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string[]]$OnlyScenario = @(),
    [switch]$LayoutOnly
)

$ErrorActionPreference = 'Stop'
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$executable = Join-Path $artifactRootFull 'ChatAgentBridge.Desktop.exe'
if (-not $artifactRootFull.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactRoot must be an isolated build under desktop/artifacts.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The isolated desktop test executable is missing.' }

$testRoot = Join-Path $artifactsRoot ('log-retention-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LogRetentionUiNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@

$results = [Collections.Generic.List[object]]::new()
$script:activeProcess = $null
$script:activeDataDirectory = ''
$currentScenario = ''
$currentStep = 'setup'

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
}
function Record-Pass([string]$Name) {
    $results.Add(@{ name = $Name; status = 'PASS' })
    Write-Output "PASS $Name"
}
function Record-Fail([string]$Name, [string]$Detail) {
    $results.Add(@{ name = $Name; status = 'FAIL'; detail = $Detail })
    Write-Output "FAIL $Name"
}
function Root {
    if (-not $script:activeProcess) { throw 'No isolated test app is running.' }
    [Windows.Automation.AutomationElement]::FromHandle($script:activeProcess.MainWindowHandle)
}
function Element([string]$Id) {
    $rootElement = Root
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $rootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Element([string]$Id, [int]$Seconds = 12) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $candidate = Element $Id
        if ($candidate) { return $candidate }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Automation element '$Id' did not appear."
}
function Invoke-Element([string]$Id) {
    if ($Id -eq 'NavAbout') {
        $target = Wait-Element $Id 35
        $target.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 250
        return
    }
    $target = Wait-Element $Id
    $target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Scroll-TargetIntoView([string]$Id) {
    $scrollViewer = Wait-Element 'AboutScroll'
    $scrollPattern = $scrollViewer.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scrollPattern.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 80
    for ($attempt = 0; $attempt -le 20; $attempt++) {
        $target = Element $Id
        if ($target) {
            $targetBounds = $target.Current.BoundingRectangle
            $viewerBounds = $scrollViewer.Current.BoundingRectangle
            if ($targetBounds.Width -gt 0 -and $targetBounds.Top -ge $viewerBounds.Top -and $targetBounds.Bottom -le $viewerBounds.Bottom) { return $target }
        }
        $percent = $scrollPattern.Current.VerticalScrollPercent
        $next = [Math]::Min(100, [Math]::Max(0, $percent) + 5)
        if ($percent -eq 100 -or $next -eq $percent) { break }
        $scrollPattern.SetScrollPercent(-1, $next)
        Start-Sleep -Milliseconds 120
    }
    $target = Element $Id
    if (-not $target -or $target.Current.BoundingRectangle.Width -le 0) { throw "Could not scroll '$Id' into view." }
    return $target
}
function Scroll-AboutToEnd {
    $scrollViewer = Wait-Element 'AboutScroll'
    $scrollPattern = $scrollViewer.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scrollPattern.SetScrollPercent(-1, 100)
    Start-Sleep -Milliseconds 300
}
function Scroll-AboutToStart {
    $scrollViewer = Wait-Element 'AboutScroll'
    $scrollPattern = $scrollViewer.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scrollPattern.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 300
}
function Assert-InWindow([string]$Id) {
    $target = Scroll-TargetIntoView $Id
    $bounds = $target.Current.BoundingRectangle
    $windowBounds = (Root).Current.BoundingRectangle
    if ($target.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Left -lt $windowBounds.Left -or $bounds.Right -gt $windowBounds.Right) {
        throw "Automation element '$Id' is clipped or offscreen at this width."
    }
}
function NumberBox-Input([string]$Id) {
    $numberBox = Wait-Element $Id
    try { $numberBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern) | Out-Null; return $numberBox }
    catch {
        $editCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
        $edit = $numberBox.FindFirst([Windows.Automation.TreeScope]::Descendants, $editCondition)
        if (-not $edit) { throw "NumberBox '$Id' has no UIA ValuePattern or inner Edit." }
        $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern) | Out-Null
        return $edit
    }
}
function Read-Number([string]$Id) {
    [void](Scroll-TargetIntoView $Id)
    $input = NumberBox-Input $Id
    [double]::Parse($input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value,
        [Globalization.CultureInfo]::InvariantCulture)
}
function Set-Number([string]$Id, [int]$Value) {
    [void](Scroll-TargetIntoView $Id)
    $input = NumberBox-Input $Id
    $input.SetFocus()
    $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue([string]$Value)
    $input.SetFocus()
}
function Wait-Dialog([bool]$Present, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $dialog = Element 'LogRetentionReductionDialog'
        if (($Present -and $dialog) -or (-not $Present -and -not $dialog)) { return $dialog }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Reduction dialog present=$Present was not observed."
}
function Get-ExpanderPattern([string]$Id) {
    $expander = Wait-Element $Id
    try { return $expander.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern) }
    catch {
        $buttonCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
        $header = $expander.FindFirst([Windows.Automation.TreeScope]::Children, $buttonCondition)
        if (-not $header) { throw "Expander '$Id' exposes no ExpandCollapse pattern or header button." }
        return $header.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    }
}
function Count-Elements([string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    (Root).FindAll([Windows.Automation.TreeScope]::Descendants, $condition).Count
}
function Set-WindowDips([int]$Width, [int]$Height = 860) {
    $scale = [LogRetentionUiNative]::GetDpiForWindow($script:activeProcess.MainWindowHandle) / 96.0
    if (-not [LogRetentionUiNative]::SetWindowPos($script:activeProcess.MainWindowHandle, [IntPtr]::Zero,
        30, 30, [int]($Width * $scale), [int]($Height * $scale), 0x0014)) {
        throw 'Could not resize the isolated app window.'
    }
    Start-Sleep -Milliseconds 300
}
function Screenshot([string]$Name) {
    $path = Join-Path $testRoot "$currentScenario-$Name.png"
    $output = winapp ui screenshot -a $script:activeProcess.Id -o $path --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Screenshot capture failed: $($output -join ' ')" }
}
function Start-TestApp([string]$DataDirectory) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $artifactRootFull
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($DataDirectory)
    $script:activeDataDirectory = $DataDirectory
    $script:activeProcess = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $script:activeProcess.Refresh()
        if ($script:activeProcess.HasExited) { throw 'Isolated app exited during startup.' }
        if ($script:activeProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($script:activeProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Isolated app window did not appear.' }
    [void](Wait-Element 'NavAbout' 35)
}
function Close-TestApp {
    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        try { $script:activeProcess.Kill($true); [void]$script:activeProcess.WaitForExit(8000) } catch { }
    }
    $script:activeProcess = $null
}
function Write-ScenarioFixture([string]$DataDirectory, [string]$Language, [string]$Theme, [string]$PolicyVersion) {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = ''; AgentExecutable = 'unused'; AgentArguments = ''; AgentMode = 'exec'; AgentProvider = 'custom'
        AutoConnectProvider = 'none'; TrustedWorkspaceRoots = ''; AgentPermissionMode = 'inherit'; RequireApproval = $true
        ChatApprovalMode = 'first'; ChatResultMode = 'manual'; FileAttachmentApprovalMode = 'always'
    }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{ PolicyVersion = $PolicyVersion; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language = $Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme = $Theme }
    Write-Json (Join-Path $DataDirectory 'tips.json') @{ Seen = @('chat-connect') }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{ schemaVersion = 1; automaticChecksEnabled = $false; includePrereleases = $false; lastAttemptUtc = $null; availableRelease = $null }
    Write-Json (Join-Path $DataDirectory 'pricing-auto.json') @{ schemaVersion = 1; autoUpdateEnabled = $false }

    $auditTimestamp = [DateTimeOffset]::UtcNow.AddDays(-37)
    Write-Json (Join-Path $DataDirectory 'audit.json') @{
        SchemaVersion = 1
        Records = @(@{ Timestamp = $auditTimestamp.ToString('o'); Kind = 'completed'; ExecutionId = $null; Source = 'local'; ChannelId = ''; ThreadId = '' })
    }
    $historyTimestamp = [DateTimeOffset]::UtcNow.AddDays(-40)
    $script:seededHistoryTimestamp = $historyTimestamp
    Write-Json (Join-Path $DataDirectory 'history.json') @{
        SchemaVersion = 1
        Records = @(@{
            Id = [guid]::NewGuid().ToString(); StartedAt = $historyTimestamp.ToString('o'); FinishedAt = $historyTimestamp.AddSeconds(4).ToString('o')
            Source = 'local'; ChannelId = ''; ThreadId = ''; UserId = ''; WorkspaceRoot = ''; Status = 'completed'
            ExitCode = 0; SessionId = $null; AgentModel = $null; AgentReasoningEffort = $null; Usage = $null; Metrics = $null
        })
    }
}
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}
function Read-PreferenceFile {
    $path = Join-Path $script:activeDataDirectory 'log-retention.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Retention preference file was not created.' }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Wait-PreferenceFile {
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        if (Test-Path -LiteralPath (Join-Path $script:activeDataDirectory 'log-retention.json') -PathType Leaf) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Retention preference file was not saved in time.'
}
function Test-Scenario([string]$Language, [string]$Theme, [int]$Width, [string]$PolicyVersion) {
    $script:currentScenario = "$Language-$Theme-$Width"
    $dataDirectory = Join-Path $testRoot "data-$script:currentScenario"
    Write-ScenarioFixture $dataDirectory $Language $Theme $PolicyVersion
    try {
        $script:currentStep = 'initial launch and navigation'
        Start-TestApp $dataDirectory
        Invoke-Element 'NavAbout'
        Set-WindowDips $Width
        Scroll-AboutToEnd
        [void](Scroll-TargetIntoView 'AdvancedSettingsExpander')
        $advancedPattern = Get-ExpanderPattern 'AdvancedSettingsExpander'
        Assert-Equal ([Windows.Automation.ExpandCollapseState]::Collapsed) $advancedPattern.Current.ExpandCollapseState 'Advanced settings should start collapsed.'
        $advancedElement = Wait-Element 'AdvancedSettingsHeading'
        $advancedName = $advancedElement.Current.Name
        if ($advancedName -notmatch 'Advanced settings|詳細設定') { throw "Advanced settings heading is missing or incorrectly localized: '$advancedName'." }
        Screenshot '01-collapsed'

        $script:currentStep = 'collapsed controls and default values'
        $applyWhenCollapsed = Element 'LogRetentionApplyButton'
        if ($applyWhenCollapsed -and -not $applyWhenCollapsed.Current.IsOffscreen) { throw 'Apply button is visible before expanding Advanced settings.' }
        $script:currentStep = 'expand shared advanced settings and inspect log section'
        $advancedPattern.Expand()
        Start-Sleep -Milliseconds 200
        Screenshot '02-expand-requested'
        $expanded = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 100
            if ($advancedPattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) { $expanded = $true; break }
        }
        if (-not $expanded) { throw 'Advanced settings did not expand.' }
        [void](Scroll-TargetIntoView 'LogManagementHeading')
        $logElement = Element 'LogManagementHeading'
        $logName = $logElement.Current.Name
        if ($logName -notmatch 'Log management|ログ管理') { throw "Log management heading is missing or incorrectly localized: '$logName'." }
        Scroll-AboutToEnd
        Screenshot '03-after-scroll'
        [void](Scroll-TargetIntoView 'LogRetentionApplyButton')
        Screenshot '02-expanded'
        foreach ($id in @('LogRetentionAuditDays','LogRetentionApplyButton')) { Assert-InWindow $id }

        if ($LayoutOnly) {
            $script:currentStep = 'collapse and reopen shared advanced settings'
            $advancedPattern.Collapse()
            Start-Sleep -Milliseconds 250
            Assert-Equal ([Windows.Automation.ExpandCollapseState]::Collapsed) $advancedPattern.Current.ExpandCollapseState 'Advanced settings did not collapse.'
            $hiddenApply = Element 'LogRetentionApplyButton'
            if ($hiddenApply -and -not $hiddenApply.Current.IsOffscreen) { throw 'Log actions remained visible after collapsing Advanced settings.' }
            $advancedPattern.Expand()
            Start-Sleep -Milliseconds 250
            Assert-Equal ([Windows.Automation.ExpandCollapseState]::Expanded) $advancedPattern.Current.ExpandCollapseState 'Advanced settings did not reopen.'
            [void](Scroll-TargetIntoView 'LogManagementHeading')
            [void](Scroll-TargetIntoView 'LogRetentionApplyButton')
            Assert-InWindow 'LogRetentionApplyButton'
            Screenshot '04-reopened'
            Record-Pass "$currentScenario advanced hierarchy, default collapsed state, log section, and narrow layout"
            return
        }

        Scroll-AboutToStart
        Assert-Equal 1825 (Read-Number 'LogRetentionStatisticsDays') 'Statistics retention default is incorrect.'
        Assert-Equal 365 (Read-Number 'LogRetentionAuditDays') 'Audit retention default is incorrect.'
        Assert-Equal 30 (Read-Number 'LogRetentionHistoryDays') 'History retention default is incorrect.'
        foreach ($id in @('LogRetentionStatisticsDays','LogRetentionAuditDays','LogRetentionAuditSegmentSizeMb','LogRetentionAuditMaxSegments',
            'LogRetentionHistoryDays','LogRetentionHistoryMaxRecords','LogRetentionConversationDays','LogRetentionDiagnosticsDays',
            'LogRetentionDiagnosticsFileKiB','LogRetentionDiagnosticsMaxFiles','LogRetentionApplyButton')) {
            Assert-InWindow $id
        }

        $script:currentStep = 'edit retention values'
        Set-Number 'LogRetentionAuditDays' 1
        Set-Number 'LogRetentionHistoryDays' 1
        [void](Scroll-TargetIntoView 'LogRetentionApplyButton')
        (Element 'LogRetentionApplyButton').SetFocus()
        Assert-Equal 1 (Read-Number 'LogRetentionAuditDays') 'Audit retention edit did not commit.'
        Assert-Equal 1 (Read-Number 'LogRetentionHistoryDays') 'History retention edit did not commit.'
        $preferencePath = Join-Path $dataDirectory 'log-retention.json'
        $beforeExists = Test-Path -LiteralPath $preferencePath -PathType Leaf
        $beforeHash = if ($beforeExists) { (Get-FileHash -LiteralPath $preferencePath -Algorithm SHA256).Hash } else { $null }

        $script:currentStep = 'confirm reduction and cancel'
        (Element 'LogRetentionApplyButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-Dialog $true)
        Screenshot '03-confirmation'
        $applyDuringDialog = Element 'LogRetentionApplyButton'
        if (-not $applyDuringDialog -or $applyDuringDialog.Current.IsEnabled) {
            throw 'Apply should be disabled while a destructive confirmation is open.'
        }
        try { $applyDuringDialog.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        Start-Sleep -Milliseconds 250
        Assert-Equal 1 (Count-Elements 'LogRetentionReductionDialog') 'A second reduction dialog opened.'
        (Wait-Element 'CloseButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 250
        [void](Wait-Dialog $false)
        $afterCancelExists = Test-Path -LiteralPath $preferencePath -PathType Leaf
        Assert-Equal $beforeExists $afterCancelExists 'Cancel changed preference-file presence.'
        if ($beforeExists) {
            $afterCancelHash = (Get-FileHash -LiteralPath $preferencePath -Algorithm SHA256).Hash
            Assert-Equal $beforeHash $afterCancelHash 'Cancel changed the saved preferences.'
        }
        Record-Pass "$currentScenario collapsed defaults, reduction cancel, and duplicate-dialog guard"

        $script:currentStep = 'apply retention and inspect maintenance results'
        [void](Scroll-TargetIntoView 'LogRetentionApplyButton')
        (Element 'LogRetentionApplyButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-Dialog $true)
        (Wait-Element 'PrimaryButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-Dialog $false)
        Wait-PreferenceFile
        $saved = Read-PreferenceFile
        Assert-Equal 1 $saved.Preferences.AuditRetentionDays 'Audit setting was not persisted.'
        Assert-Equal 1 $saved.Preferences.HistoryRetentionDays 'History setting was not persisted.'
        Assert-Equal 1825 $saved.Preferences.StatisticsRetentionDays 'Statistics retention changed unexpectedly.'
        Assert-Equal 4 $saved.Preferences.AuditSegmentSizeMb 'Audit segment size changed unexpectedly.'
        Assert-Equal 16 $saved.Preferences.AuditMaxSegments 'Audit segment count changed unexpectedly.'
        Assert-Equal 500 $saved.Preferences.HistoryMaxRecords 'History record cap changed unexpectedly.'
        Assert-Equal 30 $saved.Preferences.ConversationRetentionDays 'Conversation retention changed unexpectedly.'
        Assert-Equal 7 $saved.Preferences.DiagnosticsRetentionDays 'Diagnostics retention changed unexpectedly.'
        Assert-Equal 128 $saved.Preferences.DiagnosticsFileKiB 'Diagnostics file cap changed unexpectedly.'
        Assert-Equal 4 $saved.Preferences.DiagnosticsMaxFiles 'Diagnostics file count changed unexpectedly.'

        $status = Wait-Element 'LogRetentionStatus'
        $statusNames = @($status.Current.Name)
        $statusChildren = $status.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        for ($index = 0; $index -lt $statusChildren.Count; $index++) { $statusNames += $statusChildren.Item($index).Current.Name }
        $statusText = ($statusNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' '
        if ($status.Current.IsOffscreen -or $statusText -notmatch 'applied|適用') { throw "Successful status was not exposed to UI Automation: '$statusText'." }
        $auditSegments = @(Get-ChildItem -LiteralPath $dataDirectory -File -Filter 'audit-segment-*.jsonl' -ErrorAction SilentlyContinue)
        foreach ($segment in $auditSegments) {
            foreach ($line in Get-Content -LiteralPath $segment.FullName) {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    $entry = $line | ConvertFrom-Json
                    if ([DateTimeOffset]::Parse($entry.Timestamp) -lt [DateTimeOffset]::UtcNow.AddDays(-1)) { throw 'An expired audit event remained after apply.' }
                }
            }
        }
        $historyPath = Join-Path $dataDirectory 'history.json'
        if (Test-Path -LiteralPath $historyPath) {
            $history = Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
            if (@($history.Records | Where-Object { [DateTimeOffset]::Parse($_.StartedAt) -lt [DateTimeOffset]::UtcNow.AddDays(-1) }).Count -gt 0) {
                throw 'An expired execution remained in history after apply.'
            }
        }
        $statsPath = Join-Path $dataDirectory 'activity-statistics.json'
        if (-not (Test-Path -LiteralPath $statsPath -PathType Leaf)) { throw 'Statistics migration did not create its durable snapshot.' }
        $statistics = Get-Content -LiteralPath $statsPath -Raw | ConvertFrom-Json
        if (-not $statistics.MigrationCompleted) { throw 'Statistics migration did not commit.' }
        $migratedDay = $script:seededHistoryTimestamp.ToLocalTime().Date.ToString('yyyy-MM-dd')
        $day = @($statistics.Days | Where-Object { ([DateTime]$_.Date).ToString('yyyy-MM-dd') -eq $migratedDay })
        if ($day.Count -ne 1 -or $day[0].Total -ne 1) { throw 'The 40-day execution was not preserved exactly once in long-retained statistics.' }
        if (Test-Path -LiteralPath (Join-Path $dataDirectory 'audit.json')) { throw 'The legacy audit file remained after migration.' }
        Record-Pass "$currentScenario persisted policy, pruned old detail, and retained migrated statistics"
        Screenshot '04-applied'

        $script:currentStep = 'restart and verify persistence'
        Close-TestApp
        Start-TestApp $dataDirectory
        Invoke-Element 'NavAbout'
        Set-WindowDips $Width
        [void](Scroll-TargetIntoView 'AdvancedSettingsExpander')
        $restartExpander = Get-ExpanderPattern 'AdvancedSettingsExpander'
        if ($restartExpander.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed) { $restartExpander.Collapse() }
        $restartExpander.Expand()
        Start-Sleep -Milliseconds 250
        Assert-Equal ([Windows.Automation.ExpandCollapseState]::Expanded) $restartExpander.Current.ExpandCollapseState 'Advanced settings did not reopen after restart.'
        [void](Scroll-TargetIntoView 'LogManagementHeading')
        Assert-Equal 1 (Read-Number 'LogRetentionAuditDays') 'Audit retention did not reload after restart.'
        Assert-Equal 1 (Read-Number 'LogRetentionHistoryDays') 'History retention did not reload after restart.'
        $restartedStats = Get-Content -LiteralPath $statsPath -Raw | ConvertFrom-Json
        $restartedDay = @($restartedStats.Days | Where-Object { ([DateTime]$_.Date).ToString('yyyy-MM-dd') -eq $migratedDay })
        if ($restartedDay.Count -ne 1 -or $restartedDay[0].Total -ne 1) { throw 'Restart duplicated or lost the migrated statistics day.' }
        Record-Pass "$currentScenario restored retention values and kept migration idempotent after restart"
        Screenshot '05-restarted'
    }
    catch {
        try { Screenshot 'failure' } catch { }
        $detail = "$($_.Exception.GetType().FullName): $($_.Exception.Message) position=$($_.InvocationInfo.PositionMessage) stack=$($_.ScriptStackTrace) step=$script:currentStep"
        $detail = $detail -replace [regex]::Escape($artifactRootFull), '<artifact>' -replace [regex]::Escape($testRoot), '<fixture>'
        Record-Fail "$script:currentScenario ($script:currentStep)" $detail
    }
    finally { Close-TestApp }
}

try {
    $consentPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $consentMatch = [regex]::Match((Get-Content -LiteralPath $consentPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $consentMatch.Success) { throw 'Could not locate current consent policy version.' }
    $policyVersion = $consentMatch.Groups[1].Value
    $scenarios = @(
        @{ Language = 'ja'; Theme = 'light'; Width = 500; Name = 'ja-light-500' },
        @{ Language = 'ja'; Theme = 'dark'; Width = 600; Name = 'ja-dark-600' },
        @{ Language = 'en'; Theme = 'light'; Width = 600; Name = 'en-light-600' },
        @{ Language = 'en'; Theme = 'dark'; Width = 500; Name = 'en-dark-500' }
    )
    foreach ($scenario in $scenarios) {
        if ($OnlyScenario.Count -gt 0 -and $scenario.Name -notin $OnlyScenario) { continue }
        Test-Scenario $scenario.Language $scenario.Theme $scenario.Width $policyVersion
    }
}
catch {
    Record-Fail 'log retention UI smoke setup' "$($_.Exception.GetType().FullName): $($_.Exception.Message)"
}

Write-Json (Join-Path $testRoot 'results.json') $results.ToArray()
$failures = @($results | Where-Object status -eq 'FAIL').Count
Write-Output "Passed: $($results.Count - $failures) | Failed: $failures"
Write-Output "Isolated screenshots and results: $testRoot"
if ($failures -gt 0) { exit 1 }
