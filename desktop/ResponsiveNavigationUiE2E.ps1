param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [ValidateSet('ja','en')][string]$Language = 'ja'
)
$ErrorActionPreference = 'Stop'
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-monkey-test'))) { throw 'Use a marked isolated monkey test directory.' }
if ((Get-Process -Id $AppPid).ProcessName -ne 'ChatAgentBridge.Desktop') { throw 'Unexpected target process.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ResponsiveNavigationNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
'@
$output = Join-Path $artifacts ('responsive-navigation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $output | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$window = (Get-Process -Id $AppPid).MainWindowHandle
$scale = [ResponsiveNavigationNative]::GetDpiForWindow($window) / 96.0
function UI([string[]]$Arguments) {
    $result = & winapp ui @Arguments -a $AppPid --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI operation failed: $($Arguments -join ' ')" }
    return $result
}
function Root { [System.Windows.Automation.AutomationElement]::FromHandle($window) }
function Element([string]$Id) {
    (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Navigate([string]$Id) {
    $nav = Element $Id
    if ($null -eq $nav -or $nav.Current.IsOffscreen) {
        UI @('invoke','TogglePaneButton') | Out-Null
        UI @('wait-for',$Id,'-t','3000') | Out-Null
    }
    UI @('invoke',$Id) | Out-Null
    Start-Sleep -Milliseconds 200
}
function Heading([string]$Name) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
    $found = (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $contentTop = (Root).Current.BoundingRectangle.Top + 32*$scale
    $visible = @($found | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.Top -ge $contentTop } |
        Sort-Object { $_.Current.BoundingRectangle.Left } -Descending | Select-Object -First 1)
    if ($visible.Count -ne 1) { throw "Expected one visible page heading '$Name', found $($visible.Count)." }
    return $visible[0].Current.BoundingRectangle
}
$labels = if ($Language -eq 'ja') {
    @('Chat Agent Bridge','手動で実行','エージェント設定','接続設定','チャット受信','履歴と会話','情報')
} else {
    @('Chat Agent Bridge','Run manually','Agent settings','Connection settings','Chat inbox','History and conversations','About')
}
$pages = @('NavOverview','NavWorkbench','NavAgentSettings','NavConnections','NavChat','NavHistory','NavAbout')
$overviewOffsets = @{}
try {
    Navigate 'NavOverview'
    UI @('wait-for','SettingsLocation','--value',$dataPath,'-t','5000') | Out-Null
    # Minimal -> Compact -> Expanded -> Minimal also checks that the offset is cleared.
    foreach ($width in @(500,720,1160,500,720)) {
        if (-not [ResponsiveNavigationNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($width*$scale),[int](600*$scale),0x0010)) { throw 'Resize failed.' }
        Start-Sleep -Milliseconds 250
        for ($i=0; $i -lt $pages.Count; $i++) {
            Navigate $pages[$i]
            $heading = Heading $labels[$i]
            $toggle = (Element 'TogglePaneButton').Current.BoundingRectangle
            if ($heading.IntersectsWith($toggle)) { throw "Menu button overlaps $($pages[$i]) at width $width." }
            if ($i -eq 0) { $overviewOffsets[$width] = ($heading.Top - (Root).Current.BoundingRectangle.Top) / $scale }
            $file = "$width-$($pages[$i]).png"
            UI @('screenshot','--focus','-w',"$window",'-o',(Join-Path $output $file)) | Out-Null
            $checks.Add(@{name='Heading clear of navigation toggle';width=$width;page=$pages[$i];passed=$true;screenshot=$file})
        }
    }
    if ([Math]::Abs($overviewOffsets[720] - $overviewOffsets[1160]) -gt 2) { throw 'Minimal-mode space remained after widening the window.' }
    $checks.Add(@{name='Compact and expanded modes keep the original top offset';passed=$true})
} catch {
    $checks.Add(@{name='Responsive navigation';passed=$false;detail=$_.Exception.Message})
} finally {
    @{passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);checks=@($checks)} |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'results.json')
    Write-Output "Responsive results: $output"
}
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
