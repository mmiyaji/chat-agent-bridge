param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [int]$TimeoutSeconds = 30
)

# Isolated UI regression for model-catalog age guidance and its persistence. Only
# this explicitly supplied desktop build and the copied test-agent apphost are run.
$ErrorActionPreference = 'Stop'
$appDirectoryFull = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $appDirectoryFull 'ChatAgentBridge.Desktop.exe'
$testAgentFull = [IO.Path]::GetFullPath($TestAgentPath)
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Desktop executable not found: $executable" }
if (-not (Test-Path -LiteralPath $testAgentFull -PathType Leaf)) { throw "Test agent executable not found: $testAgentFull" }

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$testRoot = Join-Path $artifactDirectory ('model-catalog-freshness-ui-' + [guid]::NewGuid().ToString('N'))
$fixtureBin = Join-Path $testRoot 'fixture-bin'
New-Item -ItemType Directory -Force -Path $fixtureBin | Out-Null
# The fake CLI is a framework-dependent publish directory: keep all adjacent
# runtime/dependency files and add agy.exe as a copy of its apphost.
Get-ChildItem -LiteralPath (Split-Path $testAgentFull) -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $fixtureBin -Recurse -Force }
$fixtureExe = Join-Path $fixtureBin 'agy.exe'
Copy-Item -LiteralPath $testAgentFull -Destination $fixtureExe -Force

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class CatalogAgeWindow { [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags); }'
$script:startedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:results = [Collections.Generic.List[object]]::new()
$script:activeProcess = $null
$script:activeWindow = $null
$script:fatal = $null

function Capture-Metadata([string]$Name) {
    $scroll = Wait-Element 'AgentSettingsScroll'
    $pattern = [Windows.Automation.ScrollPattern]$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($Name -like '*500*') { $pattern.SetScrollPercent(-1,50); Start-Sleep -Milliseconds 400 }
    for ($i=0; $i -lt 20; $i++) {
        $label = Wait-Element 'ModelCatalogFetchedAt'; $r=$label.Current.BoundingRectangle; $w=$script:activeWindow.Current.BoundingRectangle
        if (-not $label.Current.IsOffscreen -and $r.Top -ge $w.Top -and $r.Bottom -le ($w.Bottom - 24)) { break }
        $direction = if ($r.Top -lt $w.Top) { [Windows.Automation.ScrollAmount]::SmallDecrement } else { [Windows.Automation.ScrollAmount]::SmallIncrement }
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,$direction)
        Start-Sleep -Milliseconds 150
    }
    foreach($id in @('ModelCatalogFetchedAt','ModelCatalogRefreshRecommended')) {
        $element=Wait-Element $id; $r=$element.Current.BoundingRectangle; $w=$script:activeWindow.Current.BoundingRectangle
        if($element.Current.IsOffscreen -or $r.IsEmpty -or $r.Left -lt $w.Left -or $r.Right -gt $w.Right -or $r.Top -lt $w.Top -or $r.Bottom -gt ($w.Bottom - 24)) { throw "Metadata '$id' is clipped in $Name." }
    }
    $rect=$script:activeWindow.Current.BoundingRectangle
    $bitmap=[Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
    try { $graphics=[Drawing.Graphics]::FromImage($bitmap); try { $graphics.CopyFromScreen([int]$rect.Left,[int]$rect.Top,0,0,[Drawing.Size]::new([int]$rect.Width,[int]$rect.Height)) } finally {$graphics.Dispose()}; $bitmap.Save((Join-Path $testRoot $Name),[Drawing.Imaging.ImageFormat]::Png) } finally {$bitmap.Dispose()}
}
function Add-Pass([string]$Name, [string]$Detail = '') {
    $script:results.Add(@{ name=$Name; status='PASS'; detail=$Detail }); Write-Host ("PASS " + $Name)
}
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
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
function Wait-Element([string]$Id, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $scope = $script:activeWindow
        if ($Id -in @('ModelCatalogFetchedAt','ModelCatalogRefreshRecommended')) { $pane=Find-Element $script:activeWindow 'AgentSettingsScroll'; if($pane){$scope=$pane} }
        $element = Find-Element $scope $Id
        if ($element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for AutomationId '$Id'."
}
function Get-Text([string]$Id) { return (Wait-Element $Id).Current.Name }
function Wait-Text([string]$Id, [scriptblock]$Predicate, [string]$Description, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $text = Get-Text $Id
        if (& $Predicate $text) { return $text }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Id $Description; last text='$text'."
}
function Invoke-Element([string]$Id) {
    $element = Wait-Element $Id
    if (-not $element.Current.IsEnabled) { throw "UI element is disabled: $Id" }
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return
    }
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.InvokePattern]$pattern).Invoke(); return
    }
    throw "No UI Automation selection or invoke pattern is available: $Id"
}
function Write-Json([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $Path -Encoding utf8 }
function New-Fixture([string]$DataDirectory, [string]$Workspace, [string]$Language = 'en', [string]$Theme = 'light') {
    New-Item -ItemType Directory -Force -Path $DataDirectory, $Workspace | Out-Null
    Write-Json (Join-Path $DataDirectory 'settings.json') @{
        SchemaVersion=1; WorkspaceRoot=$Workspace; AgentExecutable=$fixtureExe; AgentArguments='--output-format stream-json'
        AgentMode='exec'; AgentProvider='antigravity'; AgentModel=''; AgentReasoningEffort=''; AutoConnectProvider='none'
        RequireApproval=$true; AgentPermissionMode='inherit'; AllowAgentNetwork=$false; ChatApprovalMode='first'
        AutoApproveSwitchCommands=$false; ChatResultMode='manual'; ChatFollowUpMode='queue'; FileAttachmentApprovalMode='always'
    }
    Write-Json (Join-Path $DataDirectory 'tips.json') @{ Seen=@('chat-connect') }
    Write-Json (Join-Path $DataDirectory 'onboarding.json') @{ schemaVersion=3; step=4; status='completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policyMatch = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"')
    if (-not $policyMatch.Success) { throw 'Could not read CurrentPolicyVersion.' }
    Write-Json (Join-Path $DataDirectory 'consent.json') @{ PolicyVersion=$policyMatch.Groups[1].Value; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $DataDirectory 'language.json') @{ Language=$Language }
    Write-Json (Join-Path $DataDirectory 'appearance.json') @{ Theme=$Theme }
    Write-Json (Join-Path $DataDirectory 'portable-update.json') @{ schemaVersion=1; automaticChecksEnabled=$false; includePrereleases=$true; lastAttemptUtc=$null; availableRelease=$null }
}
function Start-TestApp([string]$DataDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $appDirectoryFull
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add([IO.Path]::GetFullPath($DataDirectory))
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Desktop process could not be started.' }
    $script:startedProcesses.Add($process); $script:activeProcess = $process
    $script:activeWindow = Wait-MainWindow $process
    try { $script:activeWindow.SetFocus() } catch [InvalidOperationException] { }
    Start-Sleep -Milliseconds 300
}
function Stop-TestApp {
    if ($script:activeProcess -and -not $script:activeProcess.HasExited) {
        $script:activeProcess.Kill($true)
        if (-not $script:activeProcess.WaitForExit(7000)) { throw 'Isolated app process did not exit during cleanup.' }
    }
    $script:activeProcess = $null; $script:activeWindow = $null
}
function Open-AntigravityEditor {
    Invoke-Element 'NavAgentSettings'
    Start-Sleep -Milliseconds 300
    Invoke-Element 'AgentScopeAntigravity'
    [void](Wait-Element 'AgentExecutable')
    [void](Wait-Element 'CheckCli')
    [void](Wait-Element 'ModelCatalogFetchedAt')
}
function Wait-Cache([string]$CachePath, [scriptblock]$Predicate, [string]$Description, [int]$Seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Test-Path -LiteralPath $CachePath -PathType Leaf) {
            try {
                $cache = Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json -DateKind String
                if (& $Predicate $cache) { return $cache }
            } catch { }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for cache $Description."
}
function Test-RefreshIcon([bool]$ExpectedVisible, [string]$ExpectedTooltip = '') {
    $pane=Find-Element $script:activeWindow 'AgentSettingsScroll'
    $icon = if($pane){Find-Element $pane 'ModelCatalogRefreshRecommended'}else{$null}
    if (-not $icon) {
        if ($ExpectedVisible) { throw 'Refresh recommendation indicator is missing from the UI tree.' }
        return
    }
    if ((-not $icon.Current.IsOffscreen) -ne $ExpectedVisible) { throw "Refresh recommendation visibility did not match expected $ExpectedVisible." }
    $tooltipText = $icon.Current.HelpText + ' ' + $icon.Current.Name
    if ($ExpectedTooltip -and $tooltipText -notmatch [regex]::Escape($ExpectedTooltip)) {
        throw "Refresh recommendation tooltip/name did not contain the expected text: $tooltipText"
    }
}
function Expand-AndCheckModels([string[]]$ExpectedNames) {
    $combo = Wait-Element 'ModelCandidates'
    $expand = [Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        # Let the ComboBox create its popup children, then scope discovery to this process.
        Start-Sleep -Milliseconds 300
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:activeProcess.Id)
            $children = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
            $names = @($children | ForEach-Object { $_.Current.Name })
            $missing = @($ExpectedNames | Where-Object { $name = $_; -not ($names | Where-Object { $_ -match [regex]::Escape($name) }) })
            if ($missing.Count -eq 0) { return }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Model dropdown did not contain expected choices: $($missing -join ', ')."
    } finally { $expand.Collapse() }
}
function Get-Cache([string]$CachePath) { return Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json -DateKind String }
function Set-Cache([string]$CachePath, [object]$Cache) { Write-Json $CachePath $Cache }
function Assert-FreshLabel([DateTimeOffset]$FetchedAt, [string]$Language = 'en') {
    $label = Wait-Text 'ModelCatalogFetchedAt' { param($v) -not [string]::IsNullOrWhiteSpace($v) } 'to show the model retrieval date'
    $expected = $FetchedAt.ToLocalTime().ToString('g', [Globalization.CultureInfo]::CurrentCulture)
    if ($label -notlike "*$expected*") { throw "Freshness label did not show persisted local timestamp '$expected': '$label'." }
    if ($Language -eq 'ja' -and $label -notmatch '取得|更新|モデル') { throw "Japanese freshness caption was not localized: '$label'." }
    return $label
}

try {
    $dataDirectory = Join-Path $testRoot 'data-en'
    $workspace = Join-Path $testRoot 'workspace'
    New-Fixture $dataDirectory $workspace 'en' 'light'
    $settingsPath = Join-Path $dataDirectory 'settings.json'
    $settingsHashBefore = (Get-FileHash -LiteralPath $settingsPath).Hash
    $cachePath = Join-Path $dataDirectory 'agent-cli-cache/antigravity/cli-verification.json'

    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    Invoke-Element 'CheckCli'
    [void](Wait-Text 'AgentEditorStatus' { param($v) $v -match 'CLI verified|CLI確認済み' } 'to complete the Antigravity CLI check')
    $freshCache = Wait-Cache $cachePath { param($c) @($c.Models).Count -ge 2 -and $null -ne $c.ModelsFetchedAtUtc } 'to persist the catalog and retrieval timestamp'
    $checkedAt = [DateTimeOffset]::Parse($freshCache.CheckedAtUtc.ToString())
    $firstFetchedAt = [DateTimeOffset]::Parse($freshCache.ModelsFetchedAtUtc.ToString())
    if (@($freshCache.Models).Count -ne 2) { throw 'Fake Antigravity CLI did not provide exactly two model choices.' }
    if (-not $freshCache.Probe.ProviderRecognized -or [string]::IsNullOrWhiteSpace($freshCache.Probe.Version)) { throw 'CLI check did not persist a successful recognized probe.' }
    $freshText = Assert-FreshLabel $firstFetchedAt
    Test-RefreshIcon $false
    Expand-AndCheckModels @('fixture-agy-fast', 'fixture-agy-deep')
    Add-Pass 'Successful CLI check displays a fresh fetched-at date and no refresh recommendation' $freshText
    Add-Pass 'Fresh model dropdown contains both fake Antigravity choices'

    # A stale but valid cache should retain the old timestamp and candidates at startup.
    Stop-TestApp
    $staleTime = [DateTimeOffset]::UtcNow.AddDays(-8)
    $cache = Get-Cache $cachePath
    $cache.ModelsFetchedAtUtc = $staleTime.ToString('o')
    Set-Cache $cachePath $cache
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    $staleText = Assert-FreshLabel $staleTime
    Test-RefreshIcon $true 'Refresh recommended'
    Expand-AndCheckModels @('fixture-agy-fast', 'fixture-agy-deep')
    Capture-Metadata 'en-light-stale.png'
    Add-Pass 'Eight-day-old cache displays its persisted date, candidates, and refresh recommendation' $staleText

    # An ordinary Fetch models action updates only the model retrieval timestamp.
    $oldCheckedAt = [DateTimeOffset]::Parse((Get-Cache $cachePath).CheckedAtUtc.ToString())
    Invoke-Element 'FetchModels'
    [void](Wait-Text 'ModelCatalogStatus' { param($v) $v -match '2 suggestions|2 件|2件' } 'for the manual catalog refresh to finish')
    $refreshedCache = Wait-Cache $cachePath { param($c) $null -ne $c.ModelsFetchedAtUtc -and [DateTimeOffset]::Parse($c.ModelsFetchedAtUtc.ToString()) -gt $staleTime } 'to update ModelsFetchedAtUtc after manual refresh'
    $refreshedAt = [DateTimeOffset]::Parse($refreshedCache.ModelsFetchedAtUtc.ToString())
    $checkedAfterRefresh = [DateTimeOffset]::Parse($refreshedCache.CheckedAtUtc.ToString())
    if ($checkedAfterRefresh -ne $oldCheckedAt) { throw 'Manual catalog refresh changed CheckedAtUtc.' }
    $freshAfterRefresh = Assert-FreshLabel $refreshedAt
    Test-RefreshIcon $false
    Add-Pass 'Manual catalog refresh persists its date without changing the CLI checked-at timestamp' $freshAfterRefresh

    Stop-TestApp
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    [void](Assert-FreshLabel $refreshedAt)
    Test-RefreshIcon $false
    Add-Pass 'Fresh date and recommendation state survive an app restart'

    # A schema-compatible older cache with no retrieval timestamp recommends refresh.
    Stop-TestApp
    $cache = Get-Cache $cachePath
    $cache.ModelsFetchedAtUtc = $null
    Set-Cache $cachePath $cache
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    $unknownText = Wait-Text 'ModelCatalogFetchedAt' { param($v) $v -match 'unknown|date unknown|不明|未取得|不詳' } 'to show an unknown retrieval date'
    Test-RefreshIcon $true
    Expand-AndCheckModels @('fixture-agy-fast', 'fixture-agy-deep')
    Add-Pass 'Missing retrieval timestamp displays unknown and recommends refresh' $unknownText

    # Exercise a launch failure after the cache is already restored into the UI. Renaming
    # only this test's apphost makes the lookup fail while leaving the loaded candidates intact.
    Stop-TestApp
    $cache = Get-Cache $cachePath
    $cache.ModelsFetchedAtUtc = $staleTime.ToString('o')
    Set-Cache $cachePath $cache
    Start-TestApp $dataDirectory
    Open-AntigravityEditor
    [void](Assert-FreshLabel $staleTime)
    Test-RefreshIcon $true
    $offlineExe = $fixtureExe + '.offline'
    Move-Item -LiteralPath $fixtureExe -Destination $offlineExe
    try {
        Invoke-Element 'FetchModels'
        [void](Wait-Text 'ModelCatalogStatus' { param($v) $v -match 'could not be started|not return a usable|取得できませんでした|起動できません' } 'to report the isolated fake CLI launch failure')
        [void](Assert-FreshLabel $staleTime)
        Test-RefreshIcon $true
        Expand-AndCheckModels @('fixture-agy-fast', 'fixture-agy-deep')
        $afterFailure = Get-Cache $cachePath
        if ([DateTimeOffset]::Parse($afterFailure.ModelsFetchedAtUtc.ToString()) -ne $staleTime) { throw 'Failed refresh changed the persisted old fetched-at timestamp.' }
        Add-Pass 'Failed refresh retains the old model choices and old date with its recommendation'
    } finally {
        if (Test-Path -LiteralPath $offlineExe -PathType Leaf) { Move-Item -LiteralPath $offlineExe -Destination $fixtureExe }
    }

    # Check layout in both supported locales and themes, with the model row at 500 px.
    Stop-TestApp
    $jaData = Join-Path $testRoot 'data-ja'
    New-Fixture $jaData $workspace 'ja' 'dark'
    $jaCachePath = Join-Path $jaData 'agent-cli-cache/antigravity/cli-verification.json'
    New-Item -ItemType Directory -Force -Path (Split-Path $jaCachePath) | Out-Null
    Copy-Item -LiteralPath $cachePath -Destination $jaCachePath
    Start-TestApp $jaData
    Open-AntigravityEditor
    [void](Assert-FreshLabel $staleTime 'ja')
    Test-RefreshIcon $true
    $handle=[IntPtr]$script:activeWindow.Current.NativeWindowHandle
    $scale=[CatalogAgeWindow]::GetDpiForWindow($handle)/96.0
    if(-not [CatalogAgeWindow]::SetWindowPos($handle,[IntPtr]::Zero,30,30,[int](500*$scale),[int](800*$scale),0x0004 -bor 0x0010)) { throw 'Could not resize the isolated app.' }
    Start-Sleep -Milliseconds 800
    $script:activeWindow=Wait-MainWindow $script:activeProcess
    Capture-Metadata 'ja-dark-500.png'
    Add-Pass 'Japanese dark-mode freshness controls fit inside a narrow 500 px window'
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHashBefore) { throw 'Model catalog freshness checks changed the isolated source profile settings.' }
    Add-Pass 'CLI/model freshness diagnostics leave profile settings untouched'
    Stop-TestApp
    Add-Pass 'Model catalog freshness UI smoke completed' $testRoot
}
catch {
    $script:fatal = $_.Exception.Message + "`n" + $_.ScriptStackTrace
    $script:results.Add(@{ name='Model catalog freshness UI regression'; status='FAIL'; detail=$script:fatal })
    Write-Error $_
}
finally {
    foreach ($process in $script:startedProcesses) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(7000) | Out-Null } } catch { }
    }
    try { Write-Json (Join-Path $testRoot 'results.json') $script:results.ToArray() } catch { Write-Warning "Could not save UI smoke results: $_" }
}
if ($script:fatal) { throw $script:fatal }
Write-Output "Model catalog freshness UI artifacts: $testRoot"
