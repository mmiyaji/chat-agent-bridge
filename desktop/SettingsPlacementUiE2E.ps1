param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$layoutPath = [IO.Path]::GetFullPath($Layout)
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-language-test'))) { throw 'Use a marked isolated language test directory.' }
if (Test-Path (Join-Path $dataPath 'settings.json')) { throw 'Start with no saved bridge settings.' }
$output = Join-Path $artifacts 'settings-placement-review'
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataPath 'work') -Force | Out-Null
@{WorkspaceRoot=(Join-Path $dataPath 'work');AgentProvider='claude';AgentExecutable='claude';AgentArguments='--print --output-format stream-json --verbose';AutoConnectProvider='none';RequireApproval=$true} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'settings.json')
@{Language='en'} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'language.json')
Add-Type -AssemblyName System.Drawing
$checks = [Collections.Generic.List[object]]::new()
$script:appPid = 0
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LanguageLayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
function Scroll-ToElement([string]$Id) {
    # Scroll the viewport through UIA, without Home/End keystrokes that can
    # change a focused ComboBox selection.
    $window=[Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    function Find([string]$TargetId) {
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$TargetId)
        return $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    $initialTarget=Find $Id
    if ($initialTarget -and $initialTarget.Current.IsKeyboardFocusable) { $initialTarget.SetFocus() }
    $next=Find 'OnboardingNext'
    $scroll=Find $(if ($next -and -not $next.Current.IsOffscreen -and $next.Current.BoundingRectangle.Height -gt 0) { 'WizardScroll' } else { 'MainContentScroll' })
    if (-not $scroll) { throw "No test viewport for $Id" }
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 80 }
    for ($attempt=0;$attempt -lt 35;$attempt++) {
        $target=Find $Id
        if ($target) {
            $rect=$target.Current.BoundingRectangle; $viewport=$scroll.Current.BoundingRectangle
            # WinUI can report content-sized UIA bounds after changing pages.
            $viewportBottom=[Math]::Min($viewport.Bottom,$window.Current.BoundingRectangle.Bottom-8)
            if ($rect.Height -gt 0 -and -not $target.Current.IsOffscreen -and $rect.Top -ge $viewport.Top -and $rect.Bottom -le $viewportBottom) { return }
        }
        if (-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100) { break }
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 80
    }
    throw "Could not scroll test viewport to $Id"
}
function UI([string[]]$Arguments) {
    if ($Arguments[0] -eq 'scroll-into-view') { Scroll-ToElement $Arguments[1]; return '' }
    $result = & winapp ui @Arguments -a $script:appPid --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI action failed: $($Arguments[0]) $($Arguments[1])" }
    return $result
}
function Value([string]$Id) { return [string](UI @('get-value',$Id) | ConvertFrom-Json).text }
function Name([string]$Id) { return [string](UI @('get-property',$Id,'-p','Name') | ConvertFrom-Json).properties.Name }
function Wait-Text([string]$Id,[string]$Text) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do { if ((Value $Id).Contains($Text)) { return }; Start-Sleep -Milliseconds 100 } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Expected text on ${Id}: $Text"
}
function Launch {
    $started = & winapp run $layoutPath --manifest (Join-Path $layoutPath 'AppxManifest.xml') --output-appx-directory (Join-Path $layoutPath 'AppX') --detach --json -- --data-dir $dataPath | ConvertFrom-Json
    if (-not $started.ProcessId) { throw 'Launch did not return a process.' }
    $script:appPid = [int]$started.ProcessId
    UI @('wait-for','AppTitleBar','-t','10000') | Out-Null
    Start-Sleep -Milliseconds 300
}
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Capture([string]$File) { UI @('screenshot','--focus','-o',(Join-Path $output $File)) | Out-Null }
function Resize([int]$Width,[int]$Height) {
    $window = (Get-Process -Id $script:appPid).MainWindowHandle
    $scale = [LanguageLayoutNative]::GetDpiForWindow($window) / 96.0
    if (-not [LanguageLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Resize failed.' }
    Start-Sleep -Milliseconds 250
}
function Element([string]$Id) {
    $window=[Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Visible($Control) { return $Control -and -not $Control.Current.IsOffscreen -and $Control.Current.BoundingRectangle.Height -gt 0 }
function Go([string]$Id) {
    if (-not (Visible (Element $Id))) { UI @('invoke','TogglePaneButton') | Out-Null; Start-Sleep -Milliseconds 100 }
    UI @('invoke',$Id) | Out-Null
    Start-Sleep -Milliseconds 180
}
function Theme([string]$Name) {
    Go 'NavAppearance'
    Scroll-ToElement $(if ($Name -eq 'light') { 'ThemeLight' } else { 'ThemeDark' })
    # Verify the persisted theme after UIA selection.
    & winapp ui invoke $(if ($Name -eq 'light') { 'ThemeLight' } else { 'ThemeDark' }) -a $script:appPid --json 2>$null | Out-Null
    $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        if (Test-Path (Join-Path $dataPath 'appearance.json')) {
            if ((Get-Content (Join-Path $dataPath 'appearance.json') -Raw | ConvertFrom-Json).Theme -eq $Name) { Start-Sleep -Milliseconds 350; return }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Theme $Name did not persist."
}
try {
    Launch
    Resize 1160 860
    $settingsFile=Join-Path $dataPath 'settings.json'
    $savedHash=(Get-FileHash $settingsFile).Hash
    Go 'NavAgentSettings'
    Assert (Visible (Element 'AgentProvider')) 'Agent provider disappeared.'
    foreach ($id in @('DisplayLanguage','AppTheme','ChatApprovalMode','ChatScopeCard','AutoConnectProvider')) {
        Assert (-not (Visible (Element $id))) "Misplaced setting remains in Agent settings: $id"
    }
    Scroll-ToElement 'AgentModel'
    Capture 'en-agent-models.png'
    $draftWorkspace=Join-Path $dataPath 'work-draft'
    New-Item -ItemType Directory -Path $draftWorkspace | Out-Null
    Scroll-ToElement 'WorkspaceRoot'
    UI @('set-value','WorkspaceRoot',$draftWorkspace) | Out-Null
    Go 'NavAppearance'
    Assert (Visible (Element 'DisplayLanguage')) 'Language selector is not on Appearance.'
    Scroll-ToElement 'AppTheme'
    Assert (Visible (Element 'AppTheme')) 'Theme selector is not on Appearance.'
    Assert (-not (Visible (Element 'SaveSettings')) -and -not (Visible (Element 'SaveConnections'))) 'Appearance incorrectly requires a bridge settings save.'
    $checks.Add(@{name='Language and theme share Appearance; Agent settings keep only execution controls';passed=$true})
    foreach ($theme in @('light','dark')) {
        Resize 1160 860
        Theme $theme
        Go 'NavAppearance'
        Capture "en-appearance-$theme.png"
        foreach ($width in @(720,500)) {
            Resize $width 600
            Go 'NavAgentSettings'; Go 'NavAppearance'
            Scroll-ToElement 'DisplayLanguage'
            Capture "en-appearance-$theme-$width.png"
            Scroll-ToElement 'ThemeDark'
            Assert (Visible (Element 'ThemeDark')) "Theme controls are inaccessible at $width."
        }
        $checks.Add(@{name="$theme Appearance works at 1160, 720 and 500 DIP";passed=$true})
    }
    Assert ((Get-FileHash $settingsFile).Hash -eq $savedHash) 'Changing appearance saved unrelated execution or connection drafts.'
    Resize 1160 860
    Go 'NavAgentSettings'
    Assert ((Value 'WorkspaceRoot') -eq $draftWorkspace) 'Appearance navigation lost the unsaved workspace.'
    $checks.Add(@{name='Appearance autosave preserves unrelated drafts and saved bridge configuration';passed=$true})
    Go 'NavConnections'
    Scroll-ToElement 'ChatApprovalMode'
    Assert (Visible (Element 'ChatApprovalMode')) 'Chat approval is not accessible in Connection settings.'
    Capture 'en-connection-approval.png'
    Scroll-ToElement 'SlackAllowedThreadIds'
    UI @('set-value','SlackAllowedThreadIds','1234567890.000001') | Out-Null
    Scroll-ToElement 'AutoConnectProvider'
    Assert (Visible (Element 'StartWithWindows')) 'Windows startup is separated from auto connection.'
    Assert (Visible (Element 'SaveConnections')) 'Connection save scrolls out of view.'
    Capture 'en-connection-startup.png'
    UI @('invoke','SaveConnections') | Out-Null
    $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        $saved=Get-Content $settingsFile -Raw | ConvertFrom-Json
        if ($saved.SlackAllowedThreadIds -eq '1234567890.000001') { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $until)
    Assert ($saved.SlackAllowedThreadIds -eq '1234567890.000001' -and $saved.WorkspaceRoot -eq $draftWorkspace) 'Moved controls did not persist together.'
    Assert ($saved.AutoConnectProvider -eq 'none' -and $saved.ChatApprovalMode -ne 'auto') 'Navigation changed connection or approval behavior.'
    $checks.Add(@{name='Chat policies, scope and startup are reachable in Connections; fixed save persists drafts';passed=$true})
    Stop-Process -Id $script:appPid
    @{Language='ja'} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'language.json')
    Launch
    Resize 1160 860
    Go 'NavAppearance'
    Assert ((Name 'NavAppearance') -eq '外観') 'Appearance navigation is not Japanese.'
    Assert ((Value 'AppearancePageTitle') -eq '外観') 'Appearance title is not Japanese.'
    Capture 'ja-appearance.png'
    Resize 500 600
    Go 'NavAgentSettings'; Go 'NavAppearance'
    Scroll-ToElement 'ThemeDark'
    Capture 'ja-appearance-500.png'
    $checks.Add(@{name='Japanese Appearance labels and narrow layout';passed=$true})
    Stop-Process -Id $script:appPid
    '{invalid-test-settings' | Set-Content $settingsFile
    $invalidHash=(Get-FileHash $settingsFile).Hash
    Launch
    Go 'NavAppearance'
    Assert ((Element 'DisplayLanguage').Current.IsEnabled) 'Bridge settings failure disables language recovery.'
    Theme 'light'
    Assert ((Get-FileHash $settingsFile).Hash -eq $invalidHash) 'Appearance overwrote invalid bridge settings.'
    $checks.Add(@{name='Appearance remains usable independently of unreadable bridge settings';passed=$true})
} catch { $checks.Add(@{name='Settings placement UI';passed=$false;detail=$_.Exception.Message}) }
finally {
    @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);appPid=$script:appPid} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'ui-results.json')
}
Get-Content (Join-Path $output 'ui-results.json') -Raw
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
