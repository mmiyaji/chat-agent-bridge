param([Parameter(Mandatory)][int]$AppProcessId,[Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja',[switch]$LayoutOnly)
$ErrorActionPreference='Stop'
$proc=Get-Process -Id $AppProcessId
$layout=Split-Path (Split-Path $proc.Path)
[xml]$manifest=Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
if(-not ([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.',[StringComparison]::Ordinal)){throw 'Use the isolated Review package.'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$output=Join-Path $dataPath ('mattermost-thread-link-'+$Language)
New-Item -ItemType Directory -Path $output -Force|Out-Null
$state=Get-Content "$PSScriptRoot/artifacts/mattermost-e2e.json" -Raw|ConvertFrom-Json
$anchorPath=Join-Path $PSScriptRoot 'artifacts/mattermost-thread-anchors.json'
$saved=Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json
if($state.serverUrl -ne 'http://127.0.0.1:8070' -or $saved.MattermostServerUrl -ne $state.serverUrl -or $saved.AutoConnectProvider -ne 'none'){throw 'Use the isolated localhost:8070 fixture.'}
$agentRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/mattermost-ui-agent'))
if($saved.AgentProvider -ne 'claude' -or $saved.AgentExecutable -ne (Join-Path $agentRoot 'claude.exe') -or (Get-FileHash $saved.AgentExecutable).Hash -ne (Get-FileHash (Join-Path $agentRoot 'ChatAgentBridge.TestAgent.exe')).Hash){throw 'Only the marked offline Claude fixture is allowed.'}
function Input-Text([string]$Id,[string]$Text){(Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)}
function Input-Value([string]$Id){return [string](Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Enabled([string]$Id){$e=Element $Id;return ($e -and $e.Current.IsEnabled)}
function Ensure([string]$Id){
    UI @('wait-for','MainContentScroll','-t','5000')|Out-Null
    UI @('wait-for',$Id,'-t','5000')|Out-Null
    $sp=(Element 'MainContentScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $minimum=30*[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
    $window=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
    $top=(Element 'SaveConnections').Current.BoundingRectangle.Bottom+12
    for($i=0;$i -lt 70;$i++){
        $target=Element $Id
        if($target){
            $r=$target.Current.BoundingRectangle
            if(-not $target.Current.IsOffscreen -and $r.Height -ge $minimum -and $r.Top -ge $top -and $r.Bottom -le $window.Bottom-12){return}
        }
        if(-not $sp.Current.VerticallyScrollable){break}
        if($i -eq 0){$sp.SetScrollPercent(-1,0)}
        elseif($sp.Current.VerticalScrollPercent -ge 100){break}
        else{$sp.SetScrollPercent(-1,[Math]::Min(100,$sp.Current.VerticalScrollPercent+5))}
        Start-Sleep -Milliseconds 250
    }
    throw "Could not fully expose $Id"
}
function Scope{
    Go 'NavConnections'
    UI @('wait-for','ConnectionsJump','-t','5000')|Out-Null
    UI @('invoke','ConnectionsJump')|Out-Null
    winapp ui invoke JumpScope -a $AppProcessId --json|Out-Null
    if($LASTEXITCODE){throw 'Could not open conversation scope settings.'}
}
function Resolve([string]$Link){
    Ensure 'MattermostThreadLookupInput';Input-Text 'MattermostThreadLookupInput' $Link
    Ensure 'ResolveMattermostThreadButton';UI @('invoke','ResolveMattermostThreadButton')|Out-Null
    UI @('wait-for','ResolveMattermostThreadButton','-p','IsEnabled','--value','True','-t','20000')|Out-Null
}
function Save{
    UI @('invoke','SaveConnections')|Out-Null
    Wait-Text 'Title' $(if($Language -eq 'ja'){'設定を保存しました'}else{'Settings saved'})
    $until=[DateTimeOffset]::UtcNow.AddSeconds(8)
    do{$s=Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json;if($s.MattermostAllowedThreadIds -eq (Input-Value 'MattermostAllowedThreadIds').Trim()){return};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until)
    throw 'Saving the conversation scope failed.'
}
function Api([string]$Method,[string]$Path,[string]$Token,$Body=$null){
    try{
        $requestArgs=@{Method=$Method;Uri=$state.serverUrl+$Path;Headers=@{Authorization='Bearer '+$Token};TimeoutSec=10;ErrorAction='Stop'}
        if($null -ne $Body){$requestArgs.ContentType='application/json; charset=utf-8';$requestArgs.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Compress))}
        return Invoke-RestMethod @requestArgs
    }catch{throw "Isolated Mattermost request failed: $Method $Path"}
}
function Post([string]$Token,[string]$Marker,[string]$Root=''){
    return Api Post '/api/v4/posts' $Token @{channel_id=$state.channel.id;root_id=$Root;message='!cab '+$Marker}
}
function Records{return ,@((Get-Content (Join-Path $dataPath 'history.json') -Raw|ConvertFrom-Json).Records)}
function Bindings{
    $path=Join-Path $dataPath 'sessions.json'
    if(Test-Path $path){return ,@((Get-Content $path -Raw|ConvertFrom-Json).Bindings)}
    return ,@()
}
function Connect{
    Go 'NavChat'
    if(Enabled 'DisconnectChat'){UI @('invoke','DisconnectChat')|Out-Null}
    UI @('wait-for','ConnectMattermost','-p','IsEnabled','--value','True','-t','10000')|Out-Null
    UI @('invoke','ConnectMattermost')|Out-Null
    UI @('wait-for','ChatStatus','--value',$(if($Language -eq 'ja'){'接続しました'}else{'Connected'}),'--contains','-t','20000')|Out-Null
}
function Posts([string]$Root){
    $thread=Api Get "/api/v4/posts/$Root/thread" $state.memberUser.token
    return ,@($thread.posts.PSObject.Properties|ForEach-Object {$_.Value})
}
function Approve-IfNeeded([string]$Marker){
    $until=[DateTimeOffset]::UtcNow.AddSeconds(12)
    do{
        Input-Text 'InboxSearch' $Marker
        $list=Element 'InboxConversations'
        $items=$list.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
        if($items.Count -gt 0){
            $items[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            if(Enabled 'ApproveChat'){
                UI @('invoke','ApproveChat')|Out-Null
                UI @('wait-for','ChatRunConfirmation','-t','10000')|Out-Null
                UI @('invoke','PrimaryButton')|Out-Null
                return
            }
            if((Value 'ChatOutput').Contains($Marker)){return}
        }
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $until)
    throw 'The permitted request did not become available for approval.'
}
function Wait-Reply([string]$Root,[string]$Marker){
    $until=[DateTimeOffset]::UtcNow.AddSeconds(20)
    do{
        $reply=@((Posts $Root)|Where-Object {$_.user_id -eq $state.bridgeUser.id -and $_.message.Contains('claude-e2e:') -and $_.message.Contains($Marker)})
        if($reply.Count -gt 0){return $reply[-1]}
        Start-Sleep -Milliseconds 150
    }while([DateTimeOffset]::UtcNow -lt $until)
    throw 'A permitted thread request did not receive the offline agent result.'
}
try{
    if($LayoutOnly){$anchors=Get-Content $anchorPath -Raw|ConvertFrom-Json}
    else{
        $root=Api Post '/api/v4/posts' $state.memberUser.token @{channel_id=$state.channel.id;message='CAB thread-link test anchor '+[guid]::NewGuid().ToString('N')}
        $response=Api Post '/api/v4/posts' $state.memberUser.token @{channel_id=$state.channel.id;root_id=$root.id;message='CAB reply-link test anchor'}
        $anchors=[pscustomobject]@{RootPostId=$root.id;ReplyPostId=$response.id;RootLink=$state.serverUrl+'/'+$state.team.name+'/pl/'+$root.id;ReplyLink=$state.serverUrl+'/'+$state.team.name+'/pl/'+$response.id}
        $anchors|ConvertTo-Json|Set-Content $anchorPath
    }
    Resize 1160;Scope
    if(-not $LayoutOnly){
        Input-Text 'MattermostAllowedThreadIds' ''
        Input-Text 'MattermostAllowedUserIds' $state.memberUser.id
        Save
    }
    $hash=(Get-FileHash (Join-Path $dataPath 'settings.json')).Hash
    Resolve $anchors.RootLink
    Wait-Text 'MattermostThreadLookupResult' $anchors.RootPostId
    if(-not $LayoutOnly){
        Assert ((Input-Value 'MattermostAllowedThreadIds') -eq '') 'Lookup granted a scope before Add.'
        Resolve $anchors.ReplyLink
        Wait-Text 'MattermostThreadLookupResult' $anchors.RootPostId
        Assert (-not (Value 'MattermostThreadLookupResult').Contains($anchors.ReplyPostId)) 'Reply link used its own ID instead of the root.'
        Pass 'Actual root and reply links resolve to the same existing conversation without changing permissions'
        Input-Text 'MattermostThreadLookupInput' ($anchors.ReplyLink+'?unexpected=1')
        Assert (-not (Enabled 'AddMattermostThreadButton')) 'Editing a link retained an addable result'
        Resolve ($anchors.ReplyLink.Replace('127.0.0.1','localhost'))
        Assert (-not (Enabled 'AddMattermostThreadButton')) 'A different server origin remained addable'
        $wrong=Api Post '/api/v4/posts' $state.memberUser.token @{channel_id=$state.otherChannel.id;message='CAB wrong-channel link fixture'}
        Resolve ($state.serverUrl+'/'+$state.team.name+'/pl/'+$wrong.id)
        Assert (-not (Enabled 'AddMattermostThreadButton')) 'A different channel remained addable'
        Pass 'Changed, cross-server and wrong-channel links cannot be added'
        Resolve $anchors.ReplyLink;Wait-Text 'MattermostThreadLookupResult' $anchors.RootPostId
        Ensure 'AddMattermostThreadButton';UI @('invoke','AddMattermostThreadButton')|Out-Null
        UI @('invoke','AddMattermostThreadButton')|Out-Null
        Assert ((Input-Value 'MattermostAllowedThreadIds').Trim() -eq $anchors.RootPostId) 'Add did not retain exactly one root conversation ID'
        Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $hash) 'Lookup or Add implicitly saved settings'
        Save
        Assert ((Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json).MattermostAllowedThreadIds -eq $anchors.RootPostId) 'Saved scope differs from the resolved root'
        Pass 'Explicit Add deduplicates; explicit Save persists the parent conversation ID'
        Connect
        $marker='allowed-thread-'+[guid]::NewGuid().ToString('N')
        $null=Post $state.memberUser.token $marker $anchors.RootPostId
        Approve-IfNeeded $marker
        $reply=Wait-Reply $anchors.RootPostId $marker
        Assert ($reply.root_id -eq $anchors.RootPostId) 'Reply escaped its allowed conversation'
        Pass 'After reconnect, the permitted user can execute and receive a result inside the registered conversation'
        $before=(Records).Count;$beforeBindings=(Bindings).Count
        $newMarker='denied-new-thread-'+[guid]::NewGuid().ToString('N')
        $outsiderMarker='denied-other-user-'+[guid]::NewGuid().ToString('N')
        $new=Post $state.memberUser.token $newMarker
        $null=Post $state.unauthorizedUser.token $outsiderMarker $anchors.RootPostId
        Start-Sleep -Milliseconds 2200
        Assert ((Records).Count -eq $before -and (Bindings).Count -eq $beforeBindings) 'Rejected actor or new thread reached execution/session storage'
        foreach($pair in @(@($new.id,$newMarker),@($anchors.RootPostId,$outsiderMarker))){
            Assert (@((Posts $pair[0])|Where-Object {$_.user_id -eq $state.bridgeUser.id -and $_.message.Contains($pair[1])}).Count -eq 0) 'Rejected request received a bridge response'
            Input-Text 'InboxSearch' $pair[1];Start-Sleep -Milliseconds 150
            $queued=(Element 'InboxConversations').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
            Assert ($queued.Count -eq 0) 'Rejected request appeared in the approval inbox'
        }
        Pass 'The same user cannot start an unlisted conversation; an unlisted user cannot use the allowed conversation'
        Go 'NavChat';UI @('invoke','DisconnectChat')|Out-Null
        UI @('wait-for','ConnectMattermost','-p','IsEnabled','--value','True','-t','10000')|Out-Null
    }
    foreach($theme in @('light','dark')){
        Go 'NavAppearance';Scroll-To $(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'})
        UI @('invoke',$(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}))|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Scope;Ensure 'AddMattermostThreadButton'
            foreach($id in @('MattermostThreadLookupInput','ResolveMattermostThreadButton','MattermostThreadLookupResult','AddMattermostThreadButton')){
                $r=(Element $id).Current.BoundingRectangle;$w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($r.Left -ge $w.Left -and $r.Right -le $w.Right) "$id clips horizontally"
            }
            Capture "thread-link-$theme-$width.png"
        }
    }
    Pass 'Link entry, confirmation and Add fit Light/Dark at 500 and 1160 DIP'
}catch{$checks.Add(@{Name='Real Mattermost thread link';Status='FAIL';Detail=$_.Exception.Message});throw}
finally{
    if(Enabled 'DisconnectChat'){
        try{Go 'NavChat';UI @('invoke','DisconnectChat')|Out-Null}catch{Write-Warning 'Could not disconnect the isolated Review app.'}
    }
    $checks|ConvertTo-Json -Depth 6|Set-Content (Join-Path $output $(if($LayoutOnly){'results-layout.json'}else{'results.json'}))
    $checks|Format-Table -AutoSize
}
