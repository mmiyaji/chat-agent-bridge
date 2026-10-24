# Focused native UI smoke and screenshot evidence for the Inbox gallery.
# Requires an unpackaged portable desktop publish and a self-contained offline TestAgent.
# Every process, setting, request, file, and screenshot is scoped to desktop/artifacts/<guid>.
param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'artifacts/inbox-gallery-app'),
    [string]$TestAgentPath = (Join-Path $PSScriptRoot 'artifacts/inbox-gallery-testagent/ChatAgentBridge.TestAgent.exe'),
    [ValidateSet('both', 'en', 'ja')]
    [string]$Language = 'both',
    [ValidateSet('both', 'light', 'dark')]
    [string]$Theme = 'both',
    [ValidateRange(6, 500)]
    [int]$RestoredConversationCount = 6
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AppDirectory).Path)
$agentPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $TestAgentPath).Path)
if (-not [IO.Path]::IsPathFullyQualified($AppDirectory) -or -not [IO.Path]::IsPathFullyQualified($TestAgentPath)) {
    throw 'AppDirectory and TestAgentPath must be absolute paths.'
}
if (-not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
    throw 'Use an isolated unpackaged portable publish under desktop/artifacts.'
}
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'ChatAgentBridge.Desktop.exe') -PathType Leaf)) {
    throw "Desktop executable was not found: $AppDirectory"
}
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $agentPath) 'coreclr.dll'))) {
    throw "Use a self-contained win-x64 TestAgent publish: $TestAgentPath"
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InboxGalleryNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
}
'@

# Reuse the isolated loopback Mattermost and UIA primitives from the connection smoke.
# Evaluate only its helper declarations; its test body is not executed.
$script:PSScriptRootForFixture = $PSScriptRoot
$connectionSmoke = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ConnectionSettingsUiSmoke.ps1') -Raw
$functionStart = $connectionSmoke.IndexOf('function Assert(')
$testStart = [regex]::Match($connectionSmoke, '(?m)^try \{\r?\n    Start-IsolatedApp')
if ($functionStart -lt 0 -or -not $testStart.Success) { throw 'Could not extract isolated connection smoke helpers.' }
$helperSource = $connectionSmoke.Substring($functionStart, $testStart.Index - $functionStart)
$helperSource = $helperSource.Replace('$PSScriptRoot', '$PSScriptRootForFixture')
$helperSource = $helperSource.Replace('ConnectionSmokeNative', 'InboxGalleryNative')
$helperSource = $helperSource.Replace("ChatApprovalMode = 'auto'", "ChatApprovalMode = 'always'")
. ([scriptblock]::Create($helperSource))

$script:app = $null
$script:fixture = $null
$script:window = $null
$script:results = [Collections.Generic.List[object]]::new()
$script:evidenceRoots = [Collections.Generic.List[string]]::new()
$script:scenarioRoot = $null
$script:scenarioName = $null

function Add-Check([string]$Name, [string]$Status, [string]$Detail = '') {
    $script:results.Add(@{ name = $Name; status = $Status; detail = $Detail })
    Write-Output "$Status $Name"
}

function Get-ElementText([Windows.Automation.AutomationElement]$Element) {
    $values = [Collections.Generic.List[string]]::new()
    if ($Element -and -not [string]::IsNullOrWhiteSpace($Element.Current.Name)) { $values.Add($Element.Current.Name) }
    if ($Element) {
        $value = $null
        if ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value)) {
            $values.Add($value.Current.Value)
        }
    }
    if ($Element) {
        foreach ($child in $Element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) { $values.Add($child.Current.Name) }
        }
    }
    $values -join "`n"
}

function Get-ListItems([string]$AutomationId) {
    $list = Wait-Element $AutomationId
    @($list.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem)))
}

function Select-ListItem([Windows.Automation.AutomationElement]$Item, [string]$Description) {
    $pattern = $null
    if ($Item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.SelectionItemPattern]$pattern).Select()
    } else { Invoke-Element $Item $Description }
    $invoke = $null
    if ($Item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        ([Windows.Automation.InvokePattern]$invoke).Invoke()
    } else {
        Activate-AppWindow
        $Item.SetFocus()
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    Start-Sleep -Milliseconds 250
}

function Find-ListItemContaining([string]$AutomationId, [string]$Text, [int]$Seconds = 10) {
    $list = Wait-Element $AutomationId
    $scroll = $null
    if ($list.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) {
        $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 0)
        Start-Sleep -Milliseconds 150
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        foreach ($item in (Get-ListItems $AutomationId)) {
            if ((Get-ElementText $item).Contains($Text, [StringComparison]::Ordinal)) { return $item }
        }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Get-InboxRowRuntimeIds {
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $list = Find-Element $script:window 'InboxConversations'
    if ($list) {
        $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem))
        foreach ($item in $items) { [void]$ids.Add([string]::Join(',', $item.GetRuntimeId())) }
    }
    return $ids
}

function Ensure-Command([string]$AutomationId) {
    $element = Find-Element $script:window $AutomationId
    if ($null -eq $element -or $element.Current.IsOffscreen -or $element.Current.BoundingRectangle.Height -le 0) {
        $bar = Find-Element $script:window 'InboxCommandBar'
        if (-not $bar -or $bar.Current.IsOffscreen) { throw 'Inbox command bar is not visible.' }
        $more = Find-Element $bar 'MoreButton'
        if (-not $more) { throw "Command '$AutomationId' is unavailable and command bar has no MoreButton." }
        Invoke-Element $more 'open Inbox command overflow'
        $element = Wait-Element $AutomationId 5
    }
    if ($element.Current.IsOffscreen -or $element.Current.BoundingRectangle.Height -le 0) {
        throw "Inbox command '$AutomationId' is not reachable."
    }
    return $element
}

function Assert-CommandUnavailable([string]$AutomationId, [string]$Context) {
    $element = Find-Element $script:window $AutomationId
    if ($null -eq $element) { return }
    $bounds = $element.Current.BoundingRectangle
    if ($element.Current.IsEnabled -and -not $element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) {
        throw "${Context}: command '$AutomationId' is enabled and visible when it should be unavailable."
    }
}

function Find-ListItemWithAvatar([string]$AutomationId, [string]$NamePattern) {
    foreach ($item in (Get-ListItems $AutomationId)) {
        $avatar = Find-Element $item 'InboxConversationPersonPicture'
        if (-not $avatar) { $avatar = Find-RawDescendant $item 'InboxConversationPersonPicture' }
        if ($avatar -and $avatar.Current.Name -match $NamePattern) { return $item }
    }
    return $null
}

function Find-RawDescendant([Windows.Automation.AutomationElement]$Root, [string]$AutomationId) {
    if (-not $Root) { return $null }
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [Collections.Generic.Queue[Windows.Automation.AutomationElement]]::new()
    $child = $walker.GetFirstChild($Root)
    while ($child) {
        $pending.Enqueue($child)
        $child = $walker.GetNextSibling($child)
    }
    $visited = 0
    while ($pending.Count -gt 0 -and $visited -lt 4096) {
        $item = $pending.Dequeue()
        $visited++
        if ($item.Current.AutomationId -eq $AutomationId) { return $item }
        $child = $walker.GetFirstChild($item)
        while ($child) {
            $pending.Enqueue($child)
            $child = $walker.GetNextSibling($child)
        }
    }
    return $null
}

function Set-Value([string]$AutomationId, [string]$Value) {
    $element = Wait-Element $AutomationId
    $candidates = [Collections.Generic.List[Windows.Automation.AutomationElement]]::new()
    $candidates.Add($element)
    $edits = $element.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit))
    foreach ($edit in $edits) { $candidates.Add($edit) }
    foreach ($candidate in $candidates) {
        $pattern = $null
        if ($candidate.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.ValuePattern]$pattern).SetValue($Value)
            return
        }
    }
    throw "UIA element '$AutomationId' and its nested Edit controls have no ValuePattern."
}

function Get-OwnedAppWindows {
    $appHwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $roots = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($root in $roots) {
        $hwnd = [IntPtr]$root.Current.NativeWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { continue }
        $processId = [uint32]0
        [void][InboxGalleryNative]::GetWindowThreadProcessId($hwnd, [ref]$processId)
        $ownerHwnd = [InboxGalleryNative]::GetWindow($hwnd, 4) # GW_OWNER
        if ($processId -eq [uint32]$script:app.Id -or $ownerHwnd -eq $appHwnd) { $root }
    }
}

function Activate-AppWindow {
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Refusing foreground input with an invalid app HWND.' }
    $owner = [uint32]0
    [void][InboxGalleryNative]::GetWindowThreadProcessId($hwnd, [ref]$owner)
    if ($owner -ne [uint32]$script:app.Id) { throw 'Refusing foreground input because the HWND does not belong to the isolated app process.' }
    [void][InboxGalleryNative]::ShowWindow($hwnd, 9)
    [void][InboxGalleryNative]::BringWindowToTop($hwnd)
    try { $script:window.SetFocus() } catch { }
    $foreground = [InboxGalleryNative]::GetForegroundWindow()
    $foregroundOwner = [uint32]0
    $foregroundThread = [InboxGalleryNative]::GetWindowThreadProcessId($foreground, [ref]$foregroundOwner)
    $windowThread = [InboxGalleryNative]::GetWindowThreadProcessId($hwnd, [ref]$owner)
    $currentThread = [InboxGalleryNative]::GetCurrentThreadId()
    $attachedForeground = $false
    $attachedWindow = $false
    if ($foregroundThread -and $foregroundThread -ne $currentThread) {
        $attachedForeground = [InboxGalleryNative]::AttachThreadInput($currentThread, $foregroundThread, $true)
    }
    if ($windowThread -and $windowThread -ne $currentThread) {
        $attachedWindow = [InboxGalleryNative]::AttachThreadInput($currentThread, $windowThread, $true)
    }
    try { [void][InboxGalleryNative]::SetForegroundWindow($hwnd) }
    finally {
        if ($attachedWindow) { [void][InboxGalleryNative]::AttachThreadInput($currentThread, $windowThread, $false) }
        if ($attachedForeground) { [void][InboxGalleryNative]::AttachThreadInput($currentThread, $foregroundThread, $false) }
    }
    Start-Sleep -Milliseconds 80
    $foreground = [InboxGalleryNative]::GetForegroundWindow()
    $foregroundOwner = [uint32]0
    [void][InboxGalleryNative]::GetWindowThreadProcessId($foreground, [ref]$foregroundOwner)
    if ($foregroundOwner -ne [uint32]$script:app.Id) { throw 'Refusing keyboard input because the isolated app did not become the foreground process.' }
}

function Find-VisibleOwnedElement([string]$AutomationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    foreach ($root in (Get-OwnedAppWindows)) {
        foreach ($element in $root.FindAll([Windows.Automation.TreeScope]::Subtree, $condition)) {
            if (-not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Width -gt 0) { return $element }
        }
    }
    return $null
}

function Capture-Window([string]$Name) {
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Refusing to capture an invalid app window.' }
    $owner = [uint32]0
    [void][InboxGalleryNative]::GetWindowThreadProcessId($hwnd, [ref]$owner)
    if ($owner -ne [uint32]$script:app.Id) { throw 'Refusing screenshot because the HWND is not owned by this isolated app process.' }
    $path = Join-Path $script:scenarioRoot $Name
    & winapp ui screenshot -w $hwnd -o $path --json | Out-Null
    if ($LASTEXITCODE) { throw "Could not capture screenshot '$Name'." }
}

function Resize-App([int]$Width, [int]$Height) {
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [InboxGalleryNative]::GetDpiForWindow($hwnd) / 96.0
    if (-not [InboxGalleryNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30,
        [int]($Width * $scale), [int]($Height * $scale), 0x0004 -bor 0x0010)) {
        throw "Could not size the isolated app window to ${Width}x${Height} DIPs."
    }
    Start-Sleep -Milliseconds 300
}

function Stop-AppOnly {
    if ($script:app -and -not $script:app.HasExited) {
        try { $script:app.Kill($true); $script:app.WaitForExit(7000) | Out-Null } catch { }
    }
    $script:app = $null
    $script:window = $null
}

function Start-GalleryApp {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($script:data)
    $start.Environment['CAB_LIVE_STEERING_FIXTURE'] = '1'
    $start.Environment['CAB_LIVE_STEERING_RELEASE_FILE'] = $script:gate
    $start.Environment['CAB_LIVE_STEERING_OBSERVATIONS_FILE'] = $script:observations
    $script:app = [Diagnostics.Process]::Start($start)
    if (-not $script:app) { throw 'Could not start isolated Inbox gallery app.' }
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($candidate in $windows) {
            if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break }
        }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $script:window) { throw 'No accessible isolated application window appeared.' }
    Resize-App 1160 800
    Wait-For { (Results).connections -eq 1 } 'loopback Mattermost connection'
}

function Write-GallerySnapshots([string]$Workspace, [string]$Origin) {
    $now = [DateTimeOffset]::UtcNow
    $rows = @(
        @{ ThreadId='gallery-restored-pending'; State='pending'; Count=2; User='0123456789abcdefghijklmnop'; Display='Gallery Search Owner'; UserName='Gallery Search Owner'; Channel='gallery-restored'; ChannelName='Gallery Restored Room'; Site='Offline Gallery'; Direct=$false },
        @{ ThreadId='gallery-restored-duplicate-first'; State='completed'; Count=1; User='gallery-owner'; Display='Gallery Duplicate Title'; UserName='Gallery Duplicate Owner'; Channel='gallery-duplicate-first'; ChannelName='Gallery Duplicate First Room'; Site='Offline Gallery'; Direct=$false },
        @{ ThreadId='gallery-restored-duplicate'; State='completed'; Count=1; User='gallery-owner'; Display='Gallery Duplicate Title'; UserName='Gallery Duplicate Owner'; Channel='gallery-duplicate-second'; ChannelName='Gallery Duplicate Second Room'; Site='Offline Gallery'; Direct=$false },
        @{ ThreadId='gallery-restored-running'; State='running'; Count=1; User='runner'; Display='Gallery Running Owner'; UserName='Gallery Running Owner'; Channel='gallery-running'; ChannelName='Running Room'; Site='Offline Gallery'; Direct=$false },
        @{ ThreadId='gallery-restored-completed'; State='completed'; Count=3; User='finisher'; Display='Gallery Completed Owner'; UserName='Gallery Completed Owner'; Channel='gallery-completed'; ChannelName='Completed Room'; Site='Offline Gallery'; Direct=$false },
        @{ ThreadId='gallery-restored-generic'; State='idle'; Count=0; User='generic-user'; Display=$null; UserName=$null; Channel='gallery-generic'; ChannelName=$null; Site=$null; Direct=$true }
    )
    $entries = @()
    for ($index = 0; $index -lt $rows.Count; $index++) {
        $row = $rows[$index]
        $entries += @{
            Key = @{ Provider='mattermost'; ChannelId=$row.Channel; ThreadId=$row.ThreadId; UserId=$row.User; Origin=$Origin }
            WorkspaceRoot=$Workspace; UpdatedAt=$now.AddSeconds(-$index); State=$row.State; RequestCount=$row.Count
            DisplayName=$row.Display; UserName=$row.UserName; ChannelName=$row.ChannelName; SiteName=$row.Site; IsDirectMessage=$row.Direct
        }
    }
    for ($index = $entries.Count; $index -lt $RestoredConversationCount; $index++) {
        $entries += @{
            Key = @{ Provider='mattermost'; ChannelId="bulk-channel-$index"; ThreadId="bulk-thread-$index"; UserId="bulk-user-$index"; Origin=$Origin }
            WorkspaceRoot=$Workspace; UpdatedAt=$now.AddSeconds(-$index); State='completed'; RequestCount=1
            DisplayName="Bulk conversation $index"; UserName=$null; ChannelName="Bulk room $index"; SiteName='Offline Gallery'; IsDirectMessage=$false
        }
    }
    Save-Json (Join-Path $script:data 'chat-conversations.json') @{ SchemaVersion=1; Entries=$entries }
}

function Search-Input([string]$Text) {
    Set-Value 'InboxSearch' ''
    if (-not [string]::IsNullOrEmpty($Text)) {
        Activate-AppWindow
        $search = Wait-Element 'InboxSearch'
        $searchEdit = $search.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::Edit))
        if (-not $searchEdit) { $searchEdit = $search }
        $searchEdit.SetFocus()
        [Windows.Forms.SendKeys]::SendWait($Text)
    }
    Start-Sleep -Milliseconds 300
    $search = Wait-Element 'InboxSearch'
    $edit = $search.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit))
    if (-not $edit) { $edit = $search }
    $valuePattern = $null
    if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        Assert ([string]$valuePattern.Current.Value -ceq [string]$Text) "Search input did not retain the full typed query. Expected '$Text'; actual '$($valuePattern.Current.Value)'."
    }
}

function Find-Suggestion([string]$Text, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $inboxRowIds = Get-InboxRowRuntimeIds
    do {
        foreach ($root in (Get-OwnedAppWindows)) {
            $matches = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::ListItem))
            foreach ($candidate in $matches) {
                $runtimeId = [string]::Join(',', $candidate.GetRuntimeId())
                if (-not $inboxRowIds.Contains($runtimeId) -and
                    (Get-ElementText $candidate).Contains($Text, [StringComparison]::Ordinal)) { return $candidate }
            }
        }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Select-LiveRequest([string]$Prompt) {
    $row = Find-ListItemContaining 'InboxConversations' $Prompt 15
    if (-not $row) { throw "No Inbox row contained request marker '$Prompt'." }
    Select-ListItem $row "open Inbox conversation for '$Prompt'"
    Wait-Element 'InboxSelectedPrompt' 10 | Out-Null
}

function Wait-InboxState([string]$Thread, [string]$State, [int]$Seconds = 25) {
    Wait-For {
        $path = Join-Path $script:data 'chat-conversations.json'
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        @($doc.Entries | Where-Object { $_.Key.ThreadId -eq $Thread -and $_.State -eq $State }).Count -gt 0
    } "Inbox metadata state '$State' for '$Thread'" $Seconds
}

function Read-Observations {
    if (-not (Test-Path -LiteralPath $script:observations)) { return @() }
    @((Get-Content -LiteralPath $script:observations | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }))
}

function Run-Scenario([string]$Language, [string]$Theme) {
    $script:scenarioName = "$Language-$Theme"
    $script:scenarioRoot = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
    $script:evidenceRoots.Add($script:scenarioRoot)
    $script:root = $script:scenarioRoot
    $script:data = Join-Path $script:root 'data'
    $script:gate = Join-Path $script:data '.cab-live-steering-release'
    $script:observations = Join-Path $script:data 'protocol-observations.jsonl'
    $script:endpoint = $null
    New-Item -ItemType Directory -Force -Path $script:data | Out-Null

    # Start the existing isolated fixture once to write its known-safe profile, then stop
    # its app before seeding restored metadata. The loopback server and fixture remain owned here.
    $previousAvatarFixture = [Environment]::GetEnvironmentVariable('CAB_GALLERY_AVATAR_FIXTURE', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('CAB_GALLERY_AVATAR_FIXTURE', '1', 'Process')
        Start-IsolatedApp
    } finally {
        [Environment]::SetEnvironmentVariable('CAB_GALLERY_AVATAR_FIXTURE', $previousAvatarFixture, 'Process')
    }
    Stop-AppOnly
    # The offline CLI requires data/workspace; use a separate application profile
    # so its settings-directory exclusion does not hide synthetic workspace outputs.
    $fixtureData = $script:data
    $script:data = Join-Path $script:root 'profile'
    New-Item -ItemType Directory -Force -Path $script:data | Out-Null
    Get-ChildItem -LiteralPath $fixtureData -File -Filter '*.json' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $script:data $_.Name)
    }
    Save-Json (Join-Path $script:data 'language.json') @{ Language=$Language }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme=$Theme }
    $settingsPath = Join-Path $script:data 'settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.ChatApprovalMode = 'always'
    $settings.ChatResultMode = 'auto'
    $settings | Add-Member -NotePropertyName FileAttachmentApprovalMode -NotePropertyValue 'first' -Force
    $settings.AutoConnectProvider = 'mattermost'
    $workspace = Join-Path $fixtureData 'workspace'
    $settings.WorkspaceRoot = $workspace
    $settings.TrustedWorkspaceRoots = $workspace
    Save-Json $settingsPath $settings
    $canonical = "http://127.0.0.1:$(([uri]$script:endpoint).Port)/"
    # endpoint is a URL with the chosen port; hash the same canonical value used by Mattermost sessions.
    $origin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    Write-GallerySnapshots $workspace $origin
    Start-GalleryApp
    Invoke-Element (Wait-Element 'NavChat') 'open isolated Chat Inbox'
    Wait-Element 'InboxConversations' | Out-Null
    Wait-For { (Results).connections -eq 1 } 'connected isolated Inbox'

    $restoredCount = (Wait-Element 'InboxConversationCount').Current.Name
    Assert ($restoredCount -match "(?<!\d)$RestoredConversationCount(?!\d)") "Expected $RestoredConversationCount restored conversations; count label was '$restoredCount'."
    $restoredAvatar = Find-Element $script:window 'InboxConversationPersonPicture'
    if (-not $restoredAvatar) { $restoredAvatar = Find-RawDescendant $script:window 'InboxConversationPersonPicture' }
    if (-not $restoredAvatar) {
        throw 'List PersonPicture was not discoverable by AutomationId in either control or raw UIA views.'
    }
    Assert (-not $restoredAvatar.Current.IsOffscreen -and $restoredAvatar.Current.BoundingRectangle.Width -gt 0) 'List PersonPicture is not visibly rendered.'
    Assert ($restoredAvatar.Current.Name -match 'Gallery Search Owner') "List avatar identity did not match the selected fixture user: '$($restoredAvatar.Current.Name)'."
    Capture-Window "$Language-$Theme-01-gallery-list.png"
    Add-Check "$script:scenarioName restored list and list avatar" 'PASS' $restoredCount
    Wait-For { (Results).profileImageRequests -eq 1 } 'profile image fetched for restored requester'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $script:data 'avatar-cache') -Filter '*.png').Count -eq 1) 'Requester image was not cached.'
    Add-Check "$script:scenarioName profile image cache" 'PASS' 'One bounded image fetch for restored requester.'

    # Verify the generic fallback is present without asserting an OS-generated glyph name.
    Search-Input ''
    $genericRow = Find-ListItemWithAvatar 'InboxConversations' 'Unknown|不明|参加者'
    if (-not $genericRow) { throw 'Generic fallback conversation row was not found by its PersonPicture accessible identity.' }
    Select-ListItem $genericRow 'open generic fallback conversation identified by generic PersonPicture'
    Wait-Element 'InboxRestoredConversationDetails' | Out-Null
    $detailAvatar = Find-Element $script:window 'InboxSelectedPersonPicture'
    if (-not $detailAvatar) { $detailAvatar = Find-RawDescendant $script:window 'InboxSelectedPersonPicture' }
    if (-not $detailAvatar) {
        throw 'Detail PersonPicture was not discoverable by AutomationId in either control or raw UIA views.'
    }
    Assert (-not $detailAvatar.Current.IsOffscreen -and $detailAvatar.Current.BoundingRectangle.Width -gt 0) 'Detail PersonPicture is not visibly rendered.'
    Assert ($detailAvatar.Current.Name -match 'Unknown|不明|参加者|Person|人物|ユーザー|作成者') "Generic detail avatar has an unexpected accessible identity: '$($detailAvatar.Current.Name)'."
    Capture-Window "$Language-$Theme-02-restored-detail.png"
    Add-Check "$script:scenarioName generic fallback detail avatar" 'PASS'
    Search-Input ''

    # Add actual loopback requests. One request stays pending until explicitly approved;
    # the TestAgent is gated so its running and stopped states remain inspectable.
    $pendingThread = 'gallery-live-pending-' + [guid]::NewGuid().ToString('N')
    Send-Request ('gallery-pending-' + [guid]::NewGuid().ToString('N')) $pendingThread 'GALLERY_PENDING_SEARCH_TARGET LIVE_STEER_STEER_1'
    Wait-InboxState $pendingThread 'pending'
    Select-LiveRequest 'GALLERY_PENDING_SEARCH_TARGET'
    Assert ((Wait-Element 'ApproveChat').Current.IsEnabled) 'Approve is disabled for a live pending request.'
    Assert ((Wait-Element 'RejectChat').Current.IsEnabled) 'Reject is disabled for a live pending request.'
    Assert-CommandUnavailable 'CancelChat' 'Pending request'

    if ($RestoredConversationCount -ge 100) {
        Search-Input 'GALLERY_PENDING_SEARCH_TARGET'
        Assert ((Get-ElementText (Wait-Element 'InboxSelectedPrompt')).Contains('GALLERY_PENDING_SEARCH_TARGET')) 'Batch search reset lost the still-visible selected conversation.'
        Assert ((Wait-Element 'ApproveChat').Current.IsEnabled) 'Batch filtering disabled approval for the selected pending request.'
        Search-Input ''
        Assert ((Get-ElementText (Wait-Element 'InboxSelectedPrompt')).Contains('GALLERY_PENDING_SEARCH_TARGET')) 'Clearing the batch search lost selection.'
        Add-Check "$script:scenarioName large-list search keeps selected conversation" 'PASS' "$RestoredConversationCount restored conversations; batch narrow and restore retain selection."
    }

    # Search has local suggestions, empty results, escape/cancel, and direct candidate selection.
    Search-Input 'NO_SUCH_GALLERY_CONVERSATION_'
    Start-Sleep -Milliseconds 200
    Assert ((Get-ListItems 'InboxConversations').Count -eq 0) 'A no-results query left conversation rows visible.'
    Wait-Element 'InboxEmptyList' 5 | Out-Null
    Search-Input 'GALLERY_PENDING_SEARCH_TARGET'
    $suggestion = Find-Suggestion 'GALLERY_PENDING_SEARCH_TARGET'
    if (-not $suggestion) { throw 'AutoSuggestBox did not expose the matching pending conversation candidate through UI Automation.' }
    Activate-AppWindow
    $search = Wait-Element 'InboxSearch'
    $searchEdit = $search.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit))
    if ($searchEdit) { $searchEdit.SetFocus() } else { $search.SetFocus() }
    [Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 180
    Assert ($null -eq (Find-Suggestion 'GALLERY_PENDING_SEARCH_TARGET' 2)) 'Escape did not cancel the AutoSuggestBox candidate popup.'
    Wait-InboxState $pendingThread 'pending' 5
    Assert (@(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 0) 'Typing or cancelling autocomplete accidentally started a TestAgent turn.'
    Search-Input 'GALLERY_PENDING_SEARCH_TARGET'
    $suggestion = Find-Suggestion 'GALLERY_PENDING_SEARCH_TARGET'
    if (-not $suggestion) { throw 'Matching conversation suggestion did not return after the autocomplete was reopened.' }
    Select-ListItem $suggestion 'select pending conversation from search suggestions'
    Wait-Element 'InboxSelectedPrompt' | Out-Null
    $selectedPrompt = Wait-Element 'InboxSelectedPrompt'
    Assert ((Get-ElementText $selectedPrompt).Contains('GALLERY_PENDING_SEARCH_TARGET', [StringComparison]::Ordinal)) 'Search candidate opened the wrong conversation.'
    Assert (@(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 0) 'Typing or selecting a search candidate accidentally started an approved TestAgent turn.'
    Capture-Window "$Language-$Theme-03-search-candidate.png"
    Add-Check "$script:scenarioName search candidate selects matching pending conversation" 'PASS'
    Set-Value 'InboxSearch' ''

    # Opening and cancelling the run confirmation proves approval requires a second explicit action.
    Invoke-Element (Wait-Element 'ApproveChat') 'open live request approval confirmation'
    $primary = Wait-ProcessElement 'PrimaryButton'
    $close = Wait-ProcessElement 'CloseButton'
    Assert ((Get-ElementText $primary) -match 'Run|実行') 'Approval confirmation has no recognizable run action.'
    Invoke-Element $close 'cancel live request approval confirmation'
    Wait-For { @(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 0 } 'confirmation cancellation to leave TestAgent idle' 3
    Wait-Element 'ApproveChat' | Out-Null
    Add-Check "$script:scenarioName approval confirmation cancel leaves request pending" 'PASS'

    # Approve the isolated request; then stop its gated TestAgent turn through the UI.
    Wait-For { (Find-Element $script:window 'ApproveChat').Current.IsEnabled } 'approval command reenabled after confirmation cancellation'
    Invoke-Element (Wait-Element 'ApproveChat') 'open request approval confirmation after cancel'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm execution of isolated TestAgent request'
    Wait-InboxState $pendingThread 'running'
    Wait-For { @(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 1 } 'offline TestAgent turn actually started' 20
    Select-LiveRequest 'GALLERY_PENDING_SEARCH_TARGET'
    Assert ((Wait-Element 'CancelChat').Current.IsEnabled) 'Stop is disabled for the live running request.'
    Assert-CommandUnavailable 'ApproveChat' 'Running request'
    Assert-CommandUnavailable 'RejectChat' 'Running request'
    Capture-Window "$Language-$Theme-04-running.png"
    Invoke-Element (Wait-Element 'CancelChat') 'stop isolated gated TestAgent request'
    Wait-InboxState $pendingThread 'cancelled' 15
    Wait-For { @(Read-Observations | Where-Object method -eq 'turn/interrupt').Count -eq 1 } 'TestAgent interruption after Stop' 12
    Add-Check "$script:scenarioName Stop interrupts the offline TestAgent turn" 'PASS'

    # A separate pending request verifies Reject without creating an execution.
    $rejectThread = 'gallery-live-reject-' + [guid]::NewGuid().ToString('N')
    Send-Request ('gallery-reject-' + [guid]::NewGuid().ToString('N')) $rejectThread 'GALLERY_REJECT_TARGET LIVE_STEER_QUEUE_1'
    Wait-InboxState $rejectThread 'pending'
    Select-LiveRequest 'GALLERY_REJECT_TARGET'
    Invoke-Element (Wait-Element 'RejectChat') 'reject isolated pending request'
    Wait-InboxState $rejectThread 'cancelled' 12
    Select-LiveRequest 'GALLERY_REJECT_TARGET'
    $rejectStateName = (Wait-Element 'InboxSelectedState').Current.Name
    if ($Language -eq 'ja') {
        Assert ($rejectStateName -match '拒否') "Rejected request UI state was not localized as rejected: '$rejectStateName'."
    } else {
        Assert ($rejectStateName -match 'Rejected') "Rejected request UI state was not localized as rejected: '$rejectStateName'."
    }
    Assert (@(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 1) 'Reject unexpectedly started another TestAgent turn.'
    Add-Check "$script:scenarioName Reject leaves TestAgent idle" 'PASS'

    # Complete a separate gated run and inspect result/attachment command routing on its detail.
    $completeThread = 'gallery-live-completed-' + [guid]::NewGuid().ToString('N')
    Send-Request ('gallery-complete-' + [guid]::NewGuid().ToString('N')) $completeThread 'GALLERY_COMPLETED_SEARCH_TARGET LIVE_STEER_QUEUE_1'
    Wait-InboxState $completeThread 'pending'
    Select-LiveRequest 'GALLERY_COMPLETED_SEARCH_TARGET'
    Invoke-Element (Wait-Element 'ApproveChat') 'approve completed-state fixture request'
    Invoke-Element (Wait-ProcessElement 'PrimaryButton') 'confirm completed-state fixture request'
    Wait-InboxState $completeThread 'running'
    Wait-For { @(Read-Observations | Where-Object method -eq 'turn/start').Count -eq 2 } 'completed-state TestAgent turn actually started' 20
    Start-Sleep -Milliseconds 800
    Set-Content -LiteralPath (Join-Path $workspace 'gallery-output.txt') -Value 'Offline gallery attachment evidence.' -Encoding utf8
    Set-Content -LiteralPath $script:gate -Value 'release gallery completed-state TestAgent turn' -Encoding utf8
    Wait-InboxState $completeThread 'completed' 30
    Select-LiveRequest 'GALLERY_COMPLETED_SEARCH_TARGET'
    Assert-CommandUnavailable 'ApproveChat' 'Completed request'
    Assert-CommandUnavailable 'RejectChat' 'Completed request'
    Assert-CommandUnavailable 'CancelChat' 'Completed request'
    Wait-For {
        $attachmentStatus = Find-Element $script:window 'SelectedAttachmentStatus'
        $null -ne $attachmentStatus -and -not [string]::IsNullOrWhiteSpace($attachmentStatus.Current.Name)
    } 'synthetic output attachment candidate discovery' 15
    $resultButton = Ensure-Command 'SendChatResult'
    $attachmentButton = Ensure-Command 'SendChatAttachments'
    Assert ($resultButton.Current.IsEnabled) 'Result sharing route is disabled for a completed request with a result.'
    Wait-For { (Ensure-Command 'SendChatAttachments').Current.IsEnabled } 'attachment candidates discovered and sharing enabled' 15
    $attachmentButton = Ensure-Command 'SendChatAttachments'
    Assert (-not $resultButton.Current.IsOffscreen -and $resultButton.Current.BoundingRectangle.Width -gt 0) 'Result sharing command is not visible in completed details.'
    Assert (-not $attachmentButton.Current.IsOffscreen -and $attachmentButton.Current.BoundingRectangle.Width -gt 0) 'Attachment command is not visible in completed details.'
    $liveAvatar = Find-Element $script:window 'InboxSelectedPersonPicture'
    if (-not $liveAvatar) { $liveAvatar = Find-RawDescendant $script:window 'InboxSelectedPersonPicture' }
    if (-not $liveAvatar) { throw 'Live detail PersonPicture was not discoverable by AutomationId in either control or raw UIA views.' }
    $detailIdentity = $liveAvatar.Current.Name
    Assert ($detailIdentity -match 'Unknown|不明|参加者|Person|人物|ユーザー|作成者') "Live user without a cached username did not use the generic PersonPicture identity: '$detailIdentity'."
    $profileOverlay = Find-RawDescendant $script:window 'InboxSelectedProfileImage'
    Assert ($null -eq $profileOverlay -or $profileOverlay.Current.IsOffscreen -or $profileOverlay.Current.BoundingRectangle.Width -le 0) 'Previous requester photo remained visible on a different conversation without an image.'
    Capture-Window "$Language-$Theme-05-completed-detail.png"
    Add-Check "$script:scenarioName completed request result and attachment commands accessible" 'PASS'

    # Open each outbound confirmation, then cancel it. The only transport is the
    # loopback fixture, and this smoke must not share results or files by accident.
    $replyCountBefore = @((Results).replies).Count
    Invoke-Element $resultButton 'open isolated result sharing confirmation'
    $replyDialog = Wait-ProcessElement 'ReplyConfirmation'
    Assert ((Get-ElementText $replyDialog).Contains('LIVE_STEER_FINAL', [StringComparison]::Ordinal)) 'Result confirmation did not describe the selected TestAgent output.'
    Invoke-Element (Wait-ProcessElement 'CloseButton') 'cancel result sharing confirmation'
    Start-Sleep -Milliseconds 250
    Assert (@((Results).replies).Count -eq $replyCountBefore) 'Cancelling result sharing posted an outbound reply.'
    Add-Check "$script:scenarioName result route opens a confirmation and cancellation sends nothing" 'PASS'

    $fileDownloadCountBefore = @((Results).fileDownloads).Count
    Wait-For { (Ensure-Command 'SendChatAttachments').Current.IsEnabled } 'attachment sharing command reenabled after result confirmation' 10
    $attachmentButton = Ensure-Command 'SendChatAttachments'
    Invoke-Element $attachmentButton 'open isolated attachment sharing confirmation'
    $attachmentDialog = Wait-ProcessElement 'AttachmentConfirmation'
    $attachmentChoice = Wait-ProcessElement 'AttachmentFile1'
    Assert ((Get-ElementText $attachmentChoice).Contains('gallery-output.txt', [StringComparison]::Ordinal)) 'Attachment confirmation did not identify the synthetic workspace output.'
    Assert ($attachmentChoice.Current.IsEnabled -and $attachmentChoice.Current.ControlType -eq [Windows.Automation.ControlType]::CheckBox) 'Attachment choice is not exposed as an enabled selectable file.'
    Invoke-Element (Wait-ProcessElement 'CloseButton') 'cancel attachment sharing confirmation'
    Start-Sleep -Milliseconds 250
    Assert (@((Results).fileDownloads).Count -eq $fileDownloadCountBefore) 'Cancelling attachment sharing downloaded or routed a file.'
    Add-Check "$script:scenarioName attachment route opens a file consent dialog and cancellation shares nothing" 'PASS'

    # Filter to restored rows and verify a unique cached user name opens the correct metadata row.
    Search-Input 'Gallery Search Owner'
    $restoredSuggestion = Find-Suggestion 'Gallery Restored Room'
    if (-not $restoredSuggestion) { throw 'Restored user search did not produce its matching suggestion.' }
    Select-ListItem $restoredSuggestion 'choose restored conversation suggestion'
    Wait-Element 'InboxRestoredConversationDetails' | Out-Null
    $restoredTitle = Wait-Element 'InboxRestoredTitle'
    Assert ((Get-ElementText $restoredTitle).Contains('Gallery Search Owner', [StringComparison]::Ordinal)) 'Restored search candidate opened a different conversation.'
    Search-Input ''

    # Duplicate display titles are deliberately ordered so the second suggestion
    # names the second room. Confirm selection follows the selected object identity.
    Search-Input 'Gallery Duplicate Title'
    $duplicateCandidates = [Collections.Generic.List[Windows.Automation.AutomationElement]]::new()
    $duplicateRuntimeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inboxRowIds = Get-InboxRowRuntimeIds
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        foreach ($root in (Get-OwnedAppWindows)) {
            $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::ListItem))
            foreach ($candidate in $items) {
                $runtimeId = [string]::Join(',', $candidate.GetRuntimeId())
                if ((Get-ElementText $candidate).Contains('Gallery Duplicate Title', [StringComparison]::Ordinal) -and
                    -not $inboxRowIds.Contains($runtimeId) -and
                    $duplicateRuntimeIds.Add($runtimeId)) { $duplicateCandidates.Add($candidate) }
            }
        }
        if ($duplicateCandidates.Count -ge 2) { break }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($duplicateCandidates.Count -lt 2) { throw "Expected two duplicate-title suggestions, found $($duplicateCandidates.Count)." }
    Select-ListItem $duplicateCandidates[1] 'choose the second duplicate-title suggestion'
    Wait-Element 'InboxRestoredConversationDetails' | Out-Null
    Assert ((Get-ElementText (Wait-Element 'InboxRestoredLocation')).Contains('Gallery Duplicate Second Room', [StringComparison]::Ordinal)) `
        'Choosing the second duplicate-title suggestion opened the wrong conversation.'
    Add-Check "$script:scenarioName duplicate-title suggestion selects the intended conversation" 'PASS'
    Search-Input ''

    # At 500x600, detail navigation must expose Back and return to the searchable list.
    Resize-App 500 600
    Invoke-Element (Wait-Element 'InboxBackToConversations') 'show narrow list before searching'
    Search-Input 'GALLERY_COMPLETED_SEARCH_TARGET'
    $narrowSuggestion = Find-Suggestion 'GALLERY_COMPLETED_SEARCH_TARGET'
    if ($narrowSuggestion) { Select-ListItem $narrowSuggestion 'open completed conversation at narrow width' }
    else {
        $narrowRow = Find-ListItemContaining 'InboxConversations' 'GALLERY_COMPLETED_SEARCH_TARGET' 8
        if (-not $narrowRow) { Search-Input ''; $narrowRow = Find-ListItemContaining 'InboxConversations' 'GALLERY_COMPLETED_SEARCH_TARGET' 8 }
        if (-not $narrowRow) { throw 'Could not locate completed conversation for narrow navigation check.' }
        Select-ListItem $narrowRow 'open completed conversation at narrow width'
    }
    $back = Wait-Element 'InboxBackToConversations'
    Assert (-not $back.Current.IsOffscreen -and $back.Current.IsEnabled) 'Narrow detail does not show an enabled Back action.'
    Capture-Window "$Language-$Theme-06-narrow-detail.png"
    Invoke-Element $back 'return to conversation list at narrow width'
    Assert (-not (Wait-Element 'InboxSearch').Current.IsOffscreen) 'Back did not return to the narrow conversation list.'
    Capture-Window "$Language-$Theme-07-narrow-list.png"
    Add-Check "$script:scenarioName narrow back navigation at 500x600" 'PASS'

    # TeachingTip is opt-in and must open and close without changing any request state.
    Resize-App 1160 800
    Select-LiveRequest 'GALLERY_COMPLETED_SEARCH_TARGET'
    $beforeStartCount = @(Read-Observations | Where-Object method -eq 'turn/start').Count
    $tipTrigger = Wait-Element 'InboxApprovalHelp'
    Assert ($tipTrigger.Current.IsEnabled) 'Approval help trigger is disabled.'
    Invoke-Element $tipTrigger 'open opt-in approval TeachingTip'
    Wait-For {
        $null -ne (Find-VisibleOwnedElement 'InboxApprovalHelpTip')
    } 'approval TeachingTip visible' 8
    $tip = Find-VisibleOwnedElement 'InboxApprovalHelpTip'
    Assert (-not $tip.Current.IsOffscreen -and $tip.Current.BoundingRectangle.Width -gt 0) 'Approval TeachingTip did not become visible when opened.'
    $tipText = Get-ElementText $tip
    if ($Language -eq 'ja') {
        Assert ($tipText.Contains('初回ファイル共有', [StringComparison]::Ordinal)) 'Japanese policy help did not explain first-share attachment approval.'
    } else {
        Assert ($tipText.Contains('Confirm the first file share', [StringComparison]::Ordinal) -and
            -not $tipText.Contains('Ask every time', [StringComparison]::Ordinal)) 'English policy help did not show the configured first-share label.'
    }
    Capture-Window "$Language-$Theme-08-approval-tip-open.png"
    $tipClose = Find-Element $tip 'CloseButton'
    if (-not $tipClose) { throw 'Approval TeachingTip has no close button.' }
    Invoke-Element $tipClose 'close approval TeachingTip'
    Wait-For {
        $null -eq (Find-VisibleOwnedElement 'InboxApprovalHelpTip')
    } 'approval TeachingTip closed' 5
    Assert (@(Read-Observations | Where-Object method -eq 'turn/start').Count -eq $beforeStartCount) 'Opening the TeachingTip started an agent turn.'
    Add-Check "$script:scenarioName approval TeachingTip opens and closes safely" 'PASS'

    Stop-AppOnly
    Stop-IsolatedApp
    $script:fixture = $null
}

try {
    $languages = if ($Language -eq 'both') { @('en','ja') } else { @($Language) }
    $themes = if ($Theme -eq 'both') { @('light','dark') } else { @($Theme) }
    foreach ($language in $languages) {
        foreach ($theme in $themes) { Run-Scenario $language $theme }
    }
} catch {
    Add-Check 'Inbox gallery smoke execution' 'FAIL' $_.Exception.Message
    if ($script:window -and $script:app -and -not $script:app.HasExited) {
        try { Capture-Window 'failure.png' } catch { }
    }
    throw
} finally {
    Stop-AppOnly
    Stop-IsolatedApp
    $summary = [ordered]@{
        createdUtc = [DateTime]::UtcNow.ToString('o')
        fixture = 'isolated unpackaged app plus loopback Mattermost and self-contained offline TestAgent'
        screenshots = @($script:evidenceRoots.ToArray())
        results = @($script:results)
    }
    foreach ($evidenceRoot in $script:evidenceRoots) {
        [IO.File]::WriteAllText((Join-Path $evidenceRoot 'results.json'),
            ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    }
}

$failures = @($script:results | Where-Object status -eq 'FAIL')
if ($failures.Count -gt 0) { exit 1 }
Write-Output "Inbox gallery smoke complete. Evidence root: $($script:scenarioRoot)"
