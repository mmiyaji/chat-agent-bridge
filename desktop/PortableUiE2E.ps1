param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$ExpectedVersion = '0.1.0.2'
)
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$source = [IO.Path]::GetFullPath($AppDirectory)
if (-not $source.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path (Join-Path $source 'AppxManifest.xml'))) { throw 'Use an unpackaged test payload below desktop/artifacts.' }
$testRoot = Join-Path $artifacts ('portable-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$layout = Join-Path $testRoot 'Portable app 日本語'
Copy-Item -LiteralPath $source -Destination $layout -Recurse
$exe = Join-Path $layout 'ChatAgentBridge.Desktop.exe'
$data = Join-Path $layout 'Data'
if (Test-Path -LiteralPath $data) { throw 'The source payload contains personal data.' }
$runName = 'ChatAgentBridge.Portable'
function ReadRunCommand {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { if ($key) { return $key.GetValue($runName) } } finally { if ($key) { $key.Dispose() } }
}
$initialRun = ReadRunCommand
if ($null -ne $initialRun) { throw 'Existing portable startup registration: refusing to change it during testing.' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class PortableUiPackageProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);
}
'@
$checks = [Collections.Generic.List[string]]::new()
$app = $null
function UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $app.Id --json 2>&1
    if ($LASTEXITCODE) { throw "UI command failed: $($Arguments -join ' '): $($output -join ' ')" }
    return $output
}
function Element([string]$Id) {
    $app.Refresh()
    $root = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Pass([string]$Name) { $checks.Add($Name); Write-Output "PASS $Name" }
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function ChooseLanguage([string]$Name) {
    $combo = Element 'DisplayLanguage'
    $pattern = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand()
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))
    $until = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $items = @([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) |
            Where-Object {-not $_.Current.IsOffscreen} | Group-Object { $_.GetRuntimeId() -join ',' } | ForEach-Object {$_.Group[0]})
        if ($items.Count -eq 1) { break }; Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    Assert ($items.Count -eq 1) "Language choice unavailable: $Name"
    $items[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $pattern.Collapse()
}
function Go([string]$Id,[string]$Target) {
    UI @('invoke',$Id) | Out-Null
    UI @('wait-for',$Target,'-t','5000') | Out-Null
}
function WaitStartupIdle {
    UI @('wait-for','StartWithWindows','-p','IsEnabled','--value','True','-t','6000') | Out-Null
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $null = $start.Environment.Remove('CAB_WINUI_DATA_DIR')
    $app = [Diagnostics.Process]::Start($start)
    UI @('wait-for','OnboardingTitle','-t','10000') | Out-Null
    [uint32]$length = 0
    Assert ([PortableUiPackageProbe]::GetPackageFullName($app.Handle,[ref]$length,$null) -eq 15700) 'App unexpectedly has package identity.'
    Pass 'Direct EXE launch from another working directory has no package identity'
    ChooseLanguage 'English'
    UI @('wait-for','OnboardingTitle','--value','Introduction and terms','--contains','-t','5000') | Out-Null
    Assert ((Get-Content (Join-Path $data 'language.json') -Raw | ConvertFrom-Json).Language -eq 'en') 'Language was not saved beside the EXE.'
    Pass 'First-run language changes immediately and saves into portable Data'
    UI @('invoke','OnboardingDefer') | Out-Null
    Go 'NavAppearance' 'ThemeLight'
    UI @('invoke','ThemeLight') | Out-Null
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path (Join-Path $data 'appearance.json')) -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 50 }
    Assert ((Get-Content (Join-Path $data 'appearance.json') -Raw | ConvertFrom-Json).Theme -eq 'light') 'Theme was not saved beside the EXE.'
    Pass 'Appearance saves in the portable folder'
    Go 'NavOverview' 'OverviewStartupTile'
    UI @('invoke','OverviewStartupTile') | Out-Null
    WaitStartupIdle
    $toggle = Element 'StartWithWindows'
    $pattern = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $pattern.Toggle()
    Assert (-not $toggle.Current.IsEnabled) 'Startup toggle did not block a second click.'
    WaitStartupIdle
    $command = ReadRunCommand
    Assert ($command -eq ('"' + $exe + '"')) 'Startup command did not preserve the executable path or duplicated the default data path.'
    $pattern.Toggle()
    WaitStartupIdle
    Assert ($null -eq (ReadRunCommand)) 'Disabling startup left its Run entry.'
    Pass 'Per-user startup can be enabled and disabled; repeated input is blocked'
    Go 'NavAppearance' 'DisplayLanguage'
    ChooseLanguage '日本語 (Japanese)'
    UI @('wait-for','LanguageRestartDialog','-t','5000') | Out-Null
    $oldId = $app.Id
    UI @('invoke','PrimaryButton') | Out-Null
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $newApp = Get-Process | Where-Object { $_.Id -ne $oldId -and $_.ProcessName -eq 'ChatAgentBridge.Desktop' -and $_.Path -eq $exe } | Select-Object -First 1
        if ($newApp) { break }; Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    Assert ($null -ne $newApp) 'Portable language restart did not launch a new instance.'
    $app = $newApp
    UI @('wait-for','NavAbout','-t','10000') | Out-Null
    Go 'NavAbout' 'AboutPackage'
    UI @('wait-for','AboutPackage','--value',"Portable ($ExpectedVersion)",'-t','5000') | Out-Null
    Assert ((Get-Content (Join-Path $data 'language.json') -Raw | ConvertFrom-Json).Language -eq 'ja') 'Restart lost the language setting.'
    Assert ((Get-Content (Join-Path $data 'appearance.json') -Raw | ConvertFrom-Json).Theme -eq 'light') 'Restart lost the appearance setting.'
    Pass 'Language restart preserves portable settings and displays the correct deployment version'
    UI @('screenshot','-o',(Join-Path $testRoot 'portable-final.png')) | Out-Null
} finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id }
    # This value was absent before the test. Never touch other startup registrations.
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
    try { if ($runKey) { $runKey.DeleteValue($runName, $false) } } finally { if ($runKey) { $runKey.Dispose() } }
    @{Checks=@($checks);DataDirectory=$data;Executable=$exe} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $testRoot 'results.json')
    Write-Output "Portable UI results: $testRoot"
}
