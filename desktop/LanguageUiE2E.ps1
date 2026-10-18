param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$layoutPath = [IO.Path]::GetFullPath($Layout)
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-language-test'))) { throw 'Use a marked isolated language test directory.' }
if (Test-Path (Join-Path $dataPath 'settings.json')) { throw 'Start with no saved bridge settings.' }
$output = Join-Path $artifacts 'language-review'
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataPath 'work') -Force | Out-Null
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
function Restart { Stop-Process -Id $script:appPid; Launch }
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Capture([string]$File) { UI @('screenshot','--focus','-o',(Join-Path $output $File)) | Out-Null }
function Consent-Checked([string]$Id) {
    UI @('scroll-into-view',$Id) | Out-Null
    $window = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if (-not $control) { throw "Consent control missing: $Id" }
    return $control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On
}
function Resize([int]$Width,[int]$Height) {
    $window = (Get-Process -Id $script:appPid).MainWindowHandle
    $scale = [LanguageLayoutNative]::GetDpiForWindow($window) / 96.0
    if (-not [LanguageLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Resize failed.' }
    Start-Sleep -Milliseconds 250
}
function Select-Language([string[]]$Labels,[string]$Stored,[bool]$RestartNow = $false) {
    $window=[Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    $next=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'OnboardingNext'))
    $expectsDialog=-not ($next -and -not $next.Current.IsOffscreen -and $next.Current.BoundingRectangle.Height -gt 0)
    UI @('scroll-into-view','DisplayLanguage') | Out-Null
    UI @('invoke','DisplayLanguage') | Out-Null
    Start-Sleep -Milliseconds 250
    $tree = UI @('inspect','DisplayLanguage','-d','6') | ConvertFrom-Json
    function Find-Choice($nodes) {
        foreach ($node in $nodes) {
            if ($node.type -eq 'ListItem' -and $Labels -contains $node.name) { $node.selector }
            if ($node.children) { Find-Choice $node.children }
        }
    }
    $selector = @($tree.windows | ForEach-Object { Find-Choice $_.elements }) | Select-Object -First 1
    if (-not $selector) { throw 'Expected language option is missing.' }
    # Verify persistence after the ComboBox popup has closed.
    & winapp ui invoke $selector -a $script:appPid --json 2>$null | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    $handledDialog = $false
    do {
        $window = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
        $dialog = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'LanguageRestartDialog'))
        if ($dialog -and -not $dialog.Current.IsOffscreen -and -not $handledDialog) {
            $handledDialog = $true
            Assert ($dialog.Current.Name.Contains('Restart')) 'Restart dialog has no English recovery wording.'
            Capture 'language-restart-dialog.png'
            if ($RestartNow) {
                $oldPid = $script:appPid
                UI @('invoke','PrimaryButton') | Out-Null
                do {
                    Start-Sleep -Milliseconds 200
                    $restarted = Get-Process ChatAgentBridge.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldPid -and $_.Path.StartsWith($layoutPath,[StringComparison]::OrdinalIgnoreCase) }
                } while (-not $restarted -and [DateTimeOffset]::UtcNow -lt $deadline)
                Assert ([bool]$restarted) 'Native restart did not relaunch the isolated app.'
                $script:appPid = [int]($restarted | Select-Object -First 1).Id
                UI @('wait-for','NavOverview','-t','10000') | Out-Null
                $checks.Add(@{name='Restart now relaunches app with isolated settings directory';passed=$true})
                return
            }
            UI @('invoke','SecondaryButton') | Out-Null
            $checks.Add(@{name='Language change prompts restart and Later keeps app open';passed=$true})
        }
        if (Test-Path (Join-Path $dataPath 'language.json')) {
            $preference = Get-Content (Join-Path $dataPath 'language.json') -Raw | ConvertFrom-Json
            if ($preference.Language -eq $Stored) {
                do {
                    $ready=UI @('get-property','DisplayLanguage','-p','IsEnabled') | ConvertFrom-Json
                    if ($ready.properties.IsEnabled -eq $true -and (-not $expectsDialog -or $handledDialog)) { Start-Sleep -Milliseconds 150; return }
                    if (-not $handledDialog) { break }
                    Start-Sleep -Milliseconds 100
                } while ([DateTimeOffset]::UtcNow -lt $deadline)
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The language selection was not persisted.'
}
try {
    Launch
    UI @('wait-for','DisplayLanguage','-t','5000') | Out-Null
    $systemLanguages = Get-WinUserLanguageList
    $expectsJapanese = $systemLanguages[0].LanguageTag -match '^ja(?:-|$)'
    Assert ((Value 'OnboardingTitle').Contains($(if ($expectsJapanese) { 'はじめに' } else { 'Introduction' }))) 'Initial language did not match the Windows preferred language.'
    $checks.Add(@{name='System language resolves before the initial wizard loads';passed=$true;display=(Value 'OnboardingTitle')})
    UI @('scroll-into-view','WizardAcceptTerms') | Out-Null
    UI @('invoke','WizardAcceptTerms') | Out-Null
    Select-Language @('English') 'en'
    Assert (-not (Test-Path (Join-Path $dataPath 'settings.json'))) 'Language selection created bridge settings.'
    Wait-Text 'OnboardingTitle' 'Introduction and terms'
    Assert ((Name 'WizardTerms') -match '(?i)terms') 'Terms did not switch to English without restarting.'
    Assert ((Name 'OnboardingNext') -match '(?i)agree') 'The consent action did not switch to English.'
    $window=[Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    $hidden=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'DisplayLanguageStatus'))
    Assert (-not $hidden -or $hidden.Current.IsOffscreen -or $hidden.Current.BoundingRectangle.Height -eq 0) 'Initial consent still shows redundant language guidance.'
    UI @('scroll-into-view','WizardTerms') | Out-Null
    UI @('invoke','WizardTerms') | Out-Null
    UI @('wait-for','PolicyBody','-t','3000') | Out-Null
    Assert ((Value 'PolicyBody') -notmatch '[ぁ-んァ-ン一-龯]') 'Terms were not translated before restarting.'
    UI @('invoke','PolicyClose') | Out-Null
    UI @('wait-for','PolicyBody','--gone','-t','3000') | Out-Null
    Assert (Consent-Checked 'WizardAcceptTerms') 'Language switching cleared a checked consent.'
    Assert (-not (Consent-Checked 'WizardAcceptPrivacy')) 'Language switching accepted an unchecked consent.'
    UI @('scroll-into-view','WizardPrivacy') | Out-Null
    UI @('invoke','WizardPrivacy') | Out-Null
    UI @('wait-for','PolicyBody','-t','3000') | Out-Null
    Assert ((Value 'PolicyBody').Contains('Privacy Policy')) 'Privacy policy was not translated immediately.'
    UI @('invoke','PolicyClose') | Out-Null
    UI @('wait-for','PolicyBody','--gone','-t','3000') | Out-Null
    Select-Language @('日本語 (Japanese)') 'ja'
    Wait-Text 'OnboardingTitle' 'はじめに'
    Assert ((Name 'WizardTerms') -eq '利用規約を読む') 'Returning to Japanese requires a restart.'
    Assert (Consent-Checked 'WizardAcceptTerms') 'Returning to Japanese cleared a checked consent.'
    Assert (-not (Consent-Checked 'WizardAcceptPrivacy')) 'Returning to Japanese accepted privacy implicitly.'
    Assert (-not (Test-Path (Join-Path $dataPath 'consent.json'))) 'Switching language persisted consent without continuing.'
    Select-Language @('English') 'en'
    Wait-Text 'OnboardingTitle' 'Introduction and terms'
    $checks.Add(@{name='First consent, title, action and policy switch immediately without restart';passed=$true})
    Restart
    Wait-Text 'OnboardingTitle' '[1/5]'
    Assert ((Value 'OnboardingTitle') -notmatch '[ぁ-んァ-ン一-龯]') 'Dynamic wizard heading is not English.'
    Assert ((Name 'WizardTerms') -match '(?i)terms') 'Static XAML terms button is not English.'
    Capture 'english-intro.png'
    Resize 720 600
    Capture 'english-intro-narrow.png'
    UI @('scroll','WizardScroll','--to','bottom') | Out-Null
    Capture 'english-intro-narrow-bottom.png'
    Resize 1160 860
    UI @('scroll-into-view','WizardTerms') | Out-Null
    UI @('invoke','WizardTerms') | Out-Null
    UI @('wait-for','PolicyBody','-t','3000') | Out-Null
    $body = Value 'PolicyBody'
    Assert ($body -notmatch '[ぁ-んァ-ン一-龯]' -and $body.Contains('mmiyaji') -and $body.Contains('mail@ruhenheim.org')) 'English policy text or contact details are incorrect.'
    Capture 'english-terms.png'
    UI @('invoke','PolicyClose') | Out-Null
    UI @('wait-for','PolicyBody','--gone','-t','3000') | Out-Null
    $checks.Add(@{name='Manual English survives restart and localizes XAML, dynamic text, and terms';passed=$true})

    UI @('scroll-into-view','WizardAcceptTerms') | Out-Null
    UI @('invoke','WizardAcceptTerms') | Out-Null
    UI @('invoke','WizardAcceptPrivacy') | Out-Null
    UI @('invoke','OnboardingNext') | Out-Null
    Wait-Text 'OnboardingTitle' '[2/5]'
    UI @('set-value','WizardWorkspaceRoot',(Join-Path $dataPath 'work')) | Out-Null
    UI @('invoke','OnboardingNext') | Out-Null
    Wait-Text 'OnboardingTitle' '[3/5]'
    UI @('invoke','OnboardingNext') | Out-Null
    UI @('wait-for','WizardStatus','-t','3000') | Out-Null
    Assert ((Value 'OnboardingTitle').StartsWith('[3/5]')) 'Wizard advanced without checking the CLI.'
    $status = UI @('inspect','WizardStatus','-d','4') | ConvertFrom-Json
    $messages = @($status.windows.elements.children | Where-Object { $_.automationId -in @('Title','Message') } | ForEach-Object name)
    Assert ($messages.Count -gt 0 -and ($messages -join ' ') -notmatch '[ぁ-んァ-ン一-龯]') 'Validation text was not translated into English.'
    UI @('invoke','OnboardingDefer') | Out-Null
    UI @('wait-for','OnboardingTitle','--gone','-t','3000') | Out-Null
    foreach ($page in @('NavOverview','NavWorkbench','NavAgentSettings','NavConnections','NavChat','NavHistory','NavAppearance')) {
        UI @('invoke',$page) | Out-Null
        Assert ((Name $page) -notmatch '[ぁ-んァ-ン一-龯]') "Navigation label is not English: $page"
        if ($page -eq 'NavWorkbench') { Assert ((Value 'WorkbenchAgentSummary').Contains('Run until complete')) 'The execution mode did not resolve to an English resource.' }
        if ($page -eq 'NavAppearance') { Assert ((Name 'ThemeLight') -eq 'Light' -and (Name 'ThemeDark') -eq 'Dark') 'Appearance options did not follow the initial wizard language.' }
        Capture "english-$page.png"
        Resize 720 600
        Capture "english-$page-narrow.png"
        Resize 1160 860
    }
    $checks.Add(@{name='English validation and all navigation pages render without starting a CLI or chat connection';passed=$true})

    UI @('invoke','NavAppearance') | Out-Null
    $settingsBefore = (Get-FileHash (Join-Path $dataPath 'settings.json') -Algorithm SHA256).Hash
    Select-Language @('日本語 (Japanese)') 'ja' $true
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json') -Algorithm SHA256).Hash -eq $settingsBefore) 'Language selection changed bridge settings.'
    UI @('wait-for','NavOverview','-t','5000') | Out-Null
    Assert ((Name 'NavOverview') -eq '概要') 'Japanese navigation was not restored.'
    UI @('invoke','NavOverview') | Out-Null
    $window=[Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $script:appPid).MainWindowHandle)
    $details=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'OverviewDetailsExpander'))
    $details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    UI @('scroll-into-view','StartOnboarding') | Out-Null
    UI @('invoke','StartOnboarding') | Out-Null
    Wait-Text 'OnboardingTitle' 'はじめに'
    Assert ((Name 'WizardTerms') -eq '利用規約を読む') 'Japanese XAML resources were not loaded.'
    Capture 'japanese-intro.png'
    $checks.Add(@{name='Manual Japanese survives restart and preserves bridge settings';passed=$true})
    Select-Language @('システムに合わせる (System default)') 'system'
    Restart
    UI @('wait-for','OnboardingTitle','-t','5000') | Out-Null
    Assert ((Value 'OnboardingTitle').Contains($(if ($expectsJapanese) { 'はじめに' } else { 'Introduction' }))) 'System language was not restored.'
    Capture 'system-intro.png'
    $checks.Add(@{name='Returning to system default survives restart';passed=$true;display=(Value 'OnboardingTitle')})
} catch { $checks.Add(@{name='language UI';passed=$false;detail=$_.Exception.Message}) }
finally {
    @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);appPid=$script:appPid} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'ui-results.json')
}
Get-Content (Join-Path $output 'ui-results.json') -Raw
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
