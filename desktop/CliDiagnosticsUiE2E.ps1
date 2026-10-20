param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$payload=[IO.Path]::GetFullPath($AppDirectory)
$root=Join-Path $PSScriptRoot ('artifacts/cli-diagnostics-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$agent=Join-Path $root 'agent'
New-Item -ItemType Directory -Path $agent|Out-Null
Copy-Item (Join-Path $PSScriptRoot 'ChatAgentBridge.TestAgent/bin/Release/net10.0/*') $agent
Copy-Item (Join-Path $agent 'ChatAgentBridge.TestAgent.exe') (Join-Path $agent 'codex.exe')
$catalog=Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw
$app=$null;$results=[Collections.Generic.List[object]]::new()
function UI([string[]]$commands){$out=winapp ui @commands -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $commands $out"};$out}
function Element([string]$id){$app.Refresh();[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Name([string]$id){$e=Element $id;if($e){$e.Current.Name}else{''}}
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Summary{(Name 'SettingsCliCheckError')+(Name 'SettingsCliCheckSummary')}
function Capture([string]$name){$errorElement=Element 'SettingsCliCheckError';$target=if($errorElement){'SettingsCliCheckError'}else{'SettingsCliCheckSummary'};UI @('scroll-into-view',$target)|Out-Null;UI @('screenshot','-o',(Join-Path $root ($locale+'-'+$name+'.png')))|Out-Null}
function Set-Field([string]$id,[string]$value){
 $until=[DateTime]::UtcNow.AddSeconds(5)
 do{$e=Element $id;if($e){$e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value);return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until)
 throw "Input field was not available after navigation: $id"
}
function Check([string]$expected){
 UI @('invoke','SettingsCheckCli')|Out-Null
 $until=[DateTime]::UtcNow.AddSeconds(30)
 do{if((Summary).Contains($expected)){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until)
 throw "CLI diagnostic did not contain '$expected': $(Summary)"
}
function Launch([bool]$emptyModels=$false){
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
 $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
 # The offline fixture targets the SDK used to build it. Ignore a stale .NET root
 # inherited from the user's shell without modifying that user's environment.
 $start.Environment['DOTNET_ROOT']=[IO.Path]::GetDirectoryName((Get-Command dotnet).Source)
 $start.Environment['DOTNET_ROOT_X64']=$start.Environment['DOTNET_ROOT']
 if($emptyModels){$start.Environment['CAB_TEST_AGENT_EMPTY_MODEL_CATALOG']='1'}else{$start.Environment.Remove('CAB_TEST_AGENT_EMPTY_MODEL_CATALOG')|Out-Null}
 $script:app=[Diagnostics.Process]::Start($start)
 UI @('wait-for',$(if($emptyModels){'NavAgentSettings'}else{'OnboardingDefer'}),'-t','15000')|Out-Null
}
function Stop-TestApp{if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force;$app.WaitForExit()};$script:app=$null}
try{
 foreach($locale in @('ja','en')){
  $data=Join-Path $root $locale;New-Item -ItemType Directory -Path $data|Out-Null
  @{Language=$locale}|ConvertTo-Json|Set-Content (Join-Path $data 'language.json')
  @{Theme=$(if($locale -eq 'ja'){'dark'}else{'light'})}|ConvertTo-Json|Set-Content (Join-Path $data 'appearance.json')
  @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=$catalog}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $data 'pricing-auto.json')
  @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$true}|ConvertTo-Json|Set-Content (Join-Path $data 'portable-update.json')
  Launch
  UI @('wait-for','OnboardingDefer','-t','10000')|Out-Null
  UI @('invoke','WizardAcceptTerms')|Out-Null
  UI @('invoke','WizardAcceptPrivacy')|Out-Null
  UI @('invoke','OnboardingNext')|Out-Null
  UI @('wait-for','WizardWorkspaceRoot','-t','10000')|Out-Null
  $documents=[Environment]::GetFolderPath('MyDocuments')
  Assert (((Element 'WizardWorkspaceRoot').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value) -eq $documents) 'Wizard did not suggest Documents'
  Assert (-not (Test-Path (Join-Path $data 'settings.json'))) 'Default was persisted without user saving'
  Set-Field 'WizardAgentExecutable' (Join-Path $agent 'not-installed.exe')
  UI @('invoke','OnboardingNext')|Out-Null
  UI @('wait-for','CheckCli','-t','10000')|Out-Null
  UI @('invoke','CheckCli')|Out-Null
  UI @('wait-for','WizardCliCheckError','--value','not-installed.exe','--contains','-t','10000')|Out-Null
  UI @('scroll-into-view','WizardCliCheckError')|Out-Null
  UI @('screenshot','-o',(Join-Path $root ($locale+'-wizard-error.png')))|Out-Null
  UI @('invoke','OnboardingDefer')|Out-Null
  UI @('invoke','NavAgentSettings')|Out-Null
  UI @('wait-for','WorkspaceRoot','-t','5000')|Out-Null
  Assert (((Element 'WorkspaceRoot').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value) -eq $documents) 'First-run Documents default was not displayed'
  $missing=Join-Path $data 'missing-workspace'
  Set-Field 'WorkspaceRoot' ''
  Check $(if($locale -eq 'ja'){'作業フォルダーを選択'}else{'Choose a workspace'})
  Set-Field 'WorkspaceRoot' 'relative-folder'
  Check $(if($locale -eq 'ja'){'絶対パス'}else{'absolute path'})
  Set-Field 'WorkspaceRoot' $missing
  Check $(if($locale -eq 'ja'){'見つかりません'}else{'does not exist'})
  Assert ((Summary).Contains($missing)) 'Missing-folder diagnostic omitted the path'
  Capture 'missing-folder'
  Set-Field 'WorkspaceRoot' $data
  Set-Field 'TrustedWorkspaceRoots' $agent
  Check $(if($locale -eq 'ja'){'信頼するフォルダー'}else{'Trusted Folders'})
  Set-Field 'TrustedWorkspaceRoots' ''
  Set-Field 'AgentExecutable' (Join-Path $agent 'not-installed.exe')
  Check 'not-installed.exe'
  Set-Field 'AgentExecutable' (Join-Path $agent 'codex.exe')
  Check $(if($locale -eq 'ja'){'2件のモデル候補'}else{'2 model'})
  Capture 'recovered'
  Stop-TestApp
  Launch $true
  UI @('invoke','NavAgentSettings')|Out-Null
  UI @('invoke','AgentProvider')|Out-Null
  UI @('invoke','ProviderOptionCodex')|Out-Null
  Set-Field 'WorkspaceRoot' $data
  Set-Field 'AgentExecutable' (Join-Path $agent 'codex.exe')
  Check $(if($locale -eq 'ja'){'モデル候補の取得で失敗'}else{'Model discovery failed'})
  Assert ((Summary).Contains($(if($locale -eq 'ja'){'バージョン確認は成功'}else{'version check succeeded'}))) 'Failed model discovery hid the successful version check'
  Capture 'model-discovery-failure'
  $results.Add(@{locale=$locale;passed=$true;checks=@('Documents default without saving','wizard error banner','missing workspace','relative path','missing folder with path','trust scope','missing executable','recovery and models','model discovery failure stage')})
  Stop-TestApp
 }
}catch{
 if($app -and -not $app.HasExited){
  UI @('screenshot','-o',(Join-Path $root 'failure.png'))|Out-Null
  UI @('inspect','--json')|Set-Content (Join-Path $root 'failure-tree.json')
 }
 throw
}finally{
 Stop-TestApp
 $results|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'results.json')
 Write-Output $root
}
