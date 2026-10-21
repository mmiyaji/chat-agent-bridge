param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$layoutPath = [IO.Path]::GetFullPath($Layout)
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-language-test'))) { throw 'Use a marked isolated language test directory.' }
if (Test-Path (Join-Path $dataPath 'settings.json')) { throw 'Start with no saved bridge settings.' }
$output = Join-Path $artifacts 'overview-status-review'
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataPath 'work') -Force | Out-Null
@{WorkspaceRoot=(Join-Path $dataPath 'work');AgentProvider='custom';AgentExecutable=(Join-Path $dataPath 'missing-test-cli.exe');AgentArguments='';AutoConnectProvider='none';RequireApproval=$true} | ConvertTo-Json | Set-Content (Join-Path $dataPath 'settings.json')
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
function Check-RestartGuard {
    UI @('invoke','NavOverview') | Out-Null
    Scroll-ToElement 'DisplayLanguage'
    UI @('invoke','DisplayLanguage') | Out-Null
    $tree=UI @('inspect','DisplayLanguage','-d','6') | ConvertFrom-Json
    function Find-Japanese($nodes) {
        foreach ($node in $nodes) {
            if ($node.type -eq 'ListItem' -and $node.name -ceq '日本語 (Japanese)') { $node.selector }
            if ($node.children) { Find-Japanese $node.children }
        }
    }
    $selector=@($tree.windows | ForEach-Object { Find-Japanese $_.elements }) | Select-Object -First 1
    Assert ([bool]$selector) 'Japanese choice has no English label.'
    & winapp ui invoke $selector -a $script:appPid --json 2>$null | Out-Null
    UI @('wait-for','LanguageRestartDialog','-t','5000') | Out-Null
    Assert ((Name 'LanguageRestartDialog').Contains('Restart')) 'Restart dialog has no English recovery wording.'
    Assert ((UI @('get-property','PrimaryButton','-p','IsEnabled') | ConvertFrom-Json).properties.IsEnabled -eq $false) 'Unsaved settings did not disable Restart now.'
    Capture 'language-restart-dialog.png'
    UI @('invoke','CloseButton') | Out-Null
    UI @('wait-for','LanguageRestartDialog','--gone','-t','3000') | Out-Null
    Assert ((Get-Content (Join-Path $dataPath 'language.json') -Raw | ConvertFrom-Json).Language -eq 'ja') 'Later lost the saved language preference.'
    $checks.Add(@{name='Language change prompts restart and Later keeps app open';passed=$true})
}
function Assert-Palette([string]$Theme,[string[]]$Colors,[string]$File) {
    Capture $File
    $bitmap=[Drawing.Bitmap]::new((Join-Path $output $File))
    try {
        # Font strokes are antialiased; allow surface blending around the palette color.
        foreach ($hex in $Colors) {
            $color=[Drawing.ColorTranslator]::FromHtml($hex); $count=0
            for ($y=150;$y -lt [Math]::Min(500,$bitmap.Height);$y++) {
                for ($x=200;$x -lt $bitmap.Width;$x++) {
                    $pixel=$bitmap.GetPixel($x,$y)
                    if ([Math]::Abs([int]$pixel.R-$color.R) -le 35 -and [Math]::Abs([int]$pixel.G-$color.G) -le 35 -and [Math]::Abs([int]$pixel.B-$color.B) -le 35) { $count++ }
                }
            }
            Assert ($count -ge 5) "$Theme rendered icon color $hex missing ($count pixels)."
        }
    } finally { $bitmap.Dispose() }
    $checks.Add(@{name="$Theme status icon colors render";passed=$true;colors=$Colors})
}
function Theme([string]$Theme) {
    UI @('invoke','NavOverview') | Out-Null
    Scroll-ToElement $(if ($Theme -eq 'light') { 'ThemeLight' } else { 'ThemeDark' })
    UI @('invoke',$(if ($Theme -eq 'light') { 'ThemeLight' } else { 'ThemeDark' })) | Out-Null
    Start-Sleep -Milliseconds 500
    UI @('invoke','NavOverview') | Out-Null
}
try {
    Launch
    Resize 1160 860
    UI @('invoke','NavOverview') | Out-Null
    Theme 'light'
    Assert-Palette 'Light before CLI check' @('#5E7B38','#8A741F') 'light-unchecked.png'
    UI @('invoke','NavAgentSettings') | Out-Null
    Scroll-ToElement 'SettingsCheckCli'
    UI @('invoke','SettingsCheckCli') | Out-Null
    Start-Sleep -Milliseconds 800
    UI @('invoke','NavOverview') | Out-Null
    Assert ((Name 'OverviewCliTile').Contains('Needs review')) 'Failed CLI check is not exposed as an error.'
    Assert-Palette 'Light' @('#5E7B38','#8A741F','#B45B5B') 'light-errors.png'
    Theme 'dark'
    Assert-Palette 'Dark' @('#A8C583','#D1BD79','#D99B9B') 'dark-errors.png'
    Theme 'light'
    Assert-Palette 'Light after live switch' @('#5E7B38','#8A741F','#B45B5B') 'light-restored.png'
    Resize 720 600
    Capture 'light-narrow.png'
    Theme 'dark'
    Capture 'dark-narrow.png'
    $checks.Add(@{name='Light and dark render at 720x600';passed=$true})
    $settingsHash=(Get-FileHash (Join-Path $dataPath 'settings.json')).Hash
    UI @('invoke','NavAgentSettings') | Out-Null
    Scroll-ToElement 'AgentExecutable'
    UI @('set-value','AgentExecutable',(Join-Path $dataPath 'unsaved-cli.exe')) | Out-Null
    Check-RestartGuard
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Later saved unrelated draft settings.'
    Assert ((Name 'NavAgentSettings') -eq 'Agent settings') 'Later changed display language before restart.'
    $checks.Add(@{name='Unsaved draft prevents restart; Later preserves process, display and bridge settings';passed=$true})
} catch { $checks.Add(@{name='Overview status UI';passed=$false;detail=$_.Exception.Message}) }
finally {
    @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);appPid=$script:appPid} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'ui-results.json')
}
Get-Content (Join-Path $output 'ui-results.json') -Raw
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
