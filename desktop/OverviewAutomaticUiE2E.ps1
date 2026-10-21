param([Parameter(Mandatory)][string]$Layout,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja')
$ErrorActionPreference='Stop'
$fixturePath=[IO.Path]::GetFullPath($DataDir)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $fixturePath.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixturePath)){throw 'Use a new isolated directory under desktop/artifacts.'}
New-Item -ItemType Directory $fixturePath|Out-Null
New-Item -ItemType File (Join-Path $fixturePath '.cab-activity-test')|Out-Null
# Synthetic credentials are never connected: launch with auto-connect off, restore it before exit.
Add-Type -AssemblyName System.Security
function Protect-Fixture([string]$Value){return 'dpapi:'+[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Value),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))}
@{SchemaVersion=1;WorkspaceRoot=$fixturePath;AgentProvider='claude';AgentExecutable='claude';AgentArguments='--print --output-format stream-json --verbose';AutoConnectProvider='none';SlackBotToken=(Protect-Fixture 'xoxb-fixture-only');SlackAppToken=(Protect-Fixture 'xapp-fixture-only');SlackAllowedUserIds='U0000000001'}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'settings.json')
@{SchemaVersion=1;Records=@()}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'history.json')
@{Language=$Language}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'language.json')
$layoutPath=[IO.Path]::GetFullPath($Layout)
$started=winapp run $layoutPath --manifest (Join-Path $layoutPath 'AppxManifest.xml') --output-appx-directory (Join-Path $layoutPath 'AppX') --detach --json -- --data-dir $fixturePath|ConvertFrom-Json
if(-not $started.ProcessId){throw 'Test app did not launch'}
$testAppProcessId=[int]$started.ProcessId
winapp ui wait-for AppTitleBar -a $testAppProcessId -t 10000 --json|Out-Null
if($LASTEXITCODE){throw 'Test app window did not initialize'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $testAppProcessId -DataDir $fixturePath -Language $Language -OnlyHelpers
$output=Join-Path $fixturePath 'automatic-review';New-Item -ItemType Directory $output|Out-Null
$off=if($Language -eq 'ja'){'オフ'}else{'Off'}
function Overview{Go 'NavOverview';Scroll-To 'OverviewAutoConnectTile'}
function AutoChoice([string]$Id){UI @('invoke','AutoConnectProvider')|Out-Null;UI @('invoke',$Id)|Out-Null}
function Save-GeneralSettings{Scroll-To 'SaveGeneralSettings';UI @('invoke','SaveGeneralSettings')|Out-Null}
try{
    Resize 1160;Overview
    Wait-Text 'OverviewAutoConnectStatus' $off
    $startupSummary=Value 'OverviewStartupStatus'
    Assert ($startupSummary.Length -gt 0 -and $startupSummary -notmatch '確認中|Checking') 'Startup status did not resolve'
    UI @('invoke','OverviewStartupTile')|Out-Null;Wait-Visible 'StartWithWindows' $true
    $startupToggle=(Element 'StartWithWindows').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Pass 'Startup card exposes actual OS state and jumps to the Windows sign-in setting'
    Overview;UI @('invoke','OverviewAutoConnectTile')|Out-Null;Wait-Visible 'AutoConnectProvider' $true
    Assert ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'AutoConnectProvider') 'Automatic connection control was not focused'
    AutoChoice 'AutoConnectOptionSlack';Overview
    Wait-Text 'OverviewAutoConnectStatus' $off
    Assert (Visible (Element 'OverviewDraftNotice')) 'Unsaved settings notice missing'
    Pass 'Draft auto-connect choice does not masquerade as saved configuration'
    UI @('invoke','OverviewAutoConnectTile')|Out-Null;Save-GeneralSettings
    Overview;Wait-Text 'OverviewAutoConnectStatus' 'Slack'
    Assert ((Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json).AutoConnectProvider -eq 'slack') 'Slack auto-connect was not saved'
    Pass 'Saved automatic connection shows the selected provider'
    UI @('invoke','OverviewAutoConnectTile')|Out-Null;AutoChoice 'AutoConnectOptionMattermost';Save-GeneralSettings
    Overview;Wait-Text 'OverviewAutoConnectStatus' 'Mattermost'
    Wait-Text 'OverviewAutoConnectStatus' $(if($Language -eq 'ja'){'設定が必要'}else{'Set up Mattermost'})
    Pass 'Incomplete automatic connection shows setup needed'
    UI @('invoke','OverviewAutoConnectTile')|Out-Null;AutoChoice 'AutoConnectOptionNone';Save-GeneralSettings
    Assert ((Element 'StartWithWindows').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq $startupToggle) 'Windows startup was changed by navigation'
    foreach($theme in @('light','dark')){
        Go 'NavOverview';$themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
        Scroll-To $themeId;UI @('invoke',$themeId)|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Overview;Wait-Text 'OverviewAutoConnectStatus' $off
            foreach($id in @('OverviewStartupTile','OverviewAutoConnectTile')){
                $r=(Element $id).Current.BoundingRectangle;$w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($r.Left -ge $w.Left -and $r.Right -le $w.Right) "$id is clipped at $width"
            }
            Start-Sleep -Milliseconds 400;Capture "automatic-$theme-$width.png"
        }
    }
    Pass 'Both cards fit Light/Dark at 500 and 1160 DIP; optional Off stays neutral'
    Assert ((Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json).AutoConnectProvider -eq 'none') 'Fixture auto-connect was not restored'
    Pass 'Navigation leaves Windows startup unchanged and no automatic connection enabled'
}catch{$checks.Add(@{Name='Automatic settings overview';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{$checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize;Write-Output "Test app PID: $testAppProcessId"}
