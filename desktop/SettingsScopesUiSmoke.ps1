# Runs only an isolated desktop profile, loopback Mattermost and an offline test CLI.
# Publish the app and ChatAgentBridge.TestAgent before running this smoke test.
param(
 [ValidateSet('en','ja')][string]$Language='en',
 [ValidateSet('light','dark')][string]$Theme='light',
 [string]$AppDirectory=(Join-Path $PSScriptRoot 'artifacts/settings-scope-local'),
 [string]$TestAgentPath=(Join-Path $PSScriptRoot 'artifacts/settings-scope-test-agent/ChatAgentBridge.TestAgent.exe'),
 [ValidateRange(0,200)][int]$StressCycles=0
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class ConnectionSmokeNative {
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr w);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr w,IntPtr a,int x,int y,int width,int height,uint flags);
}
'@
$repo=Split-Path $PSScriptRoot -Parent
$artifactRoot=Join-Path $repo 'desktop/artifacts'
$appRoot=(Resolve-Path $AppDirectory).Path
$agentPath=(Resolve-Path $TestAgentPath).Path
$PSScriptRootForFixture=Join-Path $repo 'desktop'
$source=Get-Content (Join-Path $PSScriptRootForFixture 'ConnectionSettingsUiSmoke.ps1') -Raw
$start=$source.IndexOf('function Assert(')
$main=[regex]::Match($source,'(?m)^try \{\r?\n    Start-IsolatedApp')
$functions=$source.Substring($start,$main.Index-$start)
$functions=$functions.Replace('$PSScriptRoot', '$PSScriptRootForFixture').Replace("ChatApprovalMode = 'auto'", "ChatApprovalMode = 'always'")
$functions=$functions.Replace("@{ Language = 'en' }", '@{ Language = $Language }').Replace("@{ Theme = 'light' }", '@{ Theme = $Theme }')
. ([scriptblock]::Create($functions))
$script:root=Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))
$script:data=Join-Path $script:root 'data'
$script:gate=Join-Path $script:data '.cab-live-steering-release'
$script:observations=Join-Path $script:data 'protocol-observations.jsonl'
$script:app=$null;$script:fixture=$null;$script:window=$null;$script:endpoint=$null
$script:results=[Collections.Generic.List[object]]::new()
$UpdateFixtureDirectory=$null
New-Item -ItemType Directory -Path $script:data -Force|Out-Null
$stringsPath=Join-Path $repo ('desktop/ChatAgentBridge.Desktop/Localization/DesktopStrings'+$(if($Language -eq 'ja'){'.ja'}else{''})+'.resx')
[xml]$strings=Get-Content $stringsPath -Raw
function Text($key){[string]($strings.root.data|Where-Object name -eq $key|Select-Object -First 1).value}
function Capture($name){& winapp ui screenshot -w $script:app.MainWindowHandle.ToInt64() -o (Join-Path $script:root ($name+'.png')) --json|Out-Null;if($LASTEXITCODE){throw 'Capture failed'}}
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class ImpactInput {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
'@
function Point-At($element) {
 Assert (-not $element.Current.IsOffscreen) ('Offscreen: '+$element.Current.AutomationId)
 $r=$element.Current.BoundingRectangle
 $w=$script:window.Current.BoundingRectangle
 Assert ($r.Width -gt 0 -and $r.Left -ge $w.Left -and $r.Right -le $w.Right -and $r.Top -ge $w.Top -and $r.Bottom -le $w.Bottom) ('Clipped: '+$element.Current.AutomationId)
 [void][ImpactInput]::SetForegroundWindow($script:app.MainWindowHandle)
 [void][ImpactInput]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2))
 Start-Sleep -Milliseconds 150
}
function Click-Actual($id) {
 $element=Wait-Element $id
 Assert $element.Current.IsEnabled ('Disabled: '+$id)
 Point-At $element
 [ImpactInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [ImpactInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 200
}
function Resize-Test($width, [int]$height = 0) {
 $scale=[ConnectionSmokeNative]::GetDpiForWindow($script:app.MainWindowHandle)/96.0
 if($height -eq 0){$height=if($width -ge 1000){1024}else{840}}
 [void][ConnectionSmokeNative]::SetWindowPos($script:app.MainWindowHandle,[IntPtr]::Zero,20,20,[int]($width*$scale),[int]($height*$scale),0x0010)
 Start-Sleep -Milliseconds 300
}
function Assert-InboxSelection {
 $item=Wait-Element 'NavChat'
 Assert (($item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected) 'Inbox selection does not match the destination.'
 Assert (-not ((Wait-Element 'NavChatConnectionStatus').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected) 'Shortcut remained selected.'
}
function Open-LatestConversation {
 $list=Wait-Element 'InboxConversations'
 Wait-For {$list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)).Count -gt 0} 'conversation item received'
 $items=$list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 Assert ($items.Count -gt 0) 'No conversation in fixture.'
 Point-At $items[0]
 [ImpactInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[ImpactInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 200
}
$previousModelListEnv=$env:CAB_LIVE_STEERING_MODEL_LIST
$env:CAB_LIVE_STEERING_MODEL_LIST='1'
function Top { $pane=@('AgentSettingsScroll','ConnectionSettingsScroll','MainContentScroll') | ForEach-Object { Find-Element $script:window $_ } | Where-Object { $_ -and -not $_.Current.IsOffscreen } | Select-Object -First 1; if(-not $pane){throw 'No active settings scroll pane found'}; $p=$pane.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern); if($p.Current.VerticallyScrollable){$p.SetScrollPercent(-1,0)};Start-Sleep -Milliseconds 150 }
function Value($id,$value){$e=Wait-Element $id;$pattern=$null;if($e.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)){([Windows.Automation.ValuePattern]$pattern).SetValue($value)}else{$inner=$e.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit));($inner.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($value)}}
function Choose-Scope($id){
 Top
 Wait-For {(Wait-Element $id).Current.IsEnabled} "scope $id enabled"
 Invoke-Element (Wait-Element $id) ('select '+$id)
 Start-Sleep -Milliseconds 200
 Wait-For {((Wait-Element $id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} ('scope selected: '+$id)
}
function Choose-CompactScope($navId,$id){
 Top
 $target=Find-Element $script:window $id
 if($null -eq $target -or $target.Current.IsOffscreen){
   $overflow=Find-Element (Wait-Element $navId) 'TopNavOverflowButton'
   Assert ($null -ne $overflow -and -not $overflow.Current.IsOffscreen) ('Missing overflow for '+$id)
   Invoke-Element $overflow 'open settings tab overflow'
   $target=Wait-ProcessElement $id
 }
 Invoke-Element $target ('select compact '+$id)
 Start-Sleep -Milliseconds 500
 Wait-For {((Wait-Element $id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} ('compact scope selected: '+$id)
}
function Drafts { $p=Join-Path $script:data 'agent-settings-drafts.json';if(Test-Path $p){Get-Content $p -Raw|ConvertFrom-Json}else{$null} }
try {
 Start-IsolatedApp
 Resize-Test 1160 800
 Select-Element 'NavAgentSettings'
 Wait-For {(Wait-Element 'AgentExecutable').Current.IsEnabled} 'default-size agent editor initialized'
 $first=(Wait-Element 'AgentScopeCommon').Current.BoundingRectangle
 $next=(Wait-Element 'AgentScopeCodex').Current.BoundingRectangle
 Assert ([Math]::Abs($first.Left-$next.Left) -lt 2 -and $next.Top -gt $first.Top) 'Agent side list must be visible at the default 1160 x 800 size.'
 Point-At (Wait-Element 'BrowseAgentExecutable')
 Capture "agent-default-sidebar-$Language-$Theme"
 Select-Element 'NavConnections'
 $first=(Wait-Element 'ConnectionScopeCommon').Current.BoundingRectangle
 $next=(Wait-Element 'ConnectionScopeMattermost').Current.BoundingRectangle
 Assert ([Math]::Abs($first.Left-$next.Left) -lt 2 -and $next.Top -gt $first.Top) 'Chat side list must be visible at the default 1160 x 800 size.'
 Capture "chat-default-sidebar-$Language-$Theme"
 Add-Pass 'Both settings pages show side lists at the actual default window size'
 Resize-Test 1440
 $settingsPath=Join-Path $script:data 'settings.json'
 $initialSettingsHash=(Get-FileHash $settingsPath).Hash
 Select-Element 'NavAgentSettings'
 Wait-For {(Wait-Element 'AgentExecutable').Current.IsEnabled} 'agent editor initialized'
 Capture "agent-codex-$Language-$Theme"
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Opening the editor changed active settings.'
 Choose-Scope 'AgentScopeCustom'
 Wait-For {
  ((Wait-Element 'AgentScopeCustom').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected -and
  (Wait-Element 'AgentExecutable').Current.IsEnabled -and
  ((Wait-Element 'AgentExecutable').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq ''
 } 'custom CLI editor selected with empty executable'
 Value 'AgentExecutable' $agentPath
 (Wait-Element 'CheckCli').SetFocus()
 Wait-For {
  $confirmation=Find-Element $script:window 'SensitiveSettingsConfirmation'
  if($confirmation){Invoke-Element (Find-Element $confirmation 'PrimaryButton') 'confirm isolated custom executable change'}
  (Drafts).Drafts | Where-Object { $_.Provider -eq 'custom' -and $_.Executable -eq $agentPath }
 } 'custom CLI draft persisted'
 Choose-Scope 'AgentScopeCodex'
 Choose-Scope 'AgentScopeCustom'
 Assert (((Wait-Element 'AgentExecutable').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $agentPath) 'Custom CLI draft was lost after switching.'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Custom CLI browsing or editing changed active settings.'
 Capture "agent-custom-$Language-$Theme"
 Choose-Scope 'AgentScopeCodex'
 Add-Pass 'Custom CLI opens, persists an isolated draft and restores it without activating the provider'
 Choose-Scope 'AgentScopeClaude'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Selecting Claude changed the active provider.'
 $model=Scroll-ToElement 'AgentModel'
 Value 'AgentModel' 'ui-claude-draft'
 (Wait-Element 'CheckCli').SetFocus()
 Wait-For { (Drafts).Drafts | Where-Object { $_.Provider -eq 'claude' -and $_.Model -eq 'ui-claude-draft' } } 'inactive Claude draft persisted'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Saving an inactive model changed active settings.'
 Capture "agent-claude-draft-$Language-$Theme"
 Choose-Scope 'AgentScopeCodex'
 Choose-Scope 'AgentScopeClaude'
 Assert (((Wait-Element 'AgentModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'ui-claude-draft') 'Per-agent draft did not survive selection changes.'
 Add-Pass 'Selecting/editing providers never activates them; inactive drafts persist independently'
 Value 'AgentExecutable' 'C:\CAB-isolated-invalid-cli.exe'
 (Wait-Element 'CheckCli').SetFocus()
 $confirmation=Wait-Element 'SensitiveSettingsConfirmation'
 Capture "profile-cancel-$Language-$Theme"
 Invoke-Element (Find-Element $confirmation 'CloseButton') 'cancel isolated CLI command edit'
 Start-Sleep -Milliseconds 700
 Wait-For {((Wait-Element 'AgentExecutable').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'claude'} 'cancel restores executable'
 Assert (((Drafts).Drafts|Where-Object Provider -eq 'claude').Executable -eq 'claude') 'Cancel persisted the rejected CLI command.'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Cancel changed the active configuration.'
 Add-Pass 'Cancelling a sensitive profile edit restores the field and preserves both saved draft and active settings'
 Choose-Scope 'AgentScopeCodex'
 Invoke-Element (Wait-Element 'CheckCli') 'check isolated CLI'
 Wait-For {(Wait-Element 'CheckCli').Current.IsEnabled} 'CLI check finishes'
 Capture "agent-checked-$Language-$Theme"
 Assert (Test-Path (Join-Path $script:data 'agent-cli-cache/codex/cli-verification.json')) 'Provider CLI check cache missing.'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'CLI check activated a profile.'
 Value 'AgentModel' 'codex-offline-fixture'
 (Wait-Element 'CheckCli').SetFocus()
 Wait-For {(Drafts).Drafts | Where-Object {$_.Provider -eq 'codex' -and $_.Model -eq 'codex-offline-fixture'}} 'active-provider editor draft saved separately'
 Assert ((Get-FileHash $settingsPath).Hash -eq $initialSettingsHash) 'Editing the active provider implicitly applied settings.'
 Invoke-Element (Wait-Element 'CheckCli') 'recheck isolated CLI with edited model'
 Wait-For {(Wait-Element 'UseAgentProfile').Current.IsEnabled} 'verified edited profile can be applied'
 $use=Scroll-ToElement 'UseAgentProfile'
 Click-Actual 'UseAgentProfile'
 Wait-For {(Get-Content $settingsPath -Raw|ConvertFrom-Json).AgentModel -eq 'codex-offline-fixture'} 'explicit Use applies model'
 Assert ((Results).connections -eq 1 -and (Results).totalConnections -eq 1) 'Applying agent profile disconnected chat.'
 Add-Pass 'Offline CLI/model check, per-provider cache and explicit activation work without disconnecting chat'
 Choose-Scope 'AgentScopeCommon'
 Assert ($null -ne (Wait-Element 'WorkspaceRoot')) 'Common workspace missing.'
 Capture "agent-common-$Language-$Theme"
 Select-Element 'NavConnections'
 foreach($scope in @('ConnectionScopeMattermost','ConnectionScopeSlack','ConnectionScopeCommon')){Choose-Scope $scope;Capture "$scope-$Language-$Theme"}
 Assert ((Results).connections -eq 1 -and (Results).totalConnections -eq 1) 'Browsing connection settings changed the live connection.'
 Assert ((Get-Content $settingsPath -Raw|ConvertFrom-Json).AutoConnectProvider -eq 'mattermost') 'Browsing Slack changed the configured provider.'
 Add-Pass 'Shared, Mattermost and Slack settings switch without changing connection or provider'
 Select-Element 'NavAgentSettings'
 Choose-Scope 'AgentScopeCodex'
 Resize-Test 1240
 Top
 $first=(Wait-Element 'AgentScopeCommon').Current.BoundingRectangle
 $next=(Wait-Element 'AgentScopeCodex').Current.BoundingRectangle
 Assert ([Math]::Abs($first.Left-$next.Left) -lt 2 -and $next.Top -gt $first.Top) 'Agent scopes should remain in a side list at the relaxed width.'
 Capture "agent-relaxed-sidebar-$Language-$Theme"
 Select-Element 'NavConnections'
 $first=(Wait-Element 'ConnectionScopeCommon').Current.BoundingRectangle
 $next=(Wait-Element 'ConnectionScopeMattermost').Current.BoundingRectangle
 Assert ([Math]::Abs($first.Left-$next.Left) -lt 2 -and $next.Top -gt $first.Top) 'Chat scopes should remain in a side list at the relaxed width.'
 Capture "chat-relaxed-sidebar-$Language-$Theme"
 Add-Pass 'Both settings pages retain the side list in a narrower window'
 Select-Element 'NavAgentSettings'
 Resize-Test 1000
 Top
 Capture "agent-medium-$Language-$Theme"
 Assert (-not (Wait-Element 'AgentScopeCodex').Current.IsOffscreen) 'Medium-width tab is offscreen.'
 Resize-Test 500
 Top
 Capture "agent-narrow-$Language-$Theme"
 foreach($id in @('AgentScopeClaude','AgentScopeGemini','AgentScopeCopilot','AgentScopeAntigravity','AgentScopeCustom','AgentScopeCommon','AgentScopeCodex')){
   Choose-CompactScope 'AgentScopeList' $id
   if($id -eq 'AgentScopeCustom'){
    Assert (((Wait-Element 'AgentExecutable').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $agentPath) 'Compact custom tab did not restore its editor.'
    Capture "agent-custom-narrow-$Language-$Theme"
   }
 }
 Assert ((Get-Content $settingsPath -Raw|ConvertFrom-Json).AgentModel -eq 'codex-offline-fixture') 'Compact scope browsing activated an agent.'
 Capture "agent-narrow-selected-$Language-$Theme"
 Assert (-not (Wait-Element 'AgentScopeList').Current.IsOffscreen) 'Narrow scope tabs missing.'
 $e=Scroll-ToElement 'AgentExecutable';Point-At $e
 $e=Scroll-ToElement 'UseAgentProfile';Point-At $e
 Capture "agent-narrow-bottom-$Language-$Theme"
 if($null -eq (Find-Element $script:window 'NavConnections')){Click-Actual 'TogglePaneButton'}
 Click-Actual 'NavConnections'
 Top
 Capture "connection-narrow-$Language-$Theme"
 foreach($id in @('ConnectionScopeMattermost','ConnectionScopeSlack','ConnectionScopeCommon')){
   Choose-CompactScope 'ConnectionScopeList' $id
 }
 Assert ((Results).connections -eq 1 -and (Results).totalConnections -eq 1) 'Compact scope browsing reconnected chat.'
 Assert (-not (Wait-Element 'ConnectionScopeList').Current.IsOffscreen) 'Narrow connection tabs missing.'
 Add-Pass 'Both pages expose top scope tabs and reachable controls at minimum width'
 Resize-Test 1440
 Select-Element 'NavAgentSettings'
 Choose-Scope 'AgentScopeClaude'
 $draftPath=Join-Path $script:data 'agent-settings-drafts.json'
 $draftBackup=Get-Content $draftPath -Raw
 Set-Content $draftPath '{invalid-json' -Encoding utf8
 Value 'AgentModel' 'ui-unsaved-error'
 (Wait-Element 'CheckCli').SetFocus()
 Start-Sleep -Milliseconds 800
 Select-Element 'NavConnections'
 $unsaved=Wait-Element 'UnsavedAgentProfileDialog'
 Capture "profile-save-error-$Language-$Theme"
 Invoke-Element (Find-Element $unsaved 'CloseButton') 'retain unsaved profile'
 Start-Sleep -Milliseconds 700
 Assert (((Wait-Element 'NavAgentSettings').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected) 'Cancel left the agent settings page.'
 Assert (((Wait-Element 'AgentModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'ui-unsaved-error') 'Cancel lost the unpersisted edit.'
 Select-Element 'NavConnections'
 $unsaved=Wait-Element 'UnsavedAgentProfileDialog'
 Set-Content $draftPath $draftBackup -Encoding utf8
 Invoke-Element (Find-Element $unsaved 'SecondaryButton') 'discard isolated unsaved edit'
 Start-Sleep -Milliseconds 700
 Wait-For {((Wait-Element 'NavConnections').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected} 'discard permits navigation'
 Assert (((Drafts).Drafts|Where-Object Provider -eq 'claude').Model -eq 'ui-claude-draft') 'Discard rewrote saved Claude settings.'
 Assert ((Get-Content $settingsPath -Raw|ConvertFrom-Json).AgentModel -eq 'codex-offline-fixture') 'Failed profile save changed active model.'
 Add-Pass 'Failed save can be cancelled without losing edits or discarded without changing active settings'
 Select-Element 'NavAbout'
 $about=(Wait-Element 'AboutScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
 $about.SetScrollPercent(-1,100)
 Click-Actual 'AboutExit'
 $close=Wait-Element 'WindowCloseConfirmation'
 Invoke-Element (Find-Element $close 'PrimaryButton') 'exit isolated app for persistence test'
 Assert ($script:app.WaitForExit(10000)) 'Isolated app did not exit.'
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
 $start.UseShellExecute=$false;$start.WorkingDirectory=[IO.Path]::GetTempPath()
 $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($script:data)
 $start.Environment['CAB_LIVE_STEERING_FIXTURE']='1'
 $start.Environment['CAB_LIVE_STEERING_RELEASE_FILE']=$script:gate
 $start.Environment['CAB_LIVE_STEERING_OBSERVATIONS_FILE']=$script:observations
 $script:app=[Diagnostics.Process]::Start($start)
 Wait-For {$script:app.Refresh();$script:app.MainWindowHandle -ne 0} 'restarted window'
 $script:window=[Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
 Wait-For {(Results).connections -eq 1} 'restarted connection'
 Resize-Test 1440
 Select-Element 'NavAgentSettings'
 Wait-For {(Wait-Element 'AgentEditorStatus').Current.Name -eq $(if($Language -eq 'ja'){'CLI確認済み'}else{'CLI verified'})} 'CLI cache restored after restart'
 Capture "agent-restored-$Language-$Theme"
 Choose-Scope 'AgentScopeCustom'
 Assert (((Wait-Element 'AgentExecutable').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $agentPath) 'Custom CLI draft was lost after restart.'
 Choose-Scope 'AgentScopeClaude'
 Assert (((Wait-Element 'AgentModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'ui-claude-draft') 'Inactive draft was lost after restart.'
 Add-Pass 'Restart restores active model, per-agent drafts, CLI verification and chat connection'
 Choose-Scope 'AgentScopeCodex'
 Choose-Scope 'AgentScopeCommon'
 $savedWorkspace=(Get-Content $settingsPath -Raw|ConvertFrom-Json).WorkspaceRoot
 $childWorkspace=Join-Path $savedWorkspace 'child-workspace'
 New-Item -ItemType Directory -Path $childWorkspace -Force|Out-Null
 Value 'WorkspaceRoot' $childWorkspace
 (Wait-Element 'BrowseWorkspace').SetFocus()
 Wait-For {
   $confirmation=Find-Element $script:window 'SensitiveSettingsConfirmation'
   if($confirmation){Invoke-Element (Find-Element $confirmation 'PrimaryButton') 'confirm isolated shared workspace change'}
   (Get-Content $settingsPath -Raw|ConvertFrom-Json).WorkspaceRoot -eq $childWorkspace
 } 'shared workspace saved'
 Choose-Scope 'AgentScopeCodex'
 Wait-For {(Wait-Element 'AgentEditorStatus').Current.Name -eq $(if($Language -eq 'ja'){'CLI未確認'}else{'CLI not checked'})} 'workspace change invalidates old CLI proof'
 Assert (-not (Wait-Element 'UseAgentProfile').Current.IsEnabled) 'Stale context check can activate a profile.'
 Assert ((Results).connections -eq 1) 'Shared workspace edit disconnected chat.'
 Capture "changed-workspace-$Language-$Theme"
 Add-Pass 'Shared workspace changes invalidate old CLI verification without dropping chat connection'
 if($StressCycles -gt 0){
  $beforeStress=(Get-FileHash $settingsPath).Hash
  $random=[Random]::new(20261023)
  $switches=0
  foreach($cycle in 0..($StressCycles-1)){
   Resize-Test (@(500,1000,1440)[$cycle % 3])
   foreach($page in @('agent','connections')){
    $navId=if($page -eq 'agent'){'NavAgentSettings'}else{'NavConnections'}
    if($null -eq (Find-Element $script:window $navId)){Click-Actual 'TogglePaneButton'}
    Select-Element $navId
    Start-Sleep -Milliseconds 120
    Top
    $scopeNav=if($page -eq 'agent'){'AgentScopeList'}else{'ConnectionScopeList'}
    $choices=if($page -eq 'agent'){@('AgentScopeCodex','AgentScopeClaude','AgentScopeGemini','AgentScopeCopilot','AgentScopeAntigravity','AgentScopeCustom','AgentScopeCommon')}else{@('ConnectionScopeMattermost','ConnectionScopeSlack','ConnectionScopeCommon')}
    $choices=@($choices|Sort-Object {$random.Next()})
    foreach($id in $choices){
     if($script:app.HasExited){throw "App exited during stress cycle $cycle / $id"}
     $target=Find-Element $script:window $id
     if($null -eq $target -or $target.Current.IsOffscreen){
      $more=Find-Element (Wait-Element $scopeNav) 'TopNavOverflowButton'
      Invoke-Element $more 'stress overflow'
      $target=Wait-ProcessElement $id
     }
     Invoke-Element $target ('stress '+$id)
     Start-Sleep -Milliseconds 90
     Wait-For {
      $selectedScope=Find-Element $script:window $id
      $null -ne $selectedScope -and
      $selectedScope.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
     } ('stress scope selected: '+$id)
     $switches++
    }
   }
   if(($cycle+1) % 10 -eq 0){Write-Output "Stress progress: $($cycle+1) cycles / $switches scope switches"}
  }
  Start-Sleep -Milliseconds 750
  Assert (-not $script:app.HasExited) 'App exited after stress.'
  Assert ((Get-FileHash $settingsPath).Hash -eq $beforeStress) 'Stress browsing changed saved active settings.'
  Assert ((Results).connections -eq 1) 'Stress browsing dropped the loopback chat connection.'
  Capture "stress-final-$Language-$Theme"
  Add-Pass "Seed 20261023: $switches random scope switches with page changes and resizing; settings and connection retained"
 }
 Write-Output "Settings scope UI evidence: $script:root"
}
catch { $script:results.Add(@{name='Settings scope UI';status='FAIL';detail=$_.Exception.Message});try{Capture 'failure'}catch{};throw }
finally {Save-Json (Join-Path $script:root 'results.json') $script:results.ToArray();Stop-IsolatedApp;$env:CAB_LIVE_STEERING_MODEL_LIST=$previousModelListEnv}
