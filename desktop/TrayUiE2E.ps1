param([Parameter(Mandatory)][string]$AppDirectory)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not $appRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) { throw 'Use an unpackaged test payload below desktop/artifacts.' }
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath (Join-Path $appRoot 'Assets/AppIcon.ico'))) {
    throw 'The unpackaged payload or its tray icon is missing.'
}
if (@(Get-Process ChatAgentBridge.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count) {
    throw 'This payload is already running; refusing to interact with an existing app instance.'
}

$runRoot = Join-Path $artifactRoot ('tray-ui-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$checks = [Collections.Generic.List[object]]::new()
$app = $null
$hwnd = [IntPtr]::Zero
New-Item -ItemType Directory -Path $runRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TrayUiE2ENativeApi {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Pass([string]$Name) { $checks.Add(@{name=$Name; passed=$true}); Write-Output "PASS $Name" }
function UI([string[]]$Arguments) {
    if (-not $app -or $app.HasExited) { throw 'The isolated tray test app exited unexpectedly.' }
    $output = & winapp ui @Arguments -a $app.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed ($LASTEXITCODE): $($Arguments -join ' ') $($output -join ' ')" }
    return $output -join "`n"
}
function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
    $script:app = [Diagnostics.Process]::Start($start)
    UI @('wait-for','NavOverview','-t','15000') | Out-Null
    UI @('wait-for','SettingsLocation','--value',$data,'--contains','-t','5000') | Out-Null
    UI @('invoke','NavOverview') | Out-Null
    UI @('scroll-into-view','ExitCompletelyButton') | Out-Null
    UI @('wait-for','ExitCompletelyButton','-t','5000') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $app.Refresh(); $candidate = [IntPtr]$app.MainWindowHandle
        if ($candidate -ne [IntPtr]::Zero) { $script:hwnd = $candidate; break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($hwnd -ne [IntPtr]::Zero) 'Could not find the isolated app HWND.'
    [uint32]$owner = 0
    [void][TrayUiE2ENativeApi]::GetWindowThreadProcessId($hwnd, [ref]$owner)
    Assert ($owner -eq $app.Id) 'Refusing to post messages to a window owned by another process.'
    UI @('wait-for','CloseToTrayToggle','-t','5000') | Out-Null
}
function Capture-Narrow([string]$Name) {
    [void][TrayUiE2ENativeApi]::SetWindowPos($hwnd,[IntPtr]::Zero,80,80,720,740,0x0014)
    UI @('scroll-into-view','ExitCompletelyButton') | Out-Null
    UI @('wait-for','CloseToTrayToggle','-t','5000') | Out-Null
    Start-Sleep -Milliseconds 400
    UI @('screenshot','-o',(Join-Path $runRoot $Name)) | Out-Null
}
function Toggle-Pattern {
    $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $element = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'CloseToTrayToggle'))
    return $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
}
function Toggle-Preference { (Toggle-Pattern).Toggle() }
function Assert-ToggleState([bool]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (((Toggle-Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -eq $Expected) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Unexpected tray toggle state.'
}
function Wait-Visible([bool]$Visible, [int]$Seconds=5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ([TrayUiE2ENativeApi]::IsWindowVisible($hwnd) -eq $Visible) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Window visibility did not become $Visible."
}
function Tray-Callback([uint16]$Event) {
    # NOTIFYICON_VERSION_4 packs event in LOWORD(lParam), icon ID 1 in HIWORD(lParam).
    $packed = [IntPtr][long]((1 -shl 16) -bor $Event)
    if ($Event -eq 0x007B) {
        Assert ([TrayUiE2ENativeApi]::PostMessageW($hwnd, 0x82B1, [IntPtr]::Zero, $packed)) 'Could not post the tray menu callback.'
    } else {
        $result = [IntPtr]::Zero
        Assert ([TrayUiE2ENativeApi]::SendMessageTimeoutW($hwnd,0x82B1,[IntPtr]::Zero,$packed,2,5000,[ref]$result) -ne [IntPtr]::Zero) 'Tray activation timed out.'
    }
}
function Wait-Preference([bool]$Expected) {
    $path = Join-Path $data 'background.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (Test-Path -LiteralPath $path) {
            $value = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).CloseToTray
            if ([bool]$value -eq $Expected) { return }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "background.json did not persist CloseToTray=$Expected."
}
function Invoke-MenuItem([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $tree = winapp ui inspect -a $app.Id --interactive --json | ConvertFrom-Json
        foreach ($window in $tree.windows) {
            foreach ($item in $window.elements) {
                if ($item.type -eq 'MenuItem' -and $item.name -eq $Name) {
                    winapp ui invoke $item.selector -w $window.hwnd --json | Out-Null
                    if ($LASTEXITCODE) { throw 'Native menu invocation failed.' }
                    return
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Native tray menu item was not available: $Name"
}
function Close-OwnedApp {
    if ($app -and -not $app.HasExited) {
        try { [void][TrayUiE2ENativeApi]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
        if (-not $app.WaitForExit(3000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
}

try {
    # Existing fixture seeds valid settings and completed onboarding without credentials.
    & (Join-Path $PSScriptRoot 'New-UsageActivityFixture.ps1') -DataDir $data -Language ja -CostFixture | Out-Null
    @{schemaVersion=3;step=4;status='completed'} | ConvertTo-Json | Set-Content (Join-Path $data 'onboarding.json') -Encoding utf8
    $consentSource = Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw
    $policy = [regex]::Match($consentSource, 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert (-not [string]::IsNullOrWhiteSpace($policy)) 'Could not read the current onboarding policy version.'
    @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $data 'consent.json') -Encoding utf8
    @{Theme='dark'} | ConvertTo-Json | Set-Content (Join-Path $data 'appearance.json') -Encoding utf8
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw
    @{schemaVersion=1;autoUpdateEnabled=$false;lastCheckedUtc=[DateTimeOffset]::UtcNow.ToString('o');lastUpdatedUtc=[DateTimeOffset]::UtcNow.ToString('o');etag=$null;catalogJson=$catalog} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'pricing-auto.json') -Encoding utf8

    Start-App
    Assert-ToggleState $false
    Assert (-not (Test-Path -LiteralPath (Join-Path $data 'background.json'))) 'Default false should not require a saved preference.'
    Capture-Narrow 'ja-dark-720.png'
    Pass 'Japanese dark tray settings render at 720px with the default disabled'

    Toggle-Preference
    Assert-ToggleState $true
    Wait-Preference $true
    [void][TrayUiE2ENativeApi]::PostMessageW($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Visible $false
    Assert (-not $app.HasExited) 'Closing with the preference enabled should hide, not exit.'
    Pass 'Enabled close hides the window while its process remains alive'

    Tray-Callback 0x0400
    Wait-Visible $true
    [void][TrayUiE2ENativeApi]::ShowWindow($hwnd,6) # SW_MINIMIZE
    Start-Sleep -Milliseconds 400
    Assert ([TrayUiE2ENativeApi]::IsIconic($hwnd)) 'The test window did not minimize.'
    Tray-Callback 0x0401
    Wait-Visible $true
    Start-Sleep -Milliseconds 400
    Assert (-not [TrayUiE2ENativeApi]::IsIconic($hwnd)) 'Tray activation did not restore the minimized window.'
    UI @('invoke','MoveToTrayButton') | Out-Null
    Wait-Visible $false
    Tray-Callback 0x007B
    $menuResx = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Localization/DesktopStrings.ja.resx'
    [xml]$resources = Get-Content -LiteralPath $menuResx -Raw
    Invoke-MenuItem $resources.SelectSingleNode("/root/data[@name='TrayMenuOpen']/value").InnerText
    Wait-Visible $true
    Pass 'Native tray click and localized context-menu Open restore a hidden or minimized window'

    Tray-Callback 0x007B
    Invoke-MenuItem $resources.SelectSingleNode("/root/data[@name='TrayMenuExit']/value").InnerText
    Assert ($app.WaitForExit(10000)) 'Native tray menu Exit did not terminate the isolated app.'
    Assert ((Get-Content (Join-Path $data 'background.json') -Raw | ConvertFrom-Json).CloseToTray) 'Tray menu Exit changed the enabled preference.'
    Pass 'Native tray context-menu Exit completes shutdown while close-to-tray is enabled'

    Start-App
    Assert-ToggleState $true
    Toggle-Preference
    Assert-ToggleState $false
    Wait-Preference $false
    [void][TrayUiE2ENativeApi]::PostMessageW($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    Assert ($app.WaitForExit(10000)) 'Closing with close-to-tray disabled should exit.'
    Pass 'The saved preference survives restart; disabling it makes × exit'

    # Exercise explicit complete exit while the preference is enabled, using English/light at the same narrow width.
    $languagePath = Join-Path $data 'language.json'
    @{Language='en'} | ConvertTo-Json | Set-Content $languagePath -Encoding utf8
    @{Theme='light'} | ConvertTo-Json | Set-Content (Join-Path $data 'appearance.json') -Encoding utf8
    Start-App
    Capture-Narrow 'en-light-720.png'
    Toggle-Preference
    Wait-Preference $true
    UI @('invoke','ExitCompletelyButton') | Out-Null
    Assert ($app.WaitForExit(10000)) 'Complete exit did not terminate the isolated app while close-to-tray was enabled.'
    Pass 'English light layout screenshot captured; explicit complete exit bypasses close-to-tray'
} catch {
    $checks.Add(@{name='Tray UI E2E';passed=$false;detail=$_.Exception.Message})
    throw
} finally {
    Close-OwnedApp
    @{checks=@($checks);dataDirectory=$data;appDirectory=$appRoot}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output "Tray UI results: $runRoot"
}

if (@($checks | Where-Object { -not $_.passed }).Count) { exit 1 }
