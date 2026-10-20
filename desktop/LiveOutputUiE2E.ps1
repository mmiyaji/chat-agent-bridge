param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [ValidateSet('ja','en','both')][string]$Language='both',
    [switch]$Portable
)
$ErrorActionPreference='Stop'
if($Language -eq 'both'){
    foreach($locale in @('en','ja')){
        & $PSCommandPath -AppDirectory $AppDirectory -TestAgentPath $TestAgentPath -Language $locale -Portable:$Portable
    }
    return
}
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$runRoot=Join-Path $artifactRoot ('live-output-ui-'+[guid]::NewGuid().ToString('N'))
$dataPath=Join-Path $runRoot 'private-data'
$workspace=Join-Path $runRoot 'workspace'
[IO.Directory]::CreateDirectory($dataPath)|Out-Null
[IO.Directory]::CreateDirectory($workspace)|Out-Null
Set-Content -LiteralPath (Join-Path $runRoot '.cab-live-output-fixture') 'Isolated offline live-output UI test'
$gate=Join-Path $runRoot '.cab-live-output-release'
$agent=[IO.Path]::GetFullPath($TestAgentPath)
if (-not (Test-Path -LiteralPath $agent -PathType Leaf) -or [IO.Path]::GetExtension($agent) -ne '.exe') {
    throw 'Pass the self-contained ChatAgentBridge.TestAgent.exe. Framework-dependent fixture builds do not run in portable test environments.'
}
$version=& $agent --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join ' ') -notmatch 'offline fixture') { throw 'The executable is not the offline ChatAgentBridge.TestAgent fixture.' }
$checks=[Collections.Generic.List[object]]::new()
$appProcess=$null;$server=$null;$endpoint=$null
$priorFixture=$env:CAB_LIVE_OUTPUT_FIXTURE
$priorDelay=$env:CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS
$env:CAB_LIVE_OUTPUT_FIXTURE='1'
$env:CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS='700'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
function Write-Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8}
function UI([string[]]$Arguments){$out=& winapp ui @Arguments -a $appProcess.Id --json 2>&1;if($LASTEXITCODE -ne 0){throw "UI action failed: $($Arguments -join ' '): $($out -join ' ')"};return $out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)}
function Element([string]$Id){(Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Scoped-Element([string]$Scope,[string]$Id){$root=Element $Scope;if(-not $root){return $null};return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Value([string]$Id,[string]$Scope=''){$el=if($Scope){Scoped-Element $Scope $Id}else{Element $Id};if(-not $el){throw "Missing UI control $Id"};$pattern=$el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern);return [string]$pattern.Current.Value}
function Follow([bool]$Enabled){
    $el=Element 'LiveOutputFollow';if(-not $el){throw 'Missing live-output follow control.'}
    $pattern=$el.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $state=$pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    if($state -ne $Enabled){$pattern.Toggle();Start-Sleep -Milliseconds 100}
}
function Post-Chat([string]$Id,[string]$Thread,[string]$Prompt){
    $body=@{id=$Id;thread=$Thread;user='alice';text=$Prompt;mention=$false;plain=$false}|ConvertTo-Json -Compress
    $reply=Invoke-RestMethod "$endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Assert ($reply.delivered -eq 1) 'The loopback Mattermost fixture has no connected client.'
}
function Select-ChatRequest([string]$Prompt){
    $search=Element 'InboxSearch'
    if(-not $search -or $search.Current.IsOffscreen){
        $back=Element 'InboxBackToConversations'
        if($back){UI @('invoke','InboxBackToConversations')|Out-Null;Start-Sleep -Milliseconds 150}
    }
    UI @('set-value','InboxSearch',$Prompt)|Out-Null
    $list=Element 'InboxConversations'
    $row=$list.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
    if(-not $row){throw "No isolated conversation row for $Prompt"}
    # Invoke raises ListView.ItemClick as well as selection, which opens the detail
    # pane in the narrow layout used by this test.
    $row.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
    UI @('wait-for','ChatRequests','-t','5000')|Out-Null
    $combo=Element 'ChatRequests';$expand=$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern);$expand.Expand();Start-Sleep -Milliseconds 100
    $all=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
    $choice=$null;foreach($item in $all){if($item.Current.ProcessId -eq $appProcess.Id -and $item.Current.Name.Contains($Prompt)){$choice=$item;break}}
    if(-not $choice){throw "No request option for $Prompt"}
    $choice.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();$expand.Collapse()
}
function Run-Chat([string]$Prompt){
    Select-ChatRequest $Prompt
    UI @('wait-for','ApproveChat','-p','IsEnabled','--value','True','-t','5000')|Out-Null
    UI @('invoke','ApproveChat')|Out-Null
    UI @('wait-for','ChatRunConfirmation','-t','5000')|Out-Null
    UI @('invoke','PrimaryButton')|Out-Null
}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Wait-Contains([string]$Id,[string]$Text,[int]$Seconds=15,[string]$Scope=''){$until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds);do{$el=if($Scope){Scoped-Element $Scope $Id}else{Element $Id};if($el){try{if((Value $Id $Scope).Contains($Text)){return}}catch{}};Start-Sleep -Milliseconds 100}while([DateTimeOffset]::UtcNow -lt $until);throw "Timed out waiting for '$Text' in $Id"}
function Check([string]$Name,[scriptblock]$Body){& $Body;$checks.Add(@{name=$Name;passed=$true});Write-Output "PASS $Name"}
function Resize([int]$Width){
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LiveOutputUiNative { [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags); }
'@ -ErrorAction SilentlyContinue
    $scale=[LiveOutputUiNative]::GetDpiForWindow($appProcess.MainWindowHandle)/96.0
    if(-not [LiveOutputUiNative]::SetWindowPos($appProcess.MainWindowHandle,[IntPtr]::Zero,50,50,[int]($Width*$scale),[int](760*$scale),0x0010)){throw 'Could not resize the app window.'}
    Start-Sleep -Milliseconds 250
}
function Go([string]$Id){if(-not (Element $Id) -or (Element $Id).Current.IsOffscreen){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke',$Id)|Out-Null;Start-Sleep -Milliseconds 180}
function Capture([string]$Name){$out=Join-Path $runRoot $Name;UI @('screenshot','-o',$out)|Out-Null}
function Scroll-LiveOutputIntoView([string]$Scope){
    UI @('scroll-into-view',$Scope)|Out-Null
    Start-Sleep -Milliseconds 100
    UI @('scroll-into-view','LiveOutputText')|Out-Null
    $target=Scoped-Element $Scope 'LiveOutputText'
    if(-not $target){throw "Live output text is not visible in $Scope"}
    $rect=$target.Current.BoundingRectangle;$window=(Root).Current.BoundingRectangle
    Assert (-not $target.Current.IsOffscreen -and $rect.Height -gt 0 -and $rect.Left -ge $window.Left -and $rect.Right -le $window.Right) "Live output is clipped at the selected window width in $Scope"
}
try{
    $node=(Get-Command node -ErrorAction Stop).Source
    $portFile=Join-Path $dataPath 'port.txt'
    $serverScript=Join-Path $PSScriptRoot 'fixtures/file-attachments-chat-server.cjs'
    $server=Start-Process $node -ArgumentList @('"'+$serverScript+'"','"'+$portFile+'"') -WindowStyle Hidden -PassThru
    $portDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
    while(-not (Test-Path $portFile) -and [DateTimeOffset]::UtcNow -lt $portDeadline){Start-Sleep -Milliseconds 100}
    if(-not (Test-Path $portFile)){throw 'Loopback Mattermost fixture did not start.'}
    $endpoint='http://127.0.0.1:'+(Get-Content -LiteralPath $portFile -Raw).Trim()
    Add-Type -AssemblyName System.Security
    $protected='dpapi:'+[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    Write-Json 'settings.json' @{
        SchemaVersion=1;WorkspaceRoot=$workspace;AgentProvider='claude';AgentMode='exec';AgentExecutable=$agent;AgentArguments=''
        AgentPermissionMode='inherit';RequireApproval=$false;TrustedWorkspaceRoots=$workspace
        AutoConnectProvider='none';ChatApprovalMode='always';ChatResultMode='manual'
        MattermostServerUrl=$endpoint;MattermostChannelId='ui-channel';MattermostToken=$protected;MattermostAllowedUserIds='alice'
    }
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme='light'}
    if($Portable){
        $appRoot=[IO.Path]::GetFullPath($AppDirectory)
        if(-not $appRoot.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Portable app payload must be under desktop/artifacts.'}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
        $start.UseShellExecute=$false;$start.WorkingDirectory=[IO.Path]::GetTempPath();$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($dataPath)
        $appProcess=[Diagnostics.Process]::Start($start)
    }else{
        $launch=& winapp run $AppDirectory --detach --json --args ('--data-dir "'+$dataPath+'"') 2>$null
        if($LASTEXITCODE -ne 0){throw 'Packaged app launch failed.'}
        $launch|Set-Content -LiteralPath (Join-Path $runRoot 'launch.json')
        $appProcess=Get-Process -Id (($launch|ConvertFrom-Json).processId)
    }
    UI @('wait-for','NavWorkbench','-t','15000')|Out-Null
    Go 'NavChat'
    UI @('invoke','ConnectMattermost')|Out-Null
    $connectedUntil=[DateTimeOffset]::UtcNow.AddSeconds(12)
    do{$connection=Invoke-RestMethod "$endpoint/test/results";if($connection.connections -eq 1){break};Start-Sleep -Milliseconds 200}while([DateTimeOffset]::UtcNow -lt $connectedUntil)
    if($connection.connections -ne 1){throw 'The offline Mattermost connection did not become ready.'}
    Go 'NavWorkbench'
    UI @('set-value','Prompt','LIVE_RUN_ONE LIVE_FIXTURE offline UI run')|Out-Null
    Resize 500
    UI @('invoke','RunAgent')|Out-Null
    Wait-Contains 'LiveOutputText' 'LIVE_RUN_ONE LIVE_FIXTURE assistant output arrived' 25 'ManualLiveOutput'
    Wait-Contains 'LiveOutputText' 'LIVE_FIXTURE stderr arrived' 25 'ManualLiveOutput'
    UI @('invoke','CancelAgent')|Out-Null
    UI @('wait-for','RunAgent','-p','IsEnabled','--value','True','-t','10000')|Out-Null
    Assert ((Value 'LiveOutputText' 'ManualLiveOutput').Contains('LIVE_RUN_ONE LIVE_FIXTURE assistant output arrived')) 'Cancellation discarded partial live output.'
    Assert (-not (Test-Path -LiteralPath $gate)) 'Cancelled run unexpectedly released the fixture completion gate.'
    $checks.Add(@{name='Live stream appears before completion and cancellation retains the partial tail';passed=$true})

    # A new run has a fresh live buffer. Release it only after both stream types are visible.
    UI @('set-value','Prompt','LIVE_RUN_TWO LIVE_FIXTURE offline UI run two')|Out-Null
    UI @('invoke','RunAgent')|Out-Null
    Wait-Contains 'LiveOutputText' 'LIVE_FIXTURE stderr arrived' 25 'ManualLiveOutput'
    Wait-Contains 'LiveOutputText' 'LIVE_RUN_TWO LIVE_FIXTURE assistant output arrived' 10 'ManualLiveOutput'
    Follow $false
    Start-Sleep -Milliseconds 1500
    $current=Value 'LiveOutputText' 'ManualLiveOutput'
    Assert (-not $current.Contains('LIVE_FIXTURE late output while follow is paused')) 'Paused following still advanced the displayed text.'
    Assert (-not $current.Contains('LIVE_FIXTURE fixed final response')) 'Final response leaked into live progress.'
    Assert (-not $current.Contains('LIVE_RUN_ONE')) 'A prior run marker leaked into the new live buffer.'
    Follow $true
    Wait-Contains 'LiveOutputText' 'LIVE_FIXTURE late output while follow is paused' 10 'ManualLiveOutput'
    Set-Content -LiteralPath $gate 'release'
    Wait-Contains 'AgentOutput' 'LIVE_FIXTURE_FINAL fixed final response' 25
    Wait-Contains 'LiveOutputText' 'LIVE_FIXTURE stderr arrived' 10 'ManualLiveOutput'
    Assert ((Value 'AgentOutput') -match 'LIVE_FIXTURE_FINAL fixed final response') 'The existing final output control did not retain the fixture final result.'
    UI @('invoke','LiveOutputClear')|Out-Null
    Start-Sleep -Milliseconds 150
    $afterClear=Scoped-Element 'ManualLiveOutput' 'LiveOutputText'
    if($afterClear){Assert ((Value 'LiveOutputText' 'ManualLiveOutput') -eq '') 'Clear did not clear only the live display.'}
    Assert ((Value 'AgentOutput') -match 'LIVE_FIXTURE_FINAL fixed final response') 'Clearing live output changed the final result.'
    $checks.Add(@{name='Follow pause/resume, clear display, final output separation, and new-run reset work';passed=$true})

    # Two loopback Mattermost conversations exercise the selected-live-output binding.
    Remove-Item -LiteralPath $gate -Force -ErrorAction SilentlyContinue
    Resize 1100
    Post-Chat 'live-output-chat-a' 'live-output-thread-a' 'LIVE_RUN_CHAT_A isolated conversation A'
    Post-Chat 'live-output-chat-b' 'live-output-thread-b' 'LIVE_RUN_CHAT_B isolated conversation B'
    Go 'NavChat'
    Run-Chat 'LIVE_RUN_CHAT_A isolated conversation A'
    Wait-Contains 'LiveOutputText' 'LIVE_RUN_CHAT_A LIVE_FIXTURE assistant output arrived' 25 'ChatLiveOutput'
    Select-ChatRequest 'LIVE_RUN_CHAT_B isolated conversation B'
    $selectedB=Scoped-Element 'ChatLiveOutput' 'LiveOutputText'
    if($selectedB){try{Assert (-not (Value 'LiveOutputText' 'ChatLiveOutput').Contains('LIVE_RUN_CHAT_A')) 'Conversation B displayed conversation A output.'}catch{if($_.Exception.Message -like '*displayed conversation A*'){throw}}}
    Select-ChatRequest 'LIVE_RUN_CHAT_A isolated conversation A'
    Wait-Contains 'LiveOutputText' 'LIVE_RUN_CHAT_A LIVE_FIXTURE assistant output arrived' 10 'ChatLiveOutput'
    Set-Content -LiteralPath $gate 'release-chat-a'
    Wait-Contains 'ChatOutput' 'LIVE_FIXTURE_FINAL fixed final response' 25
    Remove-Item -LiteralPath $gate -Force
    Run-Chat 'LIVE_RUN_CHAT_B isolated conversation B'
    Wait-Contains 'LiveOutputText' 'LIVE_RUN_CHAT_B LIVE_FIXTURE assistant output arrived' 25 'ChatLiveOutput'
    $chatLive=Value 'LiveOutputText' 'ChatLiveOutput'
    Assert (-not $chatLive.Contains('LIVE_RUN_CHAT_A')) 'Conversation B inherited conversation A live output.'
    Set-Content -LiteralPath $gate 'release-chat-b'
    Wait-Contains 'ChatOutput' 'LIVE_FIXTURE_FINAL fixed final response' 25
    $checks.Add(@{name='Switching between loopback chat conversations isolates their live output';passed=$true})

    foreach($theme in @('light','dark')){
        Go 'NavAppearance'
        $themeId=if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}
        if(Element $themeId){UI @('invoke',$themeId)|Out-Null;Start-Sleep -Milliseconds 250}
        Go 'NavChat';Resize 500
        Wait-Contains 'LiveOutputText' 'LIVE_RUN_CHAT_B LIVE_FIXTURE assistant output arrived' 10 'ChatLiveOutput'
        UI @('scroll-into-view','LiveOutputPrivacyHelp')|Out-Null
        Capture ("live-output-$Language-$theme-500-top.png")
        Scroll-LiveOutputIntoView 'ChatLiveOutput'
        Capture ("live-output-$Language-$theme-500-bottom.png")
        UI @('scroll-into-view','LiveOutputFollow')|Out-Null
        Capture ("live-output-$Language-$theme-500-controls.png")
    }
    $checks.Add(@{name='Live output remains accessible at 500 DIP in light and dark themes';passed=$true})
}catch{
    $checks.Add(@{name='Live output UI';passed=$false;detail=$_.Exception.Message})
    throw
}finally{
    $checks|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $runRoot 'results.json')
    if($appProcess -and -not $appProcess.HasExited){Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue}
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    $env:CAB_LIVE_OUTPUT_FIXTURE=$priorFixture
    $env:CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS=$priorDelay
}
Write-Output "Artifacts: $runRoot"
