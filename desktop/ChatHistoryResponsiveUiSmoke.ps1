# Offline layout checks with sample conversations; no chat connection or CLI execution.
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
$fixtureRoot=$PSScriptRoot
$source=Get-Content (Join-Path $PSScriptRoot 'SettingsSearchPanelUiSmoke.ps1') -Raw
$begin=$source.IndexOf("`$ErrorActionPreference='Stop'")
$body=[regex]::Match($source,'(?m)^try\{\r?\n foreach\(\$language')
if($begin -lt 0 -or -not $body.Success){throw 'Could not load isolated UI fixture helpers.'}
$helpers=$source.Substring($begin,$body.Index-$begin).Replace("'settings-search-panel-'","'chat-history-responsive-'").Replace('$PSScriptRoot','$fixtureRoot')
. ([scriptblock]::Create($helpers))
function Navigate($id){if(-not(Find $id)){Invoke 'TogglePaneButton'};Invoke $id;Start-Sleep -Milliseconds 200}
function Open-FirstRow($id){
 $list=Element $id;$row=$null
 Wait-For {$script:row=$list.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem));$null -ne $script:row} "sample row in $id"
 ($script:row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 [SearchPanelInput]::Activate($script:hwnd);$script:row.SetFocus();[SearchPanelInput]::Key(13);Start-Sleep -Milliseconds 200
}
try{
 foreach($language in @('en','ja')){foreach($theme in @('light','dark')){
  $prefix=$language+'-'+$theme;$data=Join-Path $root $prefix
  New-Item -ItemType Directory -Path $data -Force|Out-Null
  Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$data;TrustedWorkspaceRoots=$data;AgentProvider='custom';AgentExecutable='unused';AgentArguments='';AgentMode='exec';AutoConnectProvider='none'}
  Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
  Json (Join-Path $data 'consent.json') @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Json (Join-Path $data 'language.json') @{Language=$language};Json (Join-Path $data 'appearance.json') @{Theme=$theme}
  Json (Join-Path $data 'background.json') @{CloseBehavior='ExitApp'};Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
  Json (Join-Path $data 'portable-update.json') @{SchemaVersion=1;AutomaticChecksEnabled=$false;IncludePrereleases=$false}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
  $bindings=@(1..3|ForEach-Object {@{Key=@{Provider='slack';ChannelId="layout-channel-$_";ThreadId="layout-thread-$_";UserId='layout-user';Origin='slack'};WorkspaceRoot=$data;AgentExecutable='unused';AgentArguments='';SessionId=[guid]::NewGuid().ToString();UpdatedAt=[DateTimeOffset]::UtcNow.ToString('o');DisplayName="Layout sample $_"}})
  Json (Join-Path $data 'sessions.json') @{SchemaVersion=1;Bindings=$bindings;Workspaces=@()}
  $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=$appRoot
  $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data);[void]$start.Environment.Remove('CAB_WINUI_DATA_DIR')
  $script:app=[Diagnostics.Process]::Start($start);$script:window=$null
  Wait-For {foreach($w in (Windows)){if($w.Current.Name -eq 'Chat Agent Bridge'){$script:window=$w;return $true}};return $false} 'main window'
  $script:hwnd=[IntPtr]$script:window.Current.NativeWindowHandle
  foreach($page in @(@{nav='NavChat';list='InboxConversations';splitter='InboxConversationsSplitter';detail='InboxRestoredConversationDetails';back='InboxBackToConversations'},@{nav='NavHistory';list='SavedSessionsList';splitter='SavedSessionsSplitter';detail='SessionActionsCommandBar';back='BackToSessionsButton'})){
   Resize 1160;Navigate $page.nav;Open-FirstRow $page.list
   foreach($width in @(1160,1040,1008,980,820,760,500,1040)){
    Resize $width
    $isWide=$width -ge 820
    if($isWide){
     $split=Element $page.splitter;$list=Element $page.list;$detail=Element $page.detail
     Fits $list;Fits $detail
     if($list.Current.BoundingRectangle.Right -gt $split.Current.BoundingRectangle.Left+1 -or $detail.Current.BoundingRectangle.Left -lt $split.Current.BoundingRectangle.Right-1){throw 'List and detail overlap.'}
    }
    else{
     Wait-For {$null -eq (Find $page.splitter)} 'single pane on small windows'
     [void](Element $page.back);Fits (Element $page.detail)
     Invoke $page.back;[void](Element $page.list);Open-FirstRow $page.list
    }
    Capture "$prefix-$($page.nav)-$width"
    $results.Add(@{language=$language;theme=$theme;page=$page.nav;width=$width;sideBySide=$isWide;passed=$true})
   }
  }
  Stop-App;Write-Host "PASS $prefix"
 }}
 Json (Join-Path $root 'results.json') @{passed=$true;checks=$results.ToArray()};Write-Host "Evidence: $root"
}catch{Json (Join-Path $root 'results.json') @{passed=$false;checks=$results.ToArray();error=$_.ToString()};try{Capture 'failure'}catch{};throw}
finally{if($script:app -and -not $script:app.HasExited){try{Stop-App}catch{if($script:app.MainModule.FileName -eq $exe){$script:app.Kill($true);[void]$script:app.WaitForExit(7000)}}}}
