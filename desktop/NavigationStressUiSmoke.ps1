# Offline seeded navigation/resize stress with 500 history and inbox rows.
# Uses a new profile for every run; never connects chat or executes a CLI.
# On a stalled UIA call, captures a diagnostic dump only of the owned test app.
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [int]$Seed = 20261026,
    [ValidateRange(20,5000)][int]$Actions = 600,
    [ValidateSet('en','ja')][string]$Language = 'ja',
    [ValidateSet('light','dark')][string]$Theme = 'dark',
    [switch]$IncludeEdits
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Automation;
public static class NavigationStressNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
 public static AutomationElement Find(IntPtr hwnd,string id) {
  var task=Task.Run(()=>AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id)));
  if(!task.Wait(4000))throw new TimeoutException("UIA lookup stalled: "+id);
  return task.GetAwaiter().GetResult();
 }
 public static void SetValue(AutomationElement element,string value) {
  var task=Task.Run(()=>((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value));
  if(!task.Wait(4000))throw new TimeoutException("UIA value change stalled");
  task.GetAwaiter().GetResult();
 }
 public static void Act(AutomationElement element) {
  var task=Task.Run(()=>{
   object p;
   if(element.TryGetCurrentPattern(InvokePattern.Pattern,out p))((InvokePattern)p).Invoke();
   else if(element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out p))((SelectionItemPattern)p).Select();
   else if(element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out p)){
    var e=(ExpandCollapsePattern)p;if(e.Current.ExpandCollapseState==ExpandCollapseState.Expanded)e.Collapse();else e.Expand();
   }else if(element.TryGetCurrentPattern(TogglePattern.Pattern,out p))((TogglePattern)p).Toggle();
   else {
    var child=element.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
    if(child!=null){
     if(child.TryGetCurrentPattern(TogglePattern.Pattern,out p))((TogglePattern)p).Toggle();
     else if(child.TryGetCurrentPattern(InvokePattern.Pattern,out p))((InvokePattern)p).Invoke();
    }
   }
  });
  if(!task.Wait(4000))throw new TimeoutException("UIA action stalled");
  task.GetAwaiter().GetResult();
 }
}
'@ -ReferencedAssemblies @('UIAutomationClient','UIAutomationTypes')
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot=(Resolve-Path -LiteralPath $AppDirectory).Path
if(-not $appRoot.StartsWith($artifactRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated build under desktop/artifacts.'}
$exe=Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Portable desktop executable was not found.'}
$root=Join-Path $artifactRoot ('navigation-stress-'+[guid]::NewGuid().ToString('N'))
$data=Join-Path $root 'profile';New-Item -ItemType Directory -Path $data | Out-Null
function Save-Json($path,$value){[IO.File]::WriteAllText($path,(ConvertTo-Json -InputObject $value -Depth 12),[Text.UTF8Encoding]::new($false))}
Save-Json (Join-Path $data 'settings.json') @{SchemaVersion=1;WorkspaceRoot=$data;TrustedWorkspaceRoots=$data;AgentProvider='codex';AgentMode='exec';AgentExecutable='codex.exe';AgentArguments='';AutoConnectProvider='none'}
Save-Json (Join-Path $data 'onboarding.json') @{schemaVersion=3;step=4;status='completed'}
$policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
Save-Json (Join-Path $data 'consent.json') @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
Save-Json (Join-Path $data 'language.json') @{Language=$Language}
Save-Json (Join-Path $data 'appearance.json') @{Theme=$Theme}
Save-Json (Join-Path $data 'tips.json') @{Seen=@('chat-connect')}
Save-Json (Join-Path $data 'portable-update.json') @{SchemaVersion=1;AutomaticChecksEnabled=$false;IncludePrereleases=$true;LastAttemptUtc=$null;AvailableRelease=$null}
Save-Json (Join-Path $data 'pricing-auto.json') @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
$records=@(foreach($i in 1..500){@{Id=[guid]::NewGuid().ToString();StartedAt=[DateTimeOffset]::UtcNow.AddHours(-$i).ToString('o');FinishedAt=[DateTimeOffset]::UtcNow.AddHours(-$i).AddSeconds(5).ToString('o');Source='local';ChannelId='';ThreadId='';UserId='';WorkspaceRoot=$data;Status='completed';ExitCode=0;AgentModel=@('fixture-fast','fixture-deep')[$i%2]}})
Save-Json (Join-Path $data 'history.json') @{SchemaVersion=1;Records=$records}
$conversations=@(foreach($i in 1..500){@{Key=@{Provider='slack';ChannelId='Cfixture';ThreadId="$i.000001";UserId="U$i";Origin='slack'};WorkspaceRoot=$data;UpdatedAt=[DateTimeOffset]::UtcNow.AddMinutes(-$i).ToString('o');State='completed';RequestCount=2;DisplayName="Fixture conversation $i";UserName="Fixture $i";ChannelName='offline';SiteName='UI stress';IsDirectMessage=$false}})
Save-Json (Join-Path $data 'chat-conversations.json') @{SchemaVersion=1;Entries=$conversations}

$settingsHash=(Get-FileHash (Join-Path $data 'settings.json')).Hash
foreach($folder in @('work-a','work-b')){New-Item -ItemType Directory -Path (Join-Path $data $folder) | Out-Null}
$random=[Random]::new($Seed);$log=[Collections.Generic.List[object]]::new();$app=$null;$failure=$null
$nav=@('NavOverview','NavWorkbench','NavChat','NavHistory','NavAgentSettings','NavConnections','NavAbout')
$agentScopes=@('AgentScopeCommon','AgentScopeCodex','AgentScopeClaude','AgentScopeGemini','AgentScopeCopilot','AgentScopeAntigravity','AgentScopeCustom')
$chatScopes=@('ConnectionScopeCommon','ConnectionScopeMattermost','ConnectionScopeSlack','ConnectionScopeDiscord')
function Find($id){[NavigationStressNative]::Find($script:handle,$id)}
function Act($id){$e=Find $id;if(-not $e -or -not $e.Current.IsEnabled){return $false};[NavigationStressNative]::Act($e);return $true}
function Go($id){$e=Find $id;if($e -and $e.Current.IsOffscreen){[void](Act 'TogglePaneButton');Start-Sleep -Milliseconds 30};[void](Act $id)}
function Scope($navId,$id){
 Go $navId
 $e=Find $id
 if($e -and -not $e.Current.IsOffscreen){[NavigationStressNative]::Act($e);return $true}
 $more=Find 'TopNavOverflowButton'
 if(-not $more -or $more.Current.IsOffscreen){return $false}
 [NavigationStressNative]::Act($more);Start-Sleep -Milliseconds 30
 $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))
 foreach($window in $windows){$e=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id));if($e -and -not $e.Current.IsOffscreen){[NavigationStressNative]::Act($e);return $true}}
 return $false
}
try{
 $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=$appRoot;$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
 $app=[Diagnostics.Process]::Start($start);$until=[DateTime]::UtcNow.AddSeconds(25)
 do{Start-Sleep -Milliseconds 100;$app.Refresh()}while($app.MainWindowHandle -eq 0 -and -not $app.HasExited -and [DateTime]::UtcNow -lt $until)
 if($app.HasExited -or $app.MainWindowHandle -eq 0){throw 'Test app failed to start'}
 $script:handle=$app.MainWindowHandle;$scale=[NavigationStressNative]::GetDpiForWindow($handle)/96.0
 Start-Sleep -Milliseconds 750
 for($index=0;$index -lt $Actions;$index++){
  $app.Refresh();if($app.HasExited){throw "App crashed, exit=$($app.ExitCode)"}
  $timer=[Diagnostics.Stopwatch]::StartNew();$detail='';$kind=$random.Next($(if($IncludeEdits){12}else{10}))
  if($kind -lt 4){$detail=$nav[$random.Next($nav.Count)];Go $detail}
  elseif($kind -lt 7){$detail=$agentScopes[$random.Next($agentScopes.Count)];[void](Scope 'NavAgentSettings' $detail)}
  elseif($kind -eq 7){$detail=$chatScopes[$random.Next($chatScopes.Count)];[void](Scope 'NavConnections' $detail)}
  elseif($kind -eq 8){$width=@(500,800,940,1000,1200,1440)[$random.Next(6)];$detail="resize:$width";[void][NavigationStressNative]::SetWindowPos($handle,[IntPtr]::Zero,30,30,[int]($width*$scale),[int](800*$scale),0x0014)}
  elseif($kind -eq 10){$detail='theme';Go 'NavOverview';[void](Act (@('ThemeSystem','ThemeLight','ThemeDark')[$random.Next(3)]))}
  elseif($kind -eq 11){$detail='workspace-edit';[void](Scope 'NavAgentSettings' 'AgentScopeCommon');$field=Find 'WorkspaceRoot';if($field -and $field.Current.IsEnabled){[NavigationStressNative]::SetValue($field,(Join-Path $data (@('work-a','work-b')[$random.Next(2)])))}}
  else{
   $choices=@(
    @('NavOverview','OverviewDetailsExpander'),@('NavOverview','OverviewArchitectureExpander'),
    @('NavAbout','AboutTechnicalDetails'),@('NavAbout','PortableUpdateSection'),
    @('NavHistory','SavedConversationsTab'),@('NavHistory','ExecutionHistoryTab'),
    @('NavHistory','AuditHistoryTab'),@('NavHistory','ActivityTab'),
    @('NavConnections','ChatApprovalCard'),@('NavConnections','FileAttachmentSettings'))
   $choice=$choices[$random.Next($choices.Count)];Go $choice[0];$id=$choice[1]
   $detail="optional:$id";$e=Find $id;if($e -and -not $e.Current.IsOffscreen -and $e.Current.IsEnabled){[NavigationStressNative]::Act($e)}
  }
  Start-Sleep -Milliseconds ($random.Next(5,45))
  $response=[IntPtr]::Zero
  if([NavigationStressNative]::SendMessageTimeout($handle,0,[IntPtr]::Zero,[IntPtr]::Zero,2,1500,[ref]$response) -eq [IntPtr]::Zero){throw 'Window stopped responding to WM_NULL for 1500ms'}
  $app.Refresh();if($app.HasExited){throw "App crashed after $detail, exit=$($app.ExitCode)"}
  $log.Add(@{index=$index;action=$detail;elapsedMs=$timer.ElapsedMilliseconds;privateMB=[math]::Round($app.PrivateMemorySize64/1MB,1);utc=[DateTimeOffset]::UtcNow.ToString('o')})
  if(($index+1)%50 -eq 0){Save-Json (Join-Path $root 'actions.json') $log.ToArray();Write-Host "Progress: $($index+1)/$Actions; privateMB=$([math]::Round($app.PrivateMemorySize64/1MB,1))"}
 }
 if($IncludeEdits){
  Start-Sleep -Milliseconds 1000
  $saved=Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
  if($saved.AgentProvider -ne 'codex' -or $saved.AgentExecutable -ne 'codex.exe' -or $saved.TrustedWorkspaceRoots -ne $data -or $saved.WorkspaceRoot -notin @($data,(Join-Path $data 'work-a'),(Join-Path $data 'work-b'))){throw 'Settings edits changed unrelated execution context'}
 }elseif((Get-FileHash (Join-Path $data 'settings.json')).Hash -ne $settingsHash){throw 'Navigation unexpectedly changed settings'}
 Write-Host "PASS $Actions seeded navigation actions ($Language/$Theme), process responsive and settings invariants retained"
}catch{
 $failure=$_.Exception.ToString();Write-Warning $failure
 if($app -and -not $app.HasExited){
  try {
   $dumpTool=Get-Command dotnet-dump -ErrorAction SilentlyContinue
   if($dumpTool){
    $dump=Join-Path $root 'hang.dmp';$dumpStart=[Diagnostics.ProcessStartInfo]::new($dumpTool.Source);$dumpStart.UseShellExecute=$false;$dumpStart.Arguments="collect -p $($app.Id) --type Mini -o `"$dump`"";$collector=[Diagnostics.Process]::Start($dumpStart);if(-not $collector.WaitForExit(20000)){$collector.Kill()}
   }else{Write-Warning 'dotnet-dump is unavailable; the action log and original failure are retained.'}
  }catch{Write-Warning "Dump collection failed; original test failure is retained: $($_.Exception.Message)"}
 }
}finally{
 if($app -and -not $app.HasExited){$app.Kill();$app.WaitForExit(5000)|Out-Null}
 Save-Json (Join-Path $root 'actions.json') $log.ToArray()
 Save-Json (Join-Path $root 'results.json') @{seed=$Seed;language=$Language;theme=$Theme;requested=$Actions;completed=$log.Count;includeEdits=[bool]$IncludeEdits;failure=$failure;failedAction=$(if($failure){$detail}else{$null});executable=$exe;executableSha256=(Get-FileHash -LiteralPath $exe).Hash}
 Write-Host "Navigation stress artifacts: $root"
}
if($failure){throw $failure}
