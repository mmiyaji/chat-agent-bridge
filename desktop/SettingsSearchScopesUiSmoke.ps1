# Exercises deferred page/tab/field search routes using an Antigravity default.
# No CLI, chat connection, token lookup, or AI execution is performed.
param([Parameter(Mandatory)][string]$AppDirectory,
      [ValidateSet('en','ja')][string[]]$Languages=@('en','ja'),
      [ValidateRange(500,1800)][int[]]$Widths=@(1160,500))
$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $PSScriptRoot 'SettingsSearchPanelUiSmoke.ps1') -Raw
$begin=$source.IndexOf("`$ErrorActionPreference='Stop'")
$body=[regex]::Match($source,'(?m)^try\{\r?\n foreach\(\$language')
if($begin -lt 0 -or -not $body.Success){throw 'Could not load search UI fixture helpers.'}
$fixtureRoot=$PSScriptRoot
$helpers=$source.Substring($begin,$body.Index-$begin).Replace("'settings-search-panel-'","'settings-search-scopes-'").Replace('$PSScriptRoot','$fixtureRoot')
. ([scriptblock]::Create($helpers))
function Element($id){
 # Keep the exact UIA element that satisfied the wait. A second popup lookup
 # can momentarily return null while WinUI is arranging the flyout.
 $script:foundElement=$null
 Wait-For {$script:foundElement=Find $id;$null -ne $script:foundElement} "visible $id"
 return $script:foundElement
}
function Start-Profile($data){
 $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=$appRoot
 $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data);[void]$start.Environment.Remove('CAB_WINUI_DATA_DIR')
 $script:app=[Diagnostics.Process]::Start($start);$script:window=$null
 Wait-For {foreach($w in (Windows)){if($w.Current.Name -eq 'Chat Agent Bridge'){$script:window=$w;return $true}};return $false} 'main window'
 $script:hwnd=[IntPtr]$script:window.Current.NativeWindowHandle
 [void](Element 'OpenSettingsSearch')
}
function Focus-Within($id){
 $focused=[Windows.Automation.AutomationElement]::FocusedElement
 for($depth=0;$focused -and $depth -lt 24;$depth++){
  if($focused.Current.ProcessId -ne $script:app.Id){return $false}
  if($focused.Current.AutomationId -eq $id){return $true}
  $focused=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)
 }
 return $false
}
function Search-Route([string]$query,[string]$scopeId,[string]$fieldId=''){
 [SearchPanelInput]::Activate($script:hwnd);[SearchPanelInput]::Key(70,$true)
 $edit=Opened;$edit.SetFocus();[SearchPanelInput]::Text($query)
 Wait-For {($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $query} "typed $query"
 Start-Sleep -Milliseconds 150
 [SearchPanelInput]::Key(13);Closed
 Wait-For {Scope-Selected $scopeId} "selected tab for $query"
 if($fieldId){Wait-For {Focus-Within $fieldId} "focused field for $query";Fits (Element $fieldId)}
 else{
  Wait-For {
   $scroll=Find ($(if($scopeId.StartsWith('AgentScope')){'AgentSettingsScroll'}else{'ConnectionSettingsScroll'}))
   $scroll -and ($scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)).Current.VerticalScrollPercent -le 0.1
  } "tab starts at the top for $query"
 }
}
function Scope-Selected($scopeId){
 $scope=Find $scopeId $false
 if($scope -and ($scope.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected){return $true}
 # Top NavigationView may leave the active tab in its unrealized overflow list.
 # The breadcrumb still exposes the actual selected scope while the menu is closed.
 $label=switch($scopeId){
  'AgentScopeCodex'{'Codex'} 'AgentScopeClaude'{'Claude Code'} 'AgentScopeGemini'{'Gemini CLI'}
  'AgentScopeCopilot'{'GitHub Copilot'} 'AgentScopeAntigravity'{'Antigravity CLI'}
  'AgentScopeCustom'{if($language -eq 'ja'){'その他の CLI'}else{'Other CLI'}}
  'ConnectionScopeSlack'{'Slack'} 'ConnectionScopeDiscord'{'Discord'} 'ConnectionScopeMattermost'{'Mattermost'}
 }
 $id=if($scopeId.StartsWith('AgentScope')){'AgentScopeBreadcrumb'}else{'ConnectionScopeBreadcrumb'}
 # A field jump can scroll the breadcrumb above the viewport; its route name
 # still identifies the selected tab, while Focus-Within checks the visible field.
 $breadcrumb=Find $id $false
 if(-not $breadcrumb -or -not $label){return $false}
 if($breadcrumb.Current.Name.EndsWith(' › '+$label)){return $true}
 $item=$breadcrumb.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$label))
 return $item -and -not $item.Current.IsOffscreen
}
function Assert-Default($data,$hash){
 $settings=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
 if($settings.AgentProvider -ne 'antigravity' -or $settings.AgentExecutable -ne 'unused-agy.exe' -or
    (Get-FileHash (Join-Path $data 'settings.json')).Hash -ne $hash){throw 'Search changed the configured default agent or settings.'}
}
try{
 foreach($language in $Languages){foreach($theme in @('light','dark')){
  $prefix=$language+'-'+$theme;$data=Join-Path $root $prefix;$workspace=Join-Path $data 'workspace'
  New-Item -ItemType Directory -Path $workspace -Force|Out-Null
  Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$workspace;TrustedWorkspaceRoots=$workspace;AgentProvider='antigravity';AgentExecutable='unused-agy.exe';AgentArguments='--output-format stream-json';AgentMode='exec';AgentPermissionMode='workspace-write';AutoConnectProvider='none'}
  Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
  Json (Join-Path $data 'consent.json') @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Json (Join-Path $data 'language.json') @{Language=$language};Json (Join-Path $data 'appearance.json') @{Theme=$theme}
  Json (Join-Path $data 'background.json') @{CloseBehavior='ExitApp'};Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
  Json (Join-Path $data 'portable-update.json') @{SchemaVersion=1;AutomaticChecksEnabled=$false;IncludePrereleases=$false}
  Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
  $hash=(Get-FileHash (Join-Path $data 'settings.json')).Hash;Start-Profile $data
  foreach($width in $Widths){
   Resize $width;$name=$prefix+'-'+$width
   # The first search starts on General, before any agent or chat tab was visited.
   Search-Route 'Codex' 'AgentScopeCodex'
   if($width -eq 1040){
    $common=(Element 'AgentScopeCommon').Current.BoundingRectangle
    $codex=(Element 'AgentScopeCodex').Current.BoundingRectangle
    if([Math]::Abs($common.Left-$codex.Left) -gt 8 -or $codex.Top-$common.Top -lt 24){throw 'Agent settings should keep vertical sidebar navigation at width 1040.'}
   }
   Capture ($name+'-codex-tab')
   Search-Route 'Codex model' 'AgentScopeCodex' 'AgentModel';Capture ($name+'-codex-model')
   Search-Route 'Codex reasoning' 'AgentScopeCodex' 'ReasoningEffortCandidates'
   Search-Route 'agent common' 'AgentScopeCommon'
   Search-Route 'Claude Code' 'AgentScopeClaude'
   Search-Route 'Claude model' 'AgentScopeClaude' 'AgentModel'
   Search-Route 'Gemini' 'AgentScopeGemini'
   Search-Route 'Gemini model' 'AgentScopeGemini' 'AgentModel'
   Search-Route 'GitHub Copilot' 'AgentScopeCopilot'
   Search-Route 'Copilot model' 'AgentScopeCopilot' 'AgentModel'
   Search-Route 'custom' 'AgentScopeCustom'
   Search-Route 'Antigravity' 'AgentScopeAntigravity'
   Search-Route 'Antigravity arguments' 'AgentScopeAntigravity' 'AgentArguments'
   $details=Element 'AgentProfileDetails'
   if(($details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded){throw 'Agent advanced settings did not expand.'}
   Capture ($name+'-advanced-field')
   Search-Route 'Slack' 'ConnectionScopeSlack'
   if($width -eq 1040){
    $common=(Element 'ConnectionScopeCommon').Current.BoundingRectangle
    $slack=(Element 'ConnectionScopeSlack').Current.BoundingRectangle
    if([Math]::Abs($common.Left-$slack.Left) -gt 8 -or $slack.Top-$common.Top -lt 24){throw 'Chat settings should keep vertical sidebar navigation at width 1040.'}
   }
   Search-Route 'Slack token' 'ConnectionScopeSlack' 'SlackBotToken'
   Search-Route 'Discord' 'ConnectionScopeDiscord'
   Search-Route 'Discord user' 'ConnectionScopeDiscord' 'DiscordAllowedUserIds';Capture ($name+'-discord-field')
   Search-Route 'Mattermost' 'ConnectionScopeMattermost'
   Search-Route 'Mattermost server' 'ConnectionScopeMattermost' 'MattermostServerUrl'
   Search-Route 'connections common' 'ConnectionScopeCommon'
   # Returning from a chat tab must still select Codex, rather than the default Antigravity tab.
   Search-Route 'Codex model' 'AgentScopeCodex' 'AgentModel'
   Assert-Default $data $hash
   Resize 1160;Invoke 'NavWorkbench'
   Wait-For {(Element 'WorkbenchAgentSummary').Current.Name -like '*unused-agy.exe*'} 'runtime default agent remains Antigravity'
   Assert-Default $data $hash
   $results.Add(@{case=$name;passed=$true;defaultAgent='antigravity';checks=@('unvisited agent tabs','all six providers','unvisited chat tabs','compound provider+field queries','reasoning field','advanced expander','chat token/user/server fields','default file unchanged','runtime default unchanged')})
   Write-Host "PASS $name"
  }
  Stop-App;Start-Profile $data;Resize 1160;Invoke 'NavAgentSettings'
  Wait-For {$scope=Find 'AgentScopeAntigravity' $false;$scope -and ($scope.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} 'Antigravity default tab after restart'
  Assert-Default $data $hash;Capture ($prefix+'-default-after-restart');Stop-App
  $results.Add(@{case=$prefix+'-restart';passed=$true;defaultAgent='antigravity'})
 }}
 Json (Join-Path $root 'results.json') @{passed=$true;checks=$results.ToArray();generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')};Write-Host "Evidence: $root"
}catch{Json (Join-Path $root 'results.json') @{passed=$false;checks=$results.ToArray();error=$_.ToString()};try{Capture 'failure'}catch{};throw}
finally{if($script:app -and -not $script:app.HasExited){try{Stop-App}catch{if($script:app.MainModule.FileName -eq $exe){$script:app.Kill($true);[void]$script:app.WaitForExit(7000)}}}}
