param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDir)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not (Test-Path (Join-Path $dataPath '.cab-cli-tools-test')) -or -not $dataPath.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Use isolated data under desktop/artifacts.' }
$settingsFile=Join-Path $dataPath 'settings.json'
$savedHash=(Get-FileHash $settingsFile).Hash
$output=Join-Path $dataPath 'cli-tools-review'
New-Item $output -ItemType Directory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$script:appPid = 0
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CliToolsLayoutNative {
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
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Capture([string]$File) { UI @('screenshot','--focus','-o',(Join-Path $output $File)) | Out-Null }
function Resize([int]$Width,[int]$Height) {
    $window = (Get-Process -Id $script:appPid).MainWindowHandle
    $scale = [CliToolsLayoutNative]::GetDpiForWindow($window) / 96.0
    if (-not [CliToolsLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Resize failed.' }
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

$script:appPid=$AppProcessId
function Pick([string]$Button,[bool]$Cancel) {
    UI @('invoke',$Button) | Out-Null
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(8)
    do {
        $picker=winapp ui list-windows -a $script:appPid --json | ConvertFrom-Json | Where-Object className -eq '#32770' | Select-Object -First 1
        if ($picker) { break }; Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    Assert ([bool]$picker) 'Native executable picker did not open.'
    $tree=winapp ui inspect -w $picker.hwnd --interactive --json | ConvertFrom-Json
    $elements=$tree.windows.elements
    if ($Cancel) {
        $target=$elements | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq '2' } | Select-Object -First 1
    } else {
        $name=winapp ui search '1148' -w $picker.hwnd --json | ConvertFrom-Json
        $edit=$name.matches | Where-Object type -eq 'Edit' | Select-Object -First 1
        Assert ([bool]$edit) 'Native filename field is unavailable.'
        winapp ui set-value $edit.selector $Executable -w $picker.hwnd --json | Out-Null
        if ($LASTEXITCODE) { throw 'Picker filename input failed.' }
        $target=$elements | Where-Object { $_.type -eq 'SplitButton' -and $_.automationId -eq '1' } | Select-Object -First 1
    }
    Assert ([bool]$target) 'Native picker action is unavailable.'
    winapp ui invoke $target.selector -w $picker.hwnd --json | Out-Null
    if ($LASTEXITCODE) { throw 'Picker action failed.' }
    Start-Sleep -Milliseconds 300
}
function Pass([string]$Text) { $checks.Add(@{Name=$Text;Status='PASS'}) }
try {
    Go 'NavAgentSettings'
    $exp=Element 'AgentAdvanced'
    $pattern=$exp.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $pattern.Expand() }
    $before=Value 'AgentExecutable'
    Scroll-ToElement 'BrowseAgentExecutable'
    Pick 'BrowseAgentExecutable' $true
    Assert ((Value 'AgentExecutable') -eq $before) 'Cancel changed the executable draft.'
    Pick 'BrowseAgentExecutable' $false
    Wait-Text 'AgentExecutable' $Executable
    Assert ((Get-FileHash $settingsFile).Hash -eq $savedHash) 'Picker implicitly saved bridge settings.'
    Pass 'Settings picker selects a native executable; cancel preserves draft; no implicit save'
    Scroll-ToElement 'SettingsCheckCli'
    UI @('invoke','SettingsCheckCli') | Out-Null
    Wait-Text 'SettingsCliCheckSummary' 'モデル候補を確認しました'
    Scroll-ToElement 'ReloadProcessEnvironment'
    UI @('invoke','ReloadProcessEnvironment') | Out-Null
    Wait-Text 'SettingsCliCheckSummary' '再確認'
    Assert ((Get-FileHash $settingsFile).Hash -eq $savedHash) 'Environment refresh saved bridge settings.'
    Pass 'Environment refresh invalidates the successful CLI probe without saving settings'
    foreach ($theme in @('light','dark')) {
        Theme $theme
        Resize 500 720
        Go 'NavAgentSettings'
        Scroll-ToElement 'BrowseAgentExecutable'
        Assert (Visible (Element 'BrowseAgentExecutable')) 'Browse button is inaccessible at minimum width.'
        Capture "settings-picker-$theme.png"
        Scroll-ToElement 'ReloadProcessEnvironment'
        Assert (Visible (Element 'ReloadProcessEnvironment')) 'Reload button is inaccessible at minimum width.'
        Capture "settings-reload-$theme.png"
        Resize 1160 800
    }
    Pass 'Settings controls remain accessible at 500 DIP in Light and Dark'
    Go 'NavOverview'; Scroll-ToElement 'StartOnboarding'; UI @('invoke','StartOnboarding') | Out-Null
    if ((Value 'OnboardingTitle') -match '\[1/5\]') {
        UI @('invoke','WizardAcceptTerms') | Out-Null
        UI @('invoke','WizardAcceptPrivacy') | Out-Null
        UI @('invoke','OnboardingNext') | Out-Null
    }
    Wait-Text 'OnboardingTitle' '[2/5]'
    Scroll-ToElement 'WizardBrowseAgentExecutable'
    $before=Value 'WizardAgentExecutable'
    Pick 'WizardBrowseAgentExecutable' $true
    Assert ((Value 'WizardAgentExecutable') -eq $before) 'Wizard picker cancel changed draft.'
    Pick 'WizardBrowseAgentExecutable' $false
    Wait-Text 'WizardAgentExecutable' $Executable
    Assert ((Get-FileHash $settingsFile).Hash -eq $savedHash) 'Wizard picker implicitly saved settings.'
    Capture 'wizard-picker.png'
    UI @('invoke','OnboardingNext') | Out-Null
    Wait-Text 'OnboardingTitle' '[3/5]'
    $wizardHash=(Get-FileHash $settingsFile).Hash
    Scroll-ToElement 'WizardReloadProcessEnvironment'
    UI @('invoke','WizardReloadProcessEnvironment') | Out-Null
    Assert ((Get-FileHash $settingsFile).Hash -eq $wizardHash) 'Wizard environment reload modified settings.'
    Capture 'wizard-reload.png'
    Pass 'Wizard picker selects/cancels correctly; environment reload works on CLI verification page'
    UI @('invoke','OnboardingDefer') | Out-Null
} catch { $checks.Add(@{Name='CLI tools UI';Status='FAIL';Detail="$($_.Exception.Message)"}); throw }
finally { $checks | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'results.json'); $checks | Format-Table -AutoSize }