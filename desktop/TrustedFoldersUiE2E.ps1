param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja',[switch]$LayoutOnly)
$ErrorActionPreference='Stop'
$proc=Get-Process -Id $AppProcessId
$layout=Split-Path (Split-Path $proc.Path)
[xml]$manifest=Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
if(-not ([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.',[StringComparison]::Ordinal)){throw 'Use an isolated review package.'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$output=Join-Path $dataPath ('trusted-folders-review-'+$Language);New-Item -ItemType Directory $output -Force|Out-Null
$work=Join-Path $dataPath 'work';$picked=Join-Path $dataPath 'picked'
if(-not (Test-Path $work) -or -not (Test-Path $picked)){throw 'Create work and picked directories in the isolated fixture.'}
function Ensure-Control([string]$Id){
    $el=Element $Id
    if($el){
        $r=$el.Current.BoundingRectangle
        $v=(Element 'MainContentScroll').Current.BoundingRectangle
        $w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
        if((Visible $el) -and $r.Top -ge $v.Top -and $r.Bottom -le [Math]::Min($v.Bottom,$w.Bottom-12)){return}
    }
    Scroll-To $Id
}
function Set-Roots([string]$Text){UI @('set-value','TrustedWorkspaceRoots',$Text)|Out-Null}
function RootText{return [string](Element 'TrustedWorkspaceRoots').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Roots{return ,@((RootText) -split '\r\n|\r|\n'|Where-Object {$_})}
function Pick([bool]$Cancel,[string]$Path=$picked){
    Ensure-Control 'ChooseTrustedFolder';UI @('invoke','ChooseTrustedFolder')|Out-Null
    $until=[DateTimeOffset]::UtcNow.AddSeconds(10)
    do{
        $picker=winapp ui list-windows -a $AppProcessId --json|ConvertFrom-Json|Where-Object className -eq '#32770'|Select-Object -First 1
        if($picker){break};Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $until)
    Assert ([bool]$picker) 'Folder picker did not open'
    foreach($id in @('ChooseTrustedFolder','AddCurrentWorkspaceTrustedFolder')){Assert (-not (Element $id).Current.IsEnabled) 'Folder actions were not disabled while choosing'}
    $elements=(winapp ui inspect -w $picker.hwnd --interactive --json|ConvertFrom-Json).windows.elements
    if(-not $Cancel){winapp ui set-value '1152' $Path -w $picker.hwnd --json|Out-Null;if($LASTEXITCODE){throw 'Native folder field could not be filled'}}
    $target=$elements|Where-Object {$_.type -in @('Button','SplitButton') -and $_.automationId -eq $(if($Cancel){'2'}else{'1'})}|Select-Object -First 1
    Assert ([bool]$target) 'Folder picker action missing'
    winapp ui invoke $target.selector -w $picker.hwnd --json|Out-Null
    if($LASTEXITCODE){throw 'Folder picker action failed'}
    UI @('wait-for','ChooseTrustedFolder','-p','IsEnabled','--value','True','-t','5000')|Out-Null
}
try{
    Resize 1160;Go 'NavAgentSettings';Scroll-To 'TrustedWorkspaceRoots'
    if(-not $LayoutOnly){
    Set-Roots ''
    Ensure-Control 'AddCurrentWorkspaceTrustedFolder';UI @('invoke','AddCurrentWorkspaceTrustedFolder')|Out-Null
    Wait-Text 'TrustedWorkspaceRoots' $work
    UI @('invoke','AddCurrentWorkspaceTrustedFolder')|Out-Null
    Assert ((Roots).Count -eq 1) 'Current folder was duplicated'
    Pass 'Current work folder can be added once without typing'
    Set-Roots '';Pick $true
    Assert ((RootText) -eq '') 'Picker cancellation edited the list'
    Pass 'Picker cancellation preserves the draft and restores enabled actions'
    Pick $false
    Assert ((Roots).Count -eq 2 -and (Roots) -contains $work -and (Roots) -contains $picked) 'First unrelated folder replaced the implicit current trust scope'
    Pick $false
    Assert ((Roots).Count -eq 2) 'Picked folder was duplicated'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Adding a folder implicitly saved settings'
    Pass 'Native picker adds a folder, preserves implicit current trust, deduplicates, and does not auto-save'
    Set-Roots '';Pick $false $dataPath
    Assert ((Roots).Count -eq 1 -and (Roots)[0] -eq $dataPath) 'Picking a parent redundantly added its child'
    Pass 'Picking a parent needs only one entry for its descendants'
    Set-Roots 'relative-path';Ensure-Control 'AddCurrentWorkspaceTrustedFolder';UI @('invoke','AddCurrentWorkspaceTrustedFolder')|Out-Null
    Assert ((RootText) -eq 'relative-path') 'Invalid manual entries were overwritten'
    Wait-Text 'TrustedFolderStatus' $(if($Language -eq 'ja'){'修正'}else{'Fix'})
    Set-Roots $dataPath
    Assert ([string]::IsNullOrEmpty((Value 'TrustedFolderStatus'))) 'Old feedback survived manual correction'
    UI @('invoke','SaveSettings')|Out-Null
    $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
    do{$saved=(Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json).TrustedWorkspaceRoots;if($saved -eq $dataPath){break};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until)
    Assert ($saved -eq $dataPath) 'Explicit Save did not persist the parent root'
    Pass 'Invalid manual input is preserved, feedback clears on edit, and explicit Save applies the list'
    }
    foreach($theme in @('light','dark')){
        Go 'NavOverview';$id=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'};Scroll-To $id;UI @('invoke',$id)|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Go 'NavAgentSettings';Ensure-Control 'AddCurrentWorkspaceTrustedFolder'
            foreach($buttonId in @('ChooseTrustedFolder','AddCurrentWorkspaceTrustedFolder')){
                $r=(Element $buttonId).Current.BoundingRectangle;$w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($r.Left -ge $w.Left -and $r.Right -le $w.Right) "$buttonId clips at $width"
            }
            Start-Sleep -Milliseconds 350;Capture "trusted-$theme-$width.png"
        }
    }
    Pass 'Both actions and explanation fit Light/Dark at 500 and 1160 DIP'
    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'Folder editing changed execution history'
}catch{$checks.Add(@{Name='Trusted folder UI';Status='FAIL';Detail="$($_.Exception.Message)"});throw}
finally{
    $picker=winapp ui list-windows -a $AppProcessId --json|ConvertFrom-Json|Where-Object className -eq '#32770'|Select-Object -First 1
    if($picker){winapp ui invoke '2' -w $picker.hwnd --json|Out-Null}
    $checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json');$checks|Format-Table -AutoSize
}
