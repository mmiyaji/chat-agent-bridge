# Requires an isolated portable publish with CABPortableUpdateTest=true under desktop/artifacts.
# The fixture replaces release transport; no live release download, chat, or AI execution is needed.
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$UpdateArchive,
    [ValidateSet('ja','en')][string[]]$Languages = @('ja','en')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UpdateNoticeWindow {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$install = (Resolve-Path -LiteralPath $AppDirectory).Path
if (-not $install.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated payload under desktop/artifacts.' }
$archive = (Resolve-Path -LiteralPath $UpdateArchive).Path
$root = Join-Path $artifacts ('update-notice-ui-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $root 'fixture'
$assets = Join-Path $fixture 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$assetName = [IO.Path]::GetFileName($archive)
if ($assetName -notmatch '^ChatAgentBridge\.Portable-(\d+\.\d+\.\d+(?:-preview\.\d+)?)-win-x64\.zip$') { throw 'Use a named x64 update archive.' }
$version = $Matches[1]
$asset = Join-Path $assets $assetName
Copy-Item -LiteralPath $archive -Destination $asset
$release = @{
    version = $version; tag = "v$version"; assetName = $assetName
    downloadUri = "https://github.com/mmiyaji/chat-agent-bridge/releases/download/v$version/$assetName"
    sha256 = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant(); size = (Get-Item $archive).Length
    releasePage = "https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v$version"
}
function Json([string]$Path, $Value) { ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
Json (Join-Path $fixture 'releases.json') @(@{
    tag_name = $release.tag; draft = $false; prerelease = $true; html_url = $release.releasePage
    assets = @(@{name = $assetName; size = $release.size; digest = 'sha256:' + $release.sha256; browser_download_url = $release.downloadUri})
})
Json (Join-Path $install 'portable-release.json') @{schemaVersion=1; releaseVersion='0.1.0-preview.8'; architecture='x64'}
$checks = [Collections.Generic.List[object]]::new()
$app = $null
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Find([string]$Id) {
    $app.Refresh()
    if ($app.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    $window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Wait([scriptblock]$Condition,[string]$Description,[int]$Seconds=30) {
    $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if ($app.HasExited) { throw "App exited while waiting for $Description" }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out waiting for $Description"
}
function Element([string]$Id) { Wait { $null -ne (Find $Id) } $Id; Find $Id }
function Visible([string]$Id) { $item=Find $Id; $item -and -not $item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Height -gt 0 }
function Invoke([string]$Id) {
    $item=Element $Id
    $pattern=$null
    if ($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke() }
    elseif ($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select() }
    else { throw "No invoke/selection pattern for $Id" }
}
function Capture([string]$Name) {
    & winapp ui screenshot -a $app.Id -o (Join-Path $root ($Name+'.png')) --json | Out-Null
    if ($LASTEXITCODE) { throw 'Screenshot failed.' }
}
function Top {
    $scroll=Element 'AboutScroll'
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0) }
    Start-Sleep -Milliseconds 150
}
function Size([int]$Width) {
    $app.Refresh(); $scale=[UpdateNoticeWindow]::GetDpiForWindow($app.MainWindowHandle)/96.0
    Assert ([UpdateNoticeWindow]::SetWindowPos($app.MainWindowHandle,[IntPtr]::Zero,20,20,[int]($Width*$scale),[int](840*$scale),0x0010)) 'Resize failed.'
    Start-Sleep -Milliseconds 200
}
function Pass([string]$Name) { $checks.Add(@{name=$Name;passed=$true}); Write-Output "PASS $Name" }
try {
    foreach ($language in $Languages) { foreach ($theme in @('light','dark')) {
        $scenario="$language-$theme"
        $data=Join-Path $root $scenario
        New-Item -ItemType Directory -Path $data | Out-Null
        Json (Join-Path $data 'settings.json') @{SchemaVersion=1; WorkspaceRoot=$data; TrustedWorkspaceRoots=$data; AgentProvider='custom'; AgentExecutable='unused'; AgentMode='exec'; AutoConnectProvider='none'}
        Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
        Json (Join-Path $data 'language.json') @{Language=$language}
        Json (Join-Path $data 'appearance.json') @{Theme=$theme}
        Json (Join-Path $data 'portable-update.json') @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$true;lastAttemptUtc=[DateTimeOffset]::UtcNow;availableRelease=$release}
        Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe'))
        $start.UseShellExecute=$false; $start.WorkingDirectory=$install
        $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
        $start.Environment['CAB_PORTABLE_UPDATE_TEST_DIRECTORY']=$fixture
        $app=[Diagnostics.Process]::Start($start)
        Element 'NavAbout' | Out-Null
        Invoke 'NavAbout'; Size 1160; Top
        Wait { Visible 'PortableUpdateNoticeAction' } 'cached update banner'
        $notice=Element 'PortableUpdateNotice'
        $noticeNames=@($notice.Current.Name) + @($notice.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
        Assert (($noticeNames -join ' ').Contains($version)) 'Available version is missing from banner.'
        $downloadText=if($language -eq 'ja'){'更新をダウンロード'}else{'Download update'}
        Assert ((Element 'PortableUpdateNoticeAction').Current.Name -eq $downloadText) 'Download action is not localized.'
        Assert (-not (Visible 'PortableUpdateStatus') -and -not (Visible 'PortableDownloadUpdate')) 'Available notice duplicates legacy status/actions.'
        Capture "$scenario-available"
        Size 500; Top
        $action=(Element 'PortableUpdateNoticeAction').Current.BoundingRectangle
        $window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).Current.BoundingRectangle
        Assert ((Visible 'PortableUpdateNoticeAction') -and $action.Left -ge $window.Left -and $action.Right -le $window.Right) 'Narrow update action is clipped.'
        Capture "$scenario-available-narrow"
        Pass "$scenario cached update, localized accent action, no duplicates and narrow layout"
        Size 1160; Top
        # A bad local asset exercises the real download failure without any network access.
        [IO.File]::WriteAllText($asset,'invalid fixture')
        Invoke 'PortableUpdateNoticeAction'
        Wait { (Find 'PortableUpdateStatus') -and (Find 'PortableDownloadUpdate') -and (Find 'PortableDownloadUpdate').Current.IsEnabled -and (Visible 'PortableUpdateStatus') } 'download failure retry'
        Assert (-not (Visible 'PortableUpdateNoticeAction')) 'Availability banner hides download failure.'
        Assert ((Element 'PortableUpdateStatus').Current.Name -match $(if($language -eq 'ja'){'更新できませんでした'}else{'Could not update the portable app'})) 'Download failure text is missing.'
        Capture "$scenario-download-error"
        Copy-Item -LiteralPath $archive -Destination $asset -Force
        Invoke 'PortableDownloadUpdate'
        Wait { $null -ne (Find 'PortableUpdateApplyConfirmation') } 'download and restart offer' 60
        Invoke 'CloseButton'
        Wait { (Visible 'PortableUpdateNoticeAction') -and (Find 'PortableUpdateNoticeAction').Current.IsEnabled } 'ready update action'
        $readyText=if($language -eq 'ja'){'更新して再起動'}else{'Update and restart'}
        Assert ((Element 'PortableUpdateNoticeAction').Current.Name -eq $readyText) 'Ready action is not localized.'
        Assert (-not (Visible 'PortableApplyUpdate') -and -not (Visible 'PortableUpdateStatus')) 'Ready notice duplicates legacy controls.'
        Top; Capture "$scenario-ready"
        Size 500; Top; Capture "$scenario-ready-narrow"
        Assert (Visible 'PortableUpdateNoticeAction') 'Ready action is unreachable in narrow window.'
        Invoke 'PortableUpdateNoticeAction'
        Element 'PortableUpdateApplyConfirmation' | Out-Null
        Invoke 'CloseButton'
        Pass "$scenario failure remains visible, retry stages update, ready action opens cancellable restart dialog"
        Invoke 'AboutExit'; Element 'WindowCloseConfirmation' | Out-Null; Invoke 'PrimaryButton'
        Assert ($app.WaitForExit(15000)) 'App did not exit normally.'
        $app=$null
    }}
    Json (Join-Path $root 'results.json') @{passed=$true;checks=@($checks);root=$root}
    Write-Output "PASS Update notice UI smoke: $root"
} catch {
    $checks.Add(@{name='Update notice UI';passed=$false;detail=$_.Exception.Message})
    if ($app -and -not $app.HasExited) { try { Capture 'failure' } catch {} }
    Json (Join-Path $root 'results.json') @{passed=$false;checks=@($checks);root=$root}
    throw
} finally { if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force } }
