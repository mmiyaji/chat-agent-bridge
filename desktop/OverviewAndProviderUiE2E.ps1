param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDir,
    [ValidateSet('en','ja')][string]$Language = 'en',
    [string]$FixtureExe
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = [IO.Path]::GetFullPath($DataDir)
$output = Join-Path $artifactRoot 'overview-provider-review'
$reportPath = Join-Path $output 'ui-results.json'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$script:appPid = $AppPid
$script:window = [IntPtr]::Zero
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OverviewProviderNative {
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
    $next=Find 'OnboardingNext'
    $scroll=Find $(if ($next -and -not $next.Current.IsOffscreen -and $next.Current.BoundingRectangle.Height -gt 0) { 'WizardScroll' } else { 'MainContentScroll' })
    if (-not $scroll) { throw "No test viewport for $Id" }
    $pattern=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 80 }
    for ($attempt=0;$attempt -lt 35;$attempt++) {
        $target=Find $Id
        if ($target) {
            $rect=$target.Current.BoundingRectangle; $viewport=$scroll.Current.BoundingRectangle
            if ($rect.Height -gt 0 -and -not $target.Current.IsOffscreen -and $rect.Top -ge $viewport.Top -and $rect.Bottom -le $viewport.Bottom) { return }
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
    if ($LASTEXITCODE -ne 0) { throw "winapp ui failed: $($Arguments -join ' ')" }
    return $result
}
function Root { [System.Windows.Automation.AutomationElement]::FromHandle($script:window) }
function Element([string]$Id, [switch]$Optional) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $all = (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
    $seen = @{}
    foreach ($item in $all) {
        if ($item.Current.ProcessId -ne $script:appPid) { continue }
        $runtimeId = [string]::Join('.', $item.GetRuntimeId())
        if ($seen.ContainsKey($runtimeId)) { continue }
        $seen[$runtimeId] = $true
        return $item
    }
    if ($Optional) { return $null }
    throw "UIA element not found: $Id"
}
function Visible($item) {
    if ($null -eq $item) { return $false }
    try { $r=$item.Current.BoundingRectangle; return (-not $item.Current.IsOffscreen -and $r.Width -gt 1 -and $r.Height -gt 1) }
    catch { return $false }
}
function Wait-Element([string]$Id,[int]$Seconds=12) {
    $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $item=Element $Id -Optional
        if (Visible $item) { return $item }
        if ($item) { try { Scroll-ToElement $Id } catch { } }
        Start-Sleep -Milliseconds 200
    }
    while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Visible UIA element did not appear: $Id"
}
function Value([string]$Id) {
    $item=Element $Id
    try { return [string]$item.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
    catch { return [string]((UI @('get-value',$Id) | ConvertFrom-Json).text) }
}
function Wait-Value([string]$Id,[string]$Expected,[int]$Seconds=12) {
    $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do { if ((Value $Id) -ceq $Expected) { return }; Start-Sleep -Milliseconds 200 } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Unexpected value for $Id; expected '$Expected'."
}
function Wait-Step([string]$Step) {
    $until=[DateTimeOffset]::UtcNow.AddSeconds(12)
    do { if ((Value 'OnboardingTitle').StartsWith($Step)) { return }; Start-Sleep -Milliseconds 150 } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Wizard did not advance to $Step"
}
function Click([string]$Id) { UI @('invoke',$Id) | Out-Null }
function Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw $Name }
    $checks.Add([pscustomobject]@{name=$Name;passed=$true})
}
function Capture([string]$Name) { UI @('screenshot','--focus','-o',(Join-Path $output $Name)) | Out-Null }
function Resize([int]$Width,[int]$Height) {
    $scale=[OverviewProviderNative]::GetDpiForWindow($script:window)/96.0
    if (-not [OverviewProviderNative]::SetWindowPos($script:window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) { throw 'Window resize failed.' }
    Start-Sleep -Milliseconds 350
}
function Select-ComboItem([string]$ComboId,[string]$ChoiceName) {
    UI @('scroll-into-view',$ComboId) | Out-Null
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $ready=UI @('get-property',$ComboId,'-p','IsEnabled') | ConvertFrom-Json
        if ($ready.properties.IsEnabled -eq $true) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    UI @('invoke',$ComboId) | Out-Null
    Start-Sleep -Milliseconds 250
    $tree=UI @('inspect',$ComboId,'-d','6') | ConvertFrom-Json
    function Find-Choice($node,[string]$name) {
        if ($null -eq $node) { return $null }
        if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string] -and $node -isnot [pscustomobject]) {
            foreach ($child in $node) { $found=Find-Choice $child $name; if ($found) { return $found } }
        } elseif ($node -is [pscustomobject]) {
            if ($node.type -eq 'ListItem' -and $node.name -ceq $name -and $node.selector) { return [string]$node.selector }
            foreach ($property in $node.PSObject.Properties) { $found=Find-Choice $property.Value $name; if ($found) { return $found } }
        }
        return $null
    }
    $selector=Find-Choice $tree $ChoiceName
    if (-not $selector) { throw "Exact ComboBox item not found: '$ChoiceName' in $ComboId." }
    # WinUI can remove a popup item after SelectionItemPattern succeeds. The
    # subsequent field and persistence assertions verify the actual selection.
    & winapp ui invoke $selector -a $script:appPid --json 2>$null | Out-Null
    Start-Sleep -Milliseconds 250
}
function Save-Provider([string]$Tag) {
    Click 'SaveSettings'
    $path=Join-Path $dataPath 'settings.json'; $until=[DateTimeOffset]::UtcNow.AddSeconds(12)
    do {
        if (Test-Path -LiteralPath $path) {
            try { $saved=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; if ($saved.AgentProvider -ceq $Tag) { return $saved } } catch { }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $until)
    throw "Provider '$Tag' was not persisted to settings.json."
}
function Visible-Names {
    $nodes=(Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    foreach ($node in $nodes) { if ($node.Current.ProcessId -eq $script:appPid -and $node.Current.Name) { [string]$node.Current.Name } }
}

try {
    UI @('wait-for','AppTitleBar','-t','10000') | Out-Null
    $process=Get-Process -Id $AppPid -ErrorAction Stop
    if ($process.ProcessName -ne 'ChatAgentBridge.Desktop') { throw 'AppPid must identify ChatAgentBridge.Desktop.' }
    $script:window=$process.MainWindowHandle
    if ($script:window -eq [IntPtr]::Zero) { throw 'AppPid has no main window.' }
    $artifactPrefix=$artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $exe=[IO.Path]::GetFullPath($process.MainModule.FileName)
    if (-not $exe.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'App executable must be inside desktop/artifacts.' }
    if (-not $dataPath.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-language-test'))) {
        throw 'DataDir must be a marked desktop/artifacts/.cab-language-test directory.'
    }
    $settingsPath=Join-Path $dataPath 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) { throw 'Use a fresh DataDir with no settings.json.' }
    if ([string]::IsNullOrWhiteSpace($FixtureExe)) { $FixtureExe=(Get-Command dotnet -ErrorAction Stop).Source }
    $dotnet=[IO.Path]::GetFullPath($FixtureExe)
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf) -or [IO.Path]::GetFileNameWithoutExtension($dotnet) -ne 'dotnet') { throw 'FixtureExe must be dotnet; the test only runs its version check.' }

    Wait-Element 'OnboardingDefer' | Out-Null
    Select-ComboItem 'DisplayLanguage' $(if ($Language -eq 'ja') { '日本語' } else { 'English' })
    $languagePath=Join-Path $dataPath 'language.json'; $languageUntil=[DateTimeOffset]::UtcNow.AddSeconds(8); $languageSaved=$false
    do {
        if (Test-Path -LiteralPath $languagePath) {
            try { $languageSaved=((Get-Content -LiteralPath $languagePath -Raw | ConvertFrom-Json).Language -ceq $Language) } catch { }
        }
        if ($languageSaved) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $languageUntil)
    Check $languageSaved "The $Language language selection persisted before entering the overview."
    UI @('scroll-into-view','WizardAcceptTerms') | Out-Null
    Click 'WizardAcceptTerms'; Click 'WizardAcceptPrivacy'; Click 'OnboardingNext'; Wait-Step '[2 / 5]'
    UI @('set-value','WizardWorkspaceRoot',(Join-Path $dataPath 'work')) | Out-Null
    Select-ComboItem 'WizardAgentProvider' 'Gemini CLI'
    Wait-Value 'WizardAgentExecutable' 'gemini'
    Select-ComboItem 'WizardAgentProvider' 'GitHub Copilot CLI'
    Wait-Value 'WizardAgentExecutable' 'copilot'
    Select-ComboItem 'WizardAgentProvider' $(if ($Language -eq 'ja') { 'その他のCLI（カスタム）' } else { 'Other CLI (custom)' })
    UI @('set-value','WizardAgentExecutable',$dotnet) | Out-Null
    UI @('set-value','WizardAgentArguments','') | Out-Null
    Click 'OnboardingNext'; Wait-Step '[3 / 5]'
    Click 'CheckCli'
    $until=[DateTimeOffset]::UtcNow.AddSeconds(15)
    do { if ((Value 'CliVersion') -match '^\d+\.\d+\.\d+') { break }; Start-Sleep -Milliseconds 150 } while ([DateTimeOffset]::UtcNow -lt $until)
    Check ((Value 'CliVersion') -match '^\d+\.\d+\.\d+') 'The wizard checks a custom CLI version without requiring a model catalog.'
    Click 'OnboardingNext'; Wait-Step '[4 / 5]'
    Check $true 'Gemini, GitHub Copilot and a custom CLI can be selected in the wizard and a checked custom CLI advances to chat setup.'
    Click 'OnboardingDefer'
    foreach ($id in @('OverviewAgentTile','OverviewConnectionTile','OverviewChatTile','OverviewCliTile','OverviewNextAction')) { Wait-Element $id | Out-Null }
    $banner=Element 'ExecutionStatus' -Optional
    Check (($null -eq $banner) -or -not (Visible $banner)) 'Initial settings success does not leave a permanent status banner.'
    $details=Element 'OverviewDetailsExpander'
    $expand=$details.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    Check ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) 'Setup details start collapsed.'
    Check (-not (Visible (Element 'SetupProgress' -Optional))) 'Collapsed details hide the detailed setup stepper.'
    Capture "$Language-overview-compact.png"

    $expand.Expand(); Start-Sleep -Milliseconds 400
    Wait-Element 'SetupStepOneStatus' | Out-Null; Wait-Element 'OverviewArchitecture' | Out-Null
    Check (($null -ne (Element 'SetupCompletionSummary')) -and (Visible (Element 'OverviewArchitecture'))) 'Expanded details expose setup summary and architecture.'
    Click 'NavAgentSettings'; Wait-Element 'AgentProvider' | Out-Null; Click 'NavOverview'; Wait-Element 'SetupStepOneStatus' | Out-Null
    Check ((Element 'OverviewDetailsExpander').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) 'Expanded details stay open after navigating away and back.'
    (Element 'OverviewDetailsExpander').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Resize 720 600; Capture "$Language-overview-720x600.png"
    foreach ($id in @('OverviewAgentTile','OverviewConnectionTile','OverviewChatTile','OverviewCliTile')) {
        $bounds=(Element $id).Current.BoundingRectangle; $clip=(Element 'MainContentScroll').Current.BoundingRectangle
        Check ($bounds.Left -ge $clip.Left -and $bounds.Right -le $clip.Right) "$id fits the 720 x 600 content width."
    }
    Resize 500 600; Capture "$Language-overview-500x600.png"
    foreach ($id in @('OverviewAgentTile','OverviewConnectionTile','OverviewChatTile','OverviewCliTile')) {
        $bounds=(Element $id).Current.BoundingRectangle; $clip=(Element 'MainContentScroll').Current.BoundingRectangle
        Check ($bounds.Left -ge $clip.Left -and $bounds.Right -le $clip.Right) "$id fits the 500 x 600 content width."
    }
    Resize 1160 800

    Click 'NavAgentSettings'; Wait-Element 'AgentProvider' | Out-Null
    UI @('set-value','WorkspaceRoot',(Join-Path $dataPath 'work')) | Out-Null
    Select-ComboItem 'AgentProvider' 'Gemini CLI'; Wait-Value 'AgentExecutable' 'gemini'; Wait-Value 'AgentArguments' '--output-format text'
    Wait-Element 'BasicProviderHelp' | Out-Null
    Check ((Visible (Element 'BasicProviderHelp' -Optional)) -and -not (Visible (Element 'AgentModel' -Optional))) 'Gemini uses CLI-owned model settings and shows provider help.'
    Check (-not (Visible (Element 'AgentPermissionCard' -Optional))) 'Gemini hides app-managed agent permission controls.'
    $gemini=Save-Provider 'gemini'
    Check ($gemini.AgentExecutable -eq 'gemini' -and $gemini.AgentArguments -eq '--output-format text') 'Gemini tag and CLI defaults persist.'

    Select-ComboItem 'AgentProvider' 'GitHub Copilot CLI'; Wait-Value 'AgentExecutable' 'copilot'; Wait-Value 'AgentArguments' '--silent --no-ask-user'
    Wait-Element 'BasicProviderHelp' | Out-Null
    Check ((Visible (Element 'BasicProviderHelp' -Optional)) -and -not (Visible (Element 'FetchModels' -Optional))) 'GitHub Copilot hides the app catalog and shows provider help.'
    Check (-not (Visible (Element 'AgentPermissionCard' -Optional))) 'GitHub Copilot hides app-managed agent permission controls.'
    $copilot=Save-Provider 'github-copilot'
    Check ($copilot.AgentExecutable -eq 'copilot' -and $copilot.AgentArguments -eq '--silent --no-ask-user') 'GitHub Copilot tag and CLI defaults persist.'

    Select-ComboItem 'AgentProvider' $(if ($Language -eq 'ja') { 'その他のCLI（詳細設定）' } else { 'Other CLI (advanced settings)' })
    UI @('set-value','AgentExecutable',$dotnet) | Out-Null
    UI @('set-value','AgentArguments','') | Out-Null
    Wait-Value 'AgentExecutable' $dotnet; Wait-Value 'AgentArguments' ''
    Click 'SettingsCheckCli'
    UI @('scroll-into-view','SettingsCliCheckSummary') | Out-Null
    $expectedVersion=(& $dotnet --version | Out-String).Trim()
    $until=[DateTimeOffset]::UtcNow.AddSeconds(15); $versionFound=$false
    do { $versionFound=@(Visible-Names | Where-Object { $_ -ceq $expectedVersion }).Count -gt 0; if ($versionFound) { break }; Start-Sleep -Milliseconds 250 } while ([DateTimeOffset]::UtcNow -lt $until)
    Check $versionFound 'Custom CLI check returns the installed dotnet version output.'
    $summary=(UI @('get-value','SettingsCliCheckSummary') | ConvertFrom-Json).text
    Check ([string]$summary -notmatch '(?i)logged in|authenticated|login succeeded') 'Version result makes no authentication claim.'
    Capture "$Language-custom-cli-version.png"

    UI @('set-value','AgentArguments','--version') | Out-Null
    Click 'NavOverview'; Wait-Element 'OverviewCliTile' | Out-Null
    $tileName=(Element 'OverviewCliTile').Current.Name
    Check ($tileName -match '(?i)not checked|needs verification|未確認|未チェック') 'Changing CLI arguments invalidates the previous verification.'
    Click 'NavAgentSettings'; Wait-Element 'AgentProvider' | Out-Null; UI @('set-value','AgentArguments','') | Out-Null
    $custom=Save-Provider 'custom'
    Check ($custom.AgentExecutable -eq $dotnet -and [string]::IsNullOrEmpty([string]$custom.AgentArguments)) 'Custom provider persists the dotnet executable with blank arguments.'
    $final=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Check ($final.AgentProvider -eq 'custom') 'Final settings contain the custom provider tag.'
    Capture "$Language-custom-provider-saved.png"
} catch {
    $checks.Add([pscustomobject]@{name='UI regression run';passed=$false;detail=$_.Exception.Message})
} finally {
    [pscustomobject]@{
        appPid=$AppPid; language=$Language; dataDir=$dataPath; fixtureExe=$FixtureExe
        passed=(@($checks | Where-Object { -not $_.passed }).Count -eq 0)
        checks=@($checks)
        screenshots=@(Get-ChildItem -LiteralPath $output -Filter '*.png' -ErrorAction SilentlyContinue | ForEach-Object Name)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
Get-Content -LiteralPath $reportPath -Raw
if (@($checks | Where-Object { -not $_.passed }).Count) { exit 1 }
