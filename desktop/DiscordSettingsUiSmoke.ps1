# Verifies Discord settings editing, sensitive-change cancellation, and persistence
# against an isolated profile. It deliberately never starts a Discord connection.
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('en','ja')][string]$Language = 'en',
    [ValidateSet('light','dark')][string]$Theme = 'light'
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
if (-not [IO.Path]::IsPathRooted($AppDirectory) -or
    -not $appRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) {
    throw 'Use an unpackaged app publish under desktop/artifacts.'
}
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Desktop executable was not found: $exe" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiscordSettingsSmokeNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

$script:root = Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data = Join-Path $script:root 'data'
$script:app = $null
$script:window = $null
$script:results = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $script:data | Out-Null

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Add-Pass([string]$Message) { $script:results.Add($Message); Write-Output "PASS $Message" }
function Save-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Find-Element([Windows.Automation.AutomationElement]$Scope, [string]$Id) {
    if (-not $Scope) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Element([string]$Id, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $script:window $Id
        if ($element) { return $element }
        if ($script:app -and $script:app.HasExited) { throw "App exited while waiting for '$Id' (exit $($script:app.ExitCode))." }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for UIA element '$Id'."
}
function Wait-ProcessElement([string]$Id, [int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($popup in $windows) { $element = Find-Element $popup $Id; if ($element) { return $element } }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for popup UIA element '$Id'."
}
function Invoke-Element([Windows.Automation.AutomationElement]$Element, [string]$Description) {
    Assert ($null -ne $Element -and $Element.Current.IsEnabled) "UI element is missing or disabled: $Description"
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.InvokePattern]$pattern).Invoke(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { ([Windows.Automation.SelectionItemPattern]$pattern).Select(); return }
    throw "No invoke or selection pattern for $Description."
}
function Value([string]$Id, [string]$Text) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw "$Id has no ValuePattern." }
    ([Windows.Automation.ValuePattern]$pattern).SetValue($Text)
}
function Checked([string]$Id) {
    $element = Wait-Element $Id
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { throw "$Id has no TogglePattern." }
    ([Windows.Automation.TogglePattern]$pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On
}
function Set-Checked([string]$Id, [bool]$Wanted) {
    if ((Checked $Id) -ne $Wanted) {
        $element = Wait-Element $Id
        $pattern = $null
        if (-not $element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { throw "$Id has no TogglePattern." }
        ([Windows.Automation.TogglePattern]$pattern).Toggle()
    }
}
function Resize-Test([int]$Width, [int]$Height = 850) {
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [DiscordSettingsSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    if (-not [DiscordSettingsSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 25, 25, [int]($Width * $scale), [int]($Height * $scale), 0x0010)) {
        throw "Could not resize the app to $Width x $Height DIPs."
    }
    Start-Sleep -Milliseconds 350
}
function Capture([string]$Name) {
    $path = Join-Path $script:root ($Name + '.png')
    & winapp ui screenshot -w $script:window.Current.NativeWindowHandle -o $path --json | Out-Null
    if ($LASTEXITCODE) { throw "Could not capture $Name." }
    Write-Output "SCREENSHOT $path"
}
function Log-Bounds([string]$Id) {
    $element = Find-Element $script:window $Id
    if ($element) {
        $r = $element.Current.BoundingRectangle
        Write-Output "BOUNDS $Id=($($r.Left),$($r.Top),$($r.Width),$($r.Height)) offscreen=$($element.Current.IsOffscreen)"
    } else { Write-Output "BOUNDS $Id=<missing>" }
}
function Choose-CompactScope([string]$Id) {
    $target = Find-Element $script:window $Id
    if (-not $target -or $target.Current.IsOffscreen) {
        $overflow = Find-Element (Wait-Element 'ConnectionScopeList') 'TopNavOverflowButton'
        Assert ($null -ne $overflow -and -not $overflow.Current.IsOffscreen) 'Narrow Discord scope overflow is unavailable.'
        Invoke-Element $overflow 'open narrow Discord settings scope menu'
        $target = Wait-ProcessElement $Id
    }
    Invoke-Element $target "select narrow scope $Id"
    Start-Sleep -Milliseconds 250
    $selected = (Wait-Element $Id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    Assert ($selected.Current.IsSelected) "Narrow scope $Id was not selected."
}
function Wait-ForNarrowScopeLayout([int]$Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $common = Find-Element $script:window 'ConnectionScopeCommon'
        $discord = Find-Element $script:window 'ConnectionScopeDiscord'
        if ($common -and $discord) {
            $commonRect = $common.Current.BoundingRectangle
            $discordRect = $discord.Current.BoundingRectangle
            if ([Math]::Abs($commonRect.Top - $discordRect.Top) -lt 10 -and $discordRect.Left -gt $commonRect.Left + 30) { return }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Connection scopes did not settle into the narrow top-tab layout.'
}
function Start-IsolatedApp {
    $workspace = Join-Path $script:data 'workspace'
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null
    Add-Type -AssemblyName System.Security
    $protectedToken = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('isolated-discord-settings-token'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $settings = @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentProvider = 'codex'; AgentMode = 'app-server'
        AgentExecutable = 'codex'; AgentArguments = 'app-server'; AgentPermissionMode = 'inherit'; RequireApproval = $true
        MattermostServerUrl = ''; MattermostChannelId = ''; MattermostToken = ''; MattermostAllowedUserIds = ''; MattermostAllowedThreadIds = ''
        SlackBotToken = ''; SlackAppToken = ''; SlackAllowedUserIds = ''; SlackAllowedChannelIds = ''; SlackAllowedThreadIds = ''
        DiscordBotToken = $protectedToken; DiscordAllowedUserIds = '100000000000000001'; DiscordAllowedGuildIds = '200000000000000002'
        DiscordAllowedChannelIds = ''; DiscordAllowedThreadIds = ''; DiscordAllowDirectMessages = $false
        AutoConnectProvider = 'none'; PostConnectionNotice = $false; ShareApprovalsInChat = $false; ShareAgentQuestionsInChat = $false
        ChatApprovalMode = 'auto'; ChatResultMode = 'auto'; ChatFollowUpMode = 'queue'; TrustedWorkspaceRoots = $workspace
        AllowAgentNetwork = $false; FileAttachmentApprovalMode = 'always'; FileAttachmentMaxSizeMb = 10
        FileAttachmentMaxCount = 10; FileAttachmentMaxTotalSizeMb = 25
    }
    Save-Json (Join-Path $script:data 'settings.json') $settings
    Save-Json (Join-Path $script:data 'onboarding.json') @{ SchemaVersion = 3; Step = 4; Status = 'completed' }
    $policyPath = Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs'
    $policy = [regex]::Match((Get-Content -LiteralPath $policyPath -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read onboarding consent policy version.'
    Save-Json (Join-Path $script:data 'consent.json') @{ PolicyVersion = $policy; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Save-Json (Join-Path $script:data 'language.json') @{ Language = $Language }
    Save-Json (Join-Path $script:data 'appearance.json') @{ Theme = $Theme }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Arguments = '--data-dir "' + $script:data + '"'
    $script:app = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw "App exited during startup (exit $($script:app.ExitCode))." }
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $windows) {
            if ($candidate.Current.Name -notmatch 'PopupHost') { $script:window = $candidate; break }
        }
        if ($script:window) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($null -ne $script:window) 'No accessible application window appeared.'
    $hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    $scale = [DiscordSettingsSmokeNative]::GetDpiForWindow($hwnd) / 96.0
    [void][DiscordSettingsSmokeNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 25, 25, [int](1160 * $scale), [int](850 * $scale), 0x0010)
    Start-Sleep -Milliseconds 500
}
function Stop-IsolatedApp {
    if ($script:app -and -not $script:app.HasExited) { try { $script:app.Kill(); $script:app.WaitForExit(7000) | Out-Null } catch { } }
}

try {
    Start-IsolatedApp
    Invoke-Element (Wait-Element 'NavConnections') 'open connection settings'
    $list = Wait-Element 'ConnectionScopeList'
    Assert ($null -ne (Find-Element $list 'ConnectionScopeDiscord')) 'Discord scope is missing from settings navigation.'
    Invoke-Element (Wait-Element 'ConnectionScopeDiscord') 'open Discord settings'
    Assert (-not (Checked 'DiscordAllowDirectMessages')) 'Direct messages must default off.'
    $commonRect = (Wait-Element 'ConnectionScopeCommon').Current.BoundingRectangle
    $discordRect = (Wait-Element 'ConnectionScopeDiscord').Current.BoundingRectangle
    Write-Output "LAYOUT common=($($commonRect.Left),$($commonRect.Top),$($commonRect.Width),$($commonRect.Height)) discord=($($discordRect.Left),$($discordRect.Top),$($discordRect.Width),$($discordRect.Height))"
    Assert ([Math]::Abs($commonRect.Left - $discordRect.Left) -lt 16 -and $discordRect.Top -gt $commonRect.Top) 'Wide connection settings should use a side list.'
    Capture "discord-wide-$Language-$Theme"
    Resize-Test 500 820
    Wait-ForNarrowScopeLayout
    foreach ($id in @('MainNavigation','MainContentGrid','MainContentScroll','FormContent','ConnectionScopeList','ConnectionScopeCommon','ConnectionScopeDiscord','DiscordSettingsCard')) { Log-Bounds $id }
    Choose-CompactScope 'ConnectionScopeCommon'
    Choose-CompactScope 'ConnectionScopeDiscord'
    Assert (-not (Wait-Element 'ConnectionScopeList').Current.IsOffscreen) 'Narrow connection scope picker is missing.'
    Capture "discord-narrow-$Language-$Theme"
    Resize-Test 1160 850
    Choose-CompactScope 'ConnectionScopeDiscord'
    $settingsPath = Join-Path $script:data 'settings.json'
    $initial = Get-FileHash -LiteralPath $settingsPath

    Value 'DiscordAllowedUserIds' '100000000000000001, 100000000000000003'
    Value 'DiscordAllowedGuildIds' '200000000000000004'
    Set-Checked 'DiscordAllowDirectMessages' $true
    (Wait-Element 'ConnectionScopeDiscord').SetFocus()
    $confirmation = Wait-ProcessElement 'SensitiveSettingsConfirmation'
    Invoke-Element (Find-Element $confirmation 'CloseButton') 'cancel sensitive Discord settings change'
    Start-Sleep -Milliseconds 500
    Assert ((Get-FileHash -LiteralPath $settingsPath).Hash -eq $initial.Hash) 'Cancelling changed the saved settings file.'
    Assert (-not (Checked 'DiscordAllowDirectMessages')) 'Cancelling did not restore the DM toggle.'
    Assert (((Wait-Element 'DiscordAllowedUserIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq '100000000000000001') 'Cancelling did not restore allowed users.'
    Assert (((Wait-Element 'DiscordAllowedGuildIds').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq '200000000000000002') 'Cancelling did not restore allowed guilds.'
    Add-Pass 'Cancel rolls back Discord DM and guild/user allowlist edits without changing persisted settings'

    Value 'DiscordAllowedUserIds' '100000000000000001, 100000000000000003'
    Value 'DiscordAllowedGuildIds' '200000000000000004'
    Set-Checked 'DiscordAllowDirectMessages' $true
    (Wait-Element 'ConnectionScopeDiscord').SetFocus()
    $confirmation = Wait-ProcessElement 'SensitiveSettingsConfirmation'
    Invoke-Element (Find-Element $confirmation 'PrimaryButton') 'confirm Discord settings change'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($saved.DiscordAllowDirectMessages -eq $true -and $saved.DiscordAllowedGuildIds -eq '200000000000000004') { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($saved.DiscordAllowDirectMessages -eq $true) 'Confirmed DM setting was not persisted.'
    Assert ($saved.DiscordAllowedUserIds -eq '100000000000000001,100000000000000003') 'Confirmed user IDs were not normalized and persisted.'
    Assert ($saved.DiscordAllowedGuildIds -eq '200000000000000004') 'Confirmed guild ID was not persisted.'
    Assert ($saved.DiscordBotToken -notmatch 'isolated-discord-settings-token') 'Bot token was written in plaintext.'
    Assert ($saved.AutoConnectProvider -eq 'none') 'The settings smoke test changed the provider connection setting.'
    Add-Pass 'Confirm persists normalized Discord IDs and DM permission while keeping the token encrypted and auto-connect disabled'
    Write-Output (ConvertTo-Json -InputObject @($script:results) -Compress)
}
catch {
    Write-Error $_
    throw
}
finally { Stop-IsolatedApp }
