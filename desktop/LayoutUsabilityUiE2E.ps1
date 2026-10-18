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
$process=Get-Process -Id $AppPid
if ($process.ProcessName -ne 'ChatAgentBridge.Desktop' -or -not $process.Path.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use the isolated staged test app.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LayoutUsabilityNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
'@
$output = Join-Path $artifacts ('layout-usability-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $output | Out-Null
$checks = [Collections.Generic.List[object]]::new()
& winapp ui wait-for AppTitleBar -a $AppPid -t 10000 --json 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Application window did not become ready.' }
$window = (Get-Process -Id $AppPid).MainWindowHandle
$scale = [LayoutUsabilityNative]::GetDpiForWindow($window) / 96.0
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
function Capture([string]$File) { UI @('screenshot','--focus','-o',(Join-Path $output $File)) | Out-Null }
function Resize([int]$Width) {
    if (-not [LayoutUsabilityNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int](600*$scale),0x0010)) { throw 'Resize failed.' }
    Start-Sleep -Milliseconds 250
}
function Visible-InWindow($Target) {
    if (-not $Target -or $Target.Current.IsOffscreen) { return $false }
    $rect=$Target.Current.BoundingRectangle; $outer=(Root).Current.BoundingRectangle
    return $rect.Width -gt 0 -and $rect.Height -gt 0 -and $rect.Left -ge $outer.Left -and $rect.Right -le $outer.Right -and $rect.Top -ge $outer.Top+32*$scale -and $rect.Bottom -le $outer.Bottom-8
}
function Scroll-To([string]$Id) {
    $scroll=Element 'MainContentScroll'
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0) }
    for($attempt=0;$attempt -lt 35;$attempt++) {
        if (Visible-InWindow (Element $Id)) { return }
        if (-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100) { break }
        $pattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,[Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 80
    }
    throw "Could not reveal $Id."
}
function Jump([string]$Option,[string]$Heading) {
    UI @('invoke','ConnectionsJump') | Out-Null
    Start-Sleep -Milliseconds 80
    $item=Element $Option
    if (-not $item) { throw "Jump choice missing: $Option" }
    try { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch [System.Management.Automation.MethodInvocationException] {
        # A command choice resets itself after activation; verify the destination.
    }
    $until=[DateTimeOffset]::UtcNow.AddSeconds(4)
    do {
        if (Visible-InWindow (Element $Heading)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Jump did not reveal $Heading."
}
try {
    Navigate 'NavOverview'
    $expanded=(Element 'OverviewDetailsExpander').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expanded.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { throw 'Overview details did not start expanded.' }
    $checks.Add(@{name='Overview details start expanded';passed=$true})
    foreach ($width in @(500,720,1160)) {
        Resize $width
        Navigate 'NavHistory'
        $tabs=(Root).FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))
        $headers=@($tabs | Where-Object { $_.Current.Name -match '^(Conversations|Executions|Audit|会話|実行|操作記録) \(\d+\)$' })
        if ($headers.Count -ne 3) { throw "Expected three history headers, found $($headers.Count)." }
        foreach ($header in $headers) { if (-not (Visible-InWindow $header)) { throw "History header clipped at ${width}: $($header.Current.Name)" } }
        foreach ($tab in @('SavedConversationsTab','ExecutionHistoryTab','AuditHistoryTab')) { UI @('invoke',$tab) | Out-Null }
        Capture "$Language-$width-history.png"
        $checks.Add(@{name='All three history sections fit and can be selected';width=$width;passed=$true})
        Navigate 'NavAgentSettings'
        foreach ($provider in @('ProviderOptionGemini','ProviderOptionCopilot','ProviderOptionCustom')) {
            Scroll-To 'AgentProvider'
            UI @('invoke','AgentProvider') | Out-Null
            UI @('invoke',$provider) | Out-Null
            Scroll-To 'RequireApproval'
            if (-not (Element 'RequireApproval').Current.IsEnabled) { throw 'Manual execution approval is disabled.' }
            $checks.Add(@{name='Manual execution approval is available for basic CLI';width=$width;provider=$provider;passed=$true})
        }
        Capture "$Language-$width-manual-approval.png"
        Navigate 'NavConnections'
        foreach ($pair in @(@('JumpMattermost','MattermostSettingsHeading'),@('JumpSlack','SlackSettingsHeading'),@('JumpApproval','chatAgentBridgeDesktopMainPageTextBlock031'),@('JumpScope','chatAgentBridgeDesktopMainPageTextBlock034'),@('JumpStartup','StartupHeading'))) {
            Jump $pair[0] $pair[1]
            if (-not (Visible-InWindow (Element 'SaveConnections')) -or -not (Visible-InWindow (Element 'ConnectionsJump'))) { throw 'Pinned settings actions are clipped.' }
            Capture "$Language-$width-$($pair[0]).png"
            $checks.Add(@{name='Jump reveals setting and keeps save available';width=$width;choice=$pair[0];passed=$true})
        }
    }
} catch {
    $checks.Add(@{name='Layout usability';passed=$false;detail=$_.Exception.Message})
    try { Capture "$Language-failure.png" } catch { }
}
finally {
    @{passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0);checks=@($checks)} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'results.json')
    Write-Output "Layout usability results: $output"
}
if (@($checks|Where-Object{-not $_.passed}).Count) { exit 1 }
