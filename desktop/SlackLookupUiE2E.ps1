param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Username,
    [Parameter(Mandatory)][string]$ExpectedUserId,
    [Parameter(Mandatory)][string]$MessageLink,
    [Parameter(Mandatory)][string]$ExpectedRootTimestamp,
    [Parameter(Mandatory)][string]$ExpectedChannelId,
    [ValidateSet('ja','en')][string]$Language='ja',
    [switch]$LayoutOnly,
    [switch]$OnlyHelpers
)

$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDir)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path (Join-Path $dataPath '.cab-activity-test'))) {
    throw 'Use a marked isolated activity fixture below desktop/artifacts.'
}

$process=Get-Process -Id $AppProcessId
$layout=Split-Path (Split-Path $process.Path)
[xml]$manifest=Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
if (-not ([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.',[StringComparison]::Ordinal)) {
    throw 'Use the already running isolated Review package.'
}

$requestedOnlyHelpers=[bool]$OnlyHelpers
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$settingsPath=Join-Path $dataPath 'settings.json'
$saved=Get-Content $settingsPath -Raw|ConvertFrom-Json
if ($saved.AutoConnectProvider -ne 'none') { throw 'Auto-connect must be disabled for this fixture.' }
foreach($tokenName in @('SlackBotToken','SlackAppToken')){
    $token=[string]$saved.$tokenName
    if (-not $token.StartsWith('dpapi:',[StringComparison]::Ordinal)) { throw 'Both Slack tokens must be DPAPI-protected in fixture settings.' }
    $token=$null
}
$agentRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/mattermost-ui-agent'))
$expectedAgent=Join-Path $agentRoot 'claude.exe'
$testAgent=Join-Path $agentRoot 'ChatAgentBridge.TestAgent.exe'
if ($saved.AgentProvider -ne 'claude' -or
    [IO.Path]::GetFullPath([string]$saved.AgentExecutable) -ne [IO.Path]::GetFullPath($expectedAgent) -or
    (Get-FileHash $expectedAgent).Hash -ne (Get-FileHash $testAgent).Hash) {
    throw 'Only the marked offline Claude fixture is allowed.'
}

$output=Join-Path $dataPath ('slack-lookup-'+$Language)
New-Item -ItemType Directory -Path $output -Force|Out-Null
$checks=[Collections.Generic.List[object]]::new()
$script:hwnd=[IntPtr]((winapp ui list-windows -a $AppProcessId --json|ConvertFrom-Json|Where-Object title -eq 'Chat Agent Bridge'|Select-Object -First 1).hwnd)
if ($script:hwnd -eq [IntPtr]::Zero) { throw 'Review app window is missing.' }

function Assert([bool]$Condition,[string]$Message){ if(-not $Condition){ throw $Message } }
function UI([string[]]$Arguments){
    $result=winapp ui @Arguments -w $script:hwnd --json 2>$null
    if($LASTEXITCODE){ throw "UI action failed: $($Arguments[0]) $($Arguments[1])" }
    return $result
}
function Element([string]$Id){
    $window=[Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Visible($El){ return $El -and -not $El.Current.IsOffscreen -and $El.Current.BoundingRectangle.Height -gt 0 }
function Enabled([string]$Id){ $el=Element $Id; return ($el -and $el.Current.IsEnabled) }
function Input-Text([string]$Id,[string]$Text){ (Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text) }
function Input-Value([string]$Id){ return [string](Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Value([string]$Id){ return [string](UI @('get-value',$Id)|ConvertFrom-Json).text }
function Pass([string]$Text){ $checks.Add(@{Name=$Text;Status='PASS'}); Write-Output "PASS: $Text" }
function Wait-Element([string]$Id,[int]$TimeoutSeconds=8){
    $until=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do{ if(Element $Id){ return };Start-Sleep -Milliseconds 100 }while([DateTimeOffset]::UtcNow -lt $until)
    throw "Timed out waiting for $Id."
}
function Wait-Enabled([string]$Id,[bool]$Expected,[int]$TimeoutSeconds=15){
    $until=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do{ if((Enabled $Id) -eq $Expected){ return };Start-Sleep -Milliseconds 100 }while([DateTimeOffset]::UtcNow -lt $until)
    throw "Enabled state did not settle for $Id."
}
function Wait-Text([string]$Id,[string]$Text,[int]$TimeoutSeconds=15){
    $until=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do{ if((Element $Id) -and (Value $Id).Contains($Text)){ return };Start-Sleep -Milliseconds 100 }while([DateTimeOffset]::UtcNow -lt $until)
    throw "Expected result was not shown in $Id."
}
function Wait-NonEmpty([string]$Id,[int]$TimeoutSeconds=15){
    $until=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do{ if((Element $Id) -and -not [string]::IsNullOrWhiteSpace((Value $Id))){ return };Start-Sleep -Milliseconds 100 }while([DateTimeOffset]::UtcNow -lt $until)
    throw "No completion status appeared in $Id."
}
function Wait-MainContent{
    UI @('wait-for','MainContentScroll','-t','5000')|Out-Null
    Wait-Element 'MainContentScroll'
}
function Go([string]$Id){
    if(-not (Visible (Element $Id))){ UI @('invoke','TogglePaneButton')|Out-Null }
    UI @('invoke',$Id)|Out-Null
    Start-Sleep -Milliseconds 120
    if($Id -eq 'NavChat'){Wait-Element 'ConnectSlack'}else{Wait-MainContent}
}
function Scope([string]$Target='JumpScope'){
    Go 'NavConnections'
    UI @('wait-for','ConnectionsJump','-t','5000')|Out-Null
    UI @('invoke','ConnectionsJump')|Out-Null
    winapp ui invoke $Target -a $AppProcessId --json|Out-Null
    if($LASTEXITCODE){throw 'Could not select the connection settings section.'}
    Wait-MainContent
    Wait-Element 'SlackUsernameLookupInput'
}
function Ensure([string]$Id){
    Wait-MainContent
    UI @('wait-for',$Id,'-t','5000')|Out-Null
    $scroll=Element 'MainContentScroll'
    $sp=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $minimum=30
    $target=Element $Id
    if($target.Current.ControlType -ne [Windows.Automation.ControlType]::Button){$minimum=1}
    $window=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
    $save=Element 'SaveConnections'
    $top=if(Visible $save){$save.Current.BoundingRectangle.Bottom+12}else{$window.Top+12}
    $dpi=[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)
    $minHeight=$minimum*$dpi/96.0
    for($i=0;$i -lt 70;$i++){
        $target=Element $Id
        if($target){
            $r=$target.Current.BoundingRectangle
            if((Visible $target) -and $r.Height -ge $minHeight -and $r.Top -ge $top -and $r.Bottom -le $window.Bottom-12){ return }
        }
        if(-not $sp.Current.VerticallyScrollable){ break }
        if($i -eq 0){ $sp.SetScrollPercent(-1,0) }
        elseif($sp.Current.VerticalScrollPercent -ge 100){ break }
        else{ $sp.SetScrollPercent(-1,[Math]::Min(100,$sp.Current.VerticalScrollPercent+5)) }
        Start-Sleep -Milliseconds 250
    }
    throw "Could not fully expose $Id."
}
function Resize([int]$Width){
    $scale=[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
    Assert ([ActivityLayoutNative]::SetWindowPos($script:hwnd,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int](860*$scale),0x0010)) 'Resize failed.'
    Start-Sleep -Milliseconds 180
}
function Capture([string]$Name){ Start-Sleep -Milliseconds 150;UI @('screenshot','-o',(Join-Path $output $Name))|Out-Null }
function Scope-Value([string]$Id){ return (Input-Value $Id).Trim() }
function Assert-VisibleWithinWindow([string]$Id,[bool]$Button=$false){
    Ensure $Id
    $el=Element $Id;$r=$el.Current.BoundingRectangle
    $window=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
    $scale=[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
    $minimum=if($Button){30*$scale}else{1}
    Assert (-not $el.Current.IsOffscreen -and $r.Height -ge $minimum -and $r.Left -ge $window.Left -and $r.Right -le $window.Right -and $r.Top -ge $window.Top -and $r.Bottom -le $window.Bottom) "$Id is clipped or too small."
}
function Save-Connections{
    UI @('invoke','SaveConnections')|Out-Null
    Wait-Text 'Title' $(if($Language -eq 'ja'){'設定を保存しました'}else{'Settings saved'}) 10
    $until=[DateTimeOffset]::UtcNow.AddSeconds(8)
    do{
        $current=Get-Content $settingsPath -Raw|ConvertFrom-Json
        if((([string]$current.SlackAllowedUserIds).Trim() -eq (Scope-Value 'SlackAllowedUserIds')) -and
           (([string]$current.SlackAllowedChannelIds).Trim() -eq (Scope-Value 'SlackAllowedChannelIds')) -and
           (([string]$current.SlackAllowedThreadIds).Trim() -eq (Scope-Value 'SlackAllowedThreadIds'))){ return }
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $until)
    throw 'Explicit Save did not persist the Slack scopes.'
}
function Lookup-Username{
    Ensure 'SlackUsernameLookupInput';Input-Text 'SlackUsernameLookupInput' $Username
    Ensure 'LookupSlackUserButton';UI @('invoke','LookupSlackUserButton')|Out-Null
    Wait-Enabled 'AddSlackUserButton' $true 25
    Wait-Text 'SlackUsernameLookupResult' $ExpectedUserId 5
    Wait-NonEmpty 'SlackUsernameLookupStatus' 5
}
function Lookup-Thread{
    Ensure 'SlackThreadLookupInput';Input-Text 'SlackThreadLookupInput' $MessageLink
    Ensure 'ResolveSlackThreadButton';UI @('invoke','ResolveSlackThreadButton')|Out-Null
    Wait-Enabled 'AddSlackThreadButton' $true 25
    Wait-Text 'SlackThreadLookupResult' $ExpectedChannelId 5
    Wait-Text 'SlackThreadLookupResult' $ExpectedRootTimestamp 5
    Wait-NonEmpty 'SlackThreadLookupStatus' 5
}

if($requestedOnlyHelpers){return}

try{
    Scope 'JumpSlack'
    $savedBefore=Get-Content $settingsPath -Raw|ConvertFrom-Json
    $savedHash=(Get-FileHash $settingsPath).Hash
    $userBefore=Scope-Value 'SlackAllowedUserIds'
    $channelsBefore=Scope-Value 'SlackAllowedChannelIds'
    $threadsBefore=Scope-Value 'SlackAllowedThreadIds'

    if(-not $LayoutOnly){
        # The marked fixture is isolated; start each lookup from a known empty draft.
        Input-Text 'SlackAllowedUserIds' ''
        Input-Text 'SlackAllowedChannelIds' ''
        Input-Text 'SlackAllowedThreadIds' ''
        $userBefore='';$channelsBefore='';$threadsBefore=''
    }
    Lookup-Username
    Assert ((Scope-Value 'SlackAllowedUserIds') -eq $userBefore -and
            (Scope-Value 'SlackAllowedChannelIds') -eq $channelsBefore -and
            (Scope-Value 'SlackAllowedThreadIds') -eq $threadsBefore) 'Username lookup changed an allowlist.'
    Assert ((Get-FileHash $settingsPath).Hash -eq $savedHash) 'Username lookup saved settings implicitly.'
    Pass 'Username lookup resolves the expected ID without changing or saving scopes'

    if($LayoutOnly){
        Lookup-Thread
        Assert ((Get-FileHash $settingsPath).Hash -eq $savedHash) 'Read-only lookup changed saved settings.'
        Pass 'Thread lookup resolves the expected channel and root without saving scopes'
    }else{
        Ensure 'AddSlackUserButton';UI @('invoke','AddSlackUserButton')|Out-Null
        UI @('invoke','AddSlackUserButton')|Out-Null
        Assert ((Scope-Value 'SlackAllowedUserIds') -eq $ExpectedUserId) 'Add did not retain exactly one expected Slack user ID.'
        Assert ((Get-FileHash $settingsPath).Hash -eq $savedHash) 'Add saved settings implicitly.'
        Pass 'Add inserts exactly one expected user ID and remains unsaved until explicit Save'
        Save-Connections
        $savedAfterUser=Get-Content $settingsPath -Raw|ConvertFrom-Json
        Assert (([string]$savedAfterUser.SlackAllowedUserIds).Trim() -eq $ExpectedUserId) 'Saved Slack user scope differs from the lookup result.'
        Pass 'Explicit Save persists the resolved Slack user ID'

        $scopeHash=(Get-FileHash $settingsPath).Hash
        Lookup-Thread
        Assert ((Scope-Value 'SlackAllowedChannelIds') -eq '' -and (Scope-Value 'SlackAllowedThreadIds') -eq '') 'Thread lookup changed channel or thread permissions.'
        Assert ((Get-FileHash $settingsPath).Hash -eq $scopeHash) 'Thread lookup changed saved settings.'
        Input-Text 'SlackThreadLookupInput' ($MessageLink+'?unexpected=1')
        Wait-Enabled 'AddSlackThreadButton' $false 5
        Pass 'Editing a resolved message link invalidates its Add action'
        Lookup-Thread
        Ensure 'AddSlackThreadButton';UI @('invoke','AddSlackThreadButton')|Out-Null
        UI @('invoke','AddSlackThreadButton')|Out-Null
        Assert ((Scope-Value 'SlackAllowedChannelIds') -eq $ExpectedChannelId) 'Add did not retain exactly the expected channel ID.'
        Assert ((Scope-Value 'SlackAllowedThreadIds') -eq $ExpectedRootTimestamp) 'Add did not retain exactly the expected root timestamp.'
        Assert ((Get-FileHash $settingsPath).Hash -eq $scopeHash) 'Thread Add saved settings implicitly.'
        Pass 'Add inserts the resolved channel and root once without saving'
        Save-Connections
        $savedAfterThread=Get-Content $settingsPath -Raw|ConvertFrom-Json
        Assert (([string]$savedAfterThread.SlackAllowedUserIds).Trim() -eq $ExpectedUserId -and
                ([string]$savedAfterThread.SlackAllowedChannelIds).Trim() -eq $ExpectedChannelId -and
                ([string]$savedAfterThread.SlackAllowedThreadIds).Trim() -eq $ExpectedRootTimestamp) 'Explicit Save did not persist the expected user, channel and root scopes.'
        Pass 'Explicit Save persists the resolved channel and root scopes'

        Go 'NavChat'
        if(Enabled 'DisconnectChat'){ UI @('invoke','DisconnectChat')|Out-Null }
        UI @('wait-for','ConnectSlack','-p','IsEnabled','--value','True','-t','10000')|Out-Null
        UI @('invoke','ConnectSlack')|Out-Null
        UI @('wait-for','ChatStatus','--value',$(if($Language -eq 'ja'){'接続しました'}else{'Connected'}),'--contains','-t','25000')|Out-Null
        Pass 'Slack connects from the saved scopes'
        UI @('invoke','DisconnectChat')|Out-Null
        UI @('wait-for','ConnectSlack','-p','IsEnabled','--value','True','-t','10000')|Out-Null
        UI @('invoke','ConnectSlack')|Out-Null
        UI @('wait-for','ChatStatus','--value',$(if($Language -eq 'ja'){'接続しました'}else{'Connected'}),'--contains','-t','25000')|Out-Null
        Pass 'Slack reconnects using the saved scopes'
        Go 'NavChat';UI @('invoke','DisconnectChat')|Out-Null
        UI @('wait-for','ConnectSlack','-p','IsEnabled','--value','True','-t','10000')|Out-Null
        Pass 'Slack is disconnected at test completion'
    }

    foreach($theme in @('light','dark')){
        Go 'NavOverview'
        $themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
        UI @('wait-for',$themeId,'-t','5000')|Out-Null
        UI @('scroll-into-view',$themeId)|Out-Null
        UI @('invoke',$themeId)|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Scope 'JumpSlack'
            foreach($pair in @(
                @('SlackUsernameLookupInput','LookupSlackUserButton','SlackUsernameLookupResult','SlackUsernameLookupStatus','AddSlackUserButton'),
                @('SlackThreadLookupInput','ResolveSlackThreadButton','SlackThreadLookupResult','SlackThreadLookupStatus','AddSlackThreadButton')
            )){
                foreach($id in $pair){
                    $button=$id -in @('LookupSlackUserButton','ResolveSlackThreadButton','AddSlackUserButton','AddSlackThreadButton')
                    Assert-VisibleWithinWindow $id $button
                }
                $captureKind=if($pair[0] -eq 'SlackUsernameLookupInput'){'username'}else{'thread'}
                Capture "slack-lookup-$theme-$width-$captureKind.png"
            }
        }
    }
    Pass 'Username and thread lookup controls fit Light and Dark at 500 and 1160 DIP'
    if(-not $LayoutOnly){
        Go 'NavChat';if(Enabled 'DisconnectChat'){UI @('invoke','DisconnectChat')|Out-Null}
        UI @('wait-for','ConnectSlack','-p','IsEnabled','--value','True','-t','10000')|Out-Null
    }
}catch{
    $checks.Add(@{Name='Slack lookup UI';Status='FAIL';Detail='See script exception.'})
    throw
}finally{
    try{
        if(Enabled 'DisconnectChat'){
            Go 'NavChat';UI @('invoke','DisconnectChat')|Out-Null
            UI @('wait-for','ConnectSlack','-p','IsEnabled','--value','True','-t','10000')|Out-Null
        }
    }catch{ Write-Warning 'Could not disconnect the isolated Review app.' }
    $checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output $(if($LayoutOnly){'results-layout.json'}else{'results.json'}))
    $checks|Format-Table -AutoSize
}
