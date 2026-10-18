param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$dataPath = [IO.Path]::GetFullPath($DataDir)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-wizard-test'))) { throw 'Use a marked isolated wizard test directory.' }
$progress = Get-Content (Join-Path $dataPath 'onboarding.json') -Raw | ConvertFrom-Json
if ($progress.status -ne 'completed' -and -not ($progress.status -eq 'active' -and $progress.step -eq 0)) { throw 'Run WizardUiE2E first.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WizardLayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
$window = (Get-Process -Id $AppPid).MainWindowHandle
$scale = [WizardLayoutNative]::GetDpiForWindow($window) / 96.0
$root = [System.Windows.Automation.AutomationElement]::FromHandle($window)
$output = Join-Path $artifactRoot 'onboarding-wizard-review'
$checks = [Collections.Generic.List[object]]::new()
function UI([string[]]$Arguments) {
    $response = & winapp ui @Arguments -a $AppPid --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI action failed: $($Arguments[0]) $($Arguments[1])" }
    return $response
}
function Element([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Missing element: $Id" }
    return $element
}
function Resize([int]$Width,[int]$Height) {
    if (-not [WizardLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Resize failed.' }
    Start-Sleep -Milliseconds 300
}
function Capture([string]$Name) {
    UI @('screenshot','--focus','-o',(Join-Path $output "screenshots/$Name.png")) | Out-Null
}
function ScrollPattern { return (Element 'WizardScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) }
function Assert-Progress([int]$Step) {
    $actual = (UI @('get-value','OnboardingTitle') | ConvertFrom-Json).text
    if (-not $actual.StartsWith("[$Step / 5] ")) { throw "Unexpected progress: $actual" }
}
try {
    if ($progress.status -eq 'completed') { UI @('invoke','StartOnboarding') | Out-Null }
    Resize 720 600
    for ($step=1; $step -le 5; $step++) {
        Assert-Progress $step
        $pattern = ScrollPattern
        if ($pattern.Current.VerticalScrollPercent -gt 0.1) { throw "Step $step did not start at the top." }
        $before = (Element 'OnboardingPrevious').Current.BoundingRectangle
        $next = (Element 'OnboardingNext').Current.BoundingRectangle
        $bounds = $root.Current.BoundingRectangle
        if ($before.Left -gt $bounds.Left + 50*$scale -or $before.Left -ge $next.Left -or $next.Right -gt $bounds.Right) { throw 'Back/Next are not fixed at the lower left/right.' }
        Capture "layout-$step-narrow-top"
        if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,100); Start-Sleep -Milliseconds 200 }
        $after = (Element 'OnboardingPrevious').Current.BoundingRectangle
        if ([Math]::Abs($before.Top-$after.Top) -gt 1) { throw 'Scrolling moved the footer.' }
        Capture "layout-$step-narrow-bottom"
        $checks.Add(@{name="step $step narrow layout, progress, fixed footer and scroll reset";passed=$true})
        if ($step -eq 1) {
            UI @('scroll-into-view','WizardAcceptTerms') | Out-Null
            UI @('invoke','WizardAcceptTerms') | Out-Null
            UI @('invoke','WizardAcceptPrivacy') | Out-Null
        }
        Resize 1160 800
        $pattern = ScrollPattern
        if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 150 }
        Capture "layout-$step-normal"
        if ($step -lt 5) { UI @('invoke','OnboardingNext') | Out-Null; Resize 720 600 }
    }
    UI @('scroll','WizardScroll','--to','bottom') | Out-Null
    UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-Progress 4
    if ((ScrollPattern).Current.VerticalScrollPercent -gt 0.1) { throw 'Back retained the previous page scroll offset.' }
    UI @('invoke','OnboardingNext') | Out-Null
    UI @('invoke','OnboardingNext') | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $completed = (Get-Content (Join-Path $dataPath 'onboarding.json') -Raw | ConvertFrom-Json).status -eq 'completed'
    } while (-not $completed -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $completed) { throw 'Wizard did not complete after layout review.' }
    $checks.Add(@{name='Back resets scroll and wizard can complete after layout review';passed=$true})
} catch { $checks.Add(@{name='layout review';passed=$false;detail=$_.Exception.Message}) }
finally {
    Resize 1160 800
    $result = @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0)}
    $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'layout-results.json')
}
$result | ConvertTo-Json -Depth 5
if (-not $result.passed) { exit 1 }
