param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$layoutPath = [IO.Path]::GetFullPath($Layout)
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-appearance-test'))) { throw 'Use a marked isolated appearance test directory.' }
if (Test-Path (Join-Path $dataPath 'settings.json')) { throw 'Use a new isolated test directory.' }
$output = Join-Path $artifacts 'appearance-review'
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataPath 'work') -Force | Out-Null
@{SchemaVersion=1; WorkspaceRoot=(Join-Path $dataPath 'work'); AgentExecutable='codex'; AgentProvider='codex';
  AutoConnectProvider='none'; RequireApproval=$true; ChatApprovalMode='always'; ChatResultMode='manual'} |
    ConvertTo-Json | Set-Content (Join-Path $dataPath 'settings.json')
@{Language='en'} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'language.json')
$settingsHash = (Get-FileHash (Join-Path $dataPath 'settings.json')).Hash
$checks = [Collections.Generic.List[object]]::new()
$script:appPid = 0
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AppearanceLayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
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
function Launch {
    $started = & winapp run $layoutPath --manifest (Join-Path $layoutPath 'AppxManifest.xml') --output-appx-directory (Join-Path $layoutPath 'AppX') --detach --json -- --data-dir $dataPath | ConvertFrom-Json
    if (-not $started.ProcessId) { throw 'Launch did not return a process.' }
    $script:appPid = [int]$started.ProcessId
    UI @('wait-for','NavOverview','-t','10000') | Out-Null
    Start-Sleep -Milliseconds 300
}
function Restart { Stop-Process -Id $script:appPid; Launch }
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Capture([string]$File) {
    $path = Join-Path $output $File
    UI @('screenshot','--focus','-o',$path) | Out-Null
    return $path
}
function Brightness([string]$Path,[double]$X=0.03,[double]$Y=0.45) {
    $bitmap = [Drawing.Bitmap]::new($Path)
    try { $color = $bitmap.GetPixel([int]($bitmap.Width*$X),[int]($bitmap.Height*$Y)); return ($color.R+$color.G+$color.B)/3.0 }
    finally { $bitmap.Dispose() }
}
function Assert-Theme([string]$Name,[string]$Path) {
    $brightness = Brightness $Path
    Assert ($(if ($Name -eq 'light') { $brightness -gt 180 } else { $brightness -lt 100 })) "Rendered surface did not use $Name theme (brightness $brightness)."
}
function Resize([int]$Width,[int]$Height) {
    $window = (Get-Process -Id $script:appPid).MainWindowHandle
    $scale = [AppearanceLayoutNative]::GetDpiForWindow($window) / 96.0
    if (-not [AppearanceLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Resize failed.' }
    Start-Sleep -Milliseconds 250
}
function Select-Theme([string]$Theme) {
    UI @('invoke','NavAppearance') | Out-Null
    $id = @{system='ThemeSystem';light='ThemeLight';dark='ThemeDark'}[$Theme]
    UI @('scroll-into-view',$id) | Out-Null
    # Verify the persisted theme after UIA selection.
    & winapp ui invoke $id -a $script:appPid --json 2>$null | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        if (Test-Path (Join-Path $dataPath 'appearance.json')) {
            $stored = Get-Content (Join-Path $dataPath 'appearance.json') -Raw | ConvertFrom-Json
            if ($stored.Theme -eq $Theme) { Start-Sleep -Milliseconds 350; return }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The theme was not persisted.'
}
try {
    Launch
    Assert (-not (Test-Path (Join-Path $dataPath 'appearance.json'))) 'Startup rewrote the default theme preference.'
    $systemMode = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize').AppsUseLightTheme
    $expectedSystem = if ($systemMode -eq 0) { 'dark' } else { 'light' }
    Assert-Theme $expectedSystem (Capture 'system-initial.png')
    $checks.Add(@{name='Initial system theme';passed=$true;theme=$expectedSystem})
    foreach ($theme in @('light','dark')) {
        Select-Theme $theme
        Assert-Theme $theme (Capture "$theme-settings.png")
        Resize 720 600
        Capture "$theme-settings-narrow.png" | Out-Null
        Resize 1160 860
        foreach ($page in @('NavOverview','NavWorkbench','NavAgentSettings','NavConnections','NavChat','NavHistory','NavAppearance')) {
            UI @('invoke',$page) | Out-Null
            Assert-Theme $theme (Capture "$theme-$page.png")
        }
        UI @('invoke','NavOverview') | Out-Null
        UI @('scroll-into-view','OverviewTerms') | Out-Null
        UI @('invoke','OverviewTerms') | Out-Null
        UI @('wait-for','PolicyBody','-t','3000') | Out-Null
        $dialogPath = Capture "$theme-dialog.png"
        $dialogBrightness = Brightness $dialogPath 0.5 0.18
        Assert ($(if ($theme -eq 'light') { $dialogBrightness -gt 180 } else { $dialogBrightness -lt 100 })) "Dialog did not use $theme theme."
        UI @('invoke','PolicyClose') | Out-Null
        UI @('wait-for','PolicyBody','--gone','-t','3000') | Out-Null
        $checks.Add(@{name="$theme switches immediately across six pages and dialogs";passed=$true})
        Restart
        Assert-Theme $theme (Capture "$theme-after-restart.png")
        $checks.Add(@{name="$theme survives restart";passed=$true})
    }
    Select-Theme 'system'
    Assert-Theme $expectedSystem (Capture 'system-restored.png')
    Restart
    Assert-Theme $expectedSystem (Capture 'system-after-restart.png')
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Appearance changes modified connection or execution settings.'
    $checks.Add(@{name='Return to system persists and preserves bridge settings';passed=$true})
    Stop-Process -Id $script:appPid
    @{Language='ja'} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'language.json')
    Launch
    UI @('invoke','NavAppearance') | Out-Null
    foreach ($entry in @{ThemeSystem='システムに合わせる';ThemeLight='ライト';ThemeDark='ダーク'}.GetEnumerator()) {
        $label = (UI @('get-property',$entry.Key,'-p','Name') | ConvertFrom-Json).properties.Name
        Assert ($label -eq $entry.Value) 'The appearance option was not localized into Japanese.'
    }
    UI @('scroll-into-view','AppTheme') | Out-Null
    Capture 'japanese-system.png' | Out-Null
    Select-Theme 'light'
    Resize 720 600
    Assert-Theme 'light' (Capture 'japanese-light-narrow.png')
    Select-Theme 'system'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Japanese theme selection modified bridge settings.'
    $checks.Add(@{name='Japanese labels and immediate switching in a narrow window';passed=$true})
} catch { $checks.Add(@{name='appearance UI';passed=$false;detail=$_.Exception.Message}) }
finally {
    @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);appPid=$script:appPid} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'ui-results.json')
}
Get-Content (Join-Path $output 'ui-results.json') -Raw
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
