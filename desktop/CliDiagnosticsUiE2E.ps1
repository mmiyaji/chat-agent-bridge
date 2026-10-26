param([Parameter(Mandatory)][string]$AppDirectory,[switch]$WizardTrustOnly)
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
function Value([string]$id){(Element $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Wait-Summary([string]$expected){
 $until=[DateTime]::UtcNow.AddSeconds(30)
 do{if((Summary).Contains($expected)){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until)
 throw "CLI diagnostic did not contain '$expected': $(Summary)"
}
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
 Wait-Summary $expected
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
  Assert (-not (Element 'WizardCliReviewTrust')) 'Trust action was offered for a missing executable'
  $beforeTrust=Get-Content (Join-Path $data 'settings.json') -Raw
  # Reproduce a portable workspace moved behind a junction, without touching user data.
  $real=Join-Path $data 'actual-workspace';New-Item -ItemType Directory -Path $real|Out-Null
  $link=Join-Path $data 'workspace-link';New-Item -ItemType Junction -Path $link -Target $real|Out-Null
  UI @('invoke','OnboardingPrevious')|Out-Null
  Set-Field 'WizardWorkspaceRoot' $link
  Set-Field 'WizardAgentExecutable' (Join-Path $agent 'codex.exe')
  UI @('invoke','OnboardingNext')|Out-Null
  UI @('wait-for','WizardWorkspaceReviewTrust','-t','10000')|Out-Null
  UI @('invoke','WizardWorkspaceReviewTrust')|Out-Null
  UI @('wait-for','CliTrustDialog','-t','5000')|Out-Null
  Assert ((Name 'CliTrustOriginalPath').Contains($link)) 'Junction review omitted original workspace'
  Assert ((Name 'CliTrustTargetPath').Contains($real)) 'Junction review omitted actual workspace'
  UI @('screenshot','-o',(Join-Path $root ($locale+'-wizard-trust-junction.png')))|Out-Null
  UI @('invoke','CloseButton')|Out-Null
  UI @('wait-for','CliTrustDialog','--gone','-t','5000')|Out-Null
  Assert ((Get-Content (Join-Path $data 'settings.json') -Raw) -eq $beforeTrust) 'Cancel persisted wizard settings'
  Assert ((Value 'WizardWorkspaceRoot') -eq $link) 'Cancel changed the linked workspace'
  # Replacing a reviewed physical target with a link while the dialog is open must be rejected.
  UI @('invoke','WizardWorkspaceReviewTrust')|Out-Null
  UI @('wait-for','CliTrustDialog','-t','5000')|Out-Null
  $moved=Join-Path $data 'actual-workspace-moved'
  Rename-Item -LiteralPath $real -NewName 'actual-workspace-moved'
  New-Item -ItemType Junction -Path $real -Target $moved|Out-Null
  UI @('invoke','PrimaryButton')|Out-Null
  UI @('wait-for','WizardStatus','-t','5000')|Out-Null
  $statusText=((Element 'WizardStatus').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) -join ' '
  Assert ($statusText.Contains($(if($locale -eq 'ja'){'フォルダーが変更された'}else{'folder changed'}))) 'A replaced target did not produce an actionable error'
  Assert ((Value 'WizardWorkspaceRoot') -eq $link) 'A replaced target was applied without fresh review'
  Assert ((Get-Content (Join-Path $data 'settings.json') -Raw) -eq $beforeTrust) 'A replaced target was persisted'
  Rename-Item -LiteralPath $real -NewName 'changed-root-link'
  Rename-Item -LiteralPath $moved -NewName 'actual-workspace'
  UI @('invoke','OnboardingNext')|Out-Null
  UI @('wait-for','WizardWorkspaceReviewTrust','-t','5000')|Out-Null
  UI @('invoke','WizardWorkspaceReviewTrust')|Out-Null
  UI @('wait-for','CliTrustDialog','-t','5000')|Out-Null
  UI @('invoke','PrimaryButton')|Out-Null
  UI @('wait-for','CliTrustDialog','--gone','-t','5000')|Out-Null
  UI @('wait-for','WizardWorkspaceRoot','--value',$real,'-t','5000')|Out-Null
  Assert ((Value 'WizardWorkspaceRoot') -eq $real) 'Trust consent did not select the actual workspace'
  Assert ((Get-Content (Join-Path $data 'settings.json') -Raw) -eq $beforeTrust) 'Trust recovery unexpectedly saved all settings'
  if($WizardTrustOnly){
   $results.Add(@{locale=$locale;passed=$true;checks=@('wizard trust review','cancel preserves settings','target replaced during consent rejected','actual workspace recovery with explicit consent')})
   Stop-TestApp
   continue
  }
  UI @('invoke','OnboardingNext')|Out-Null
  UI @('wait-for','CheckCli','-t','10000')|Out-Null
  UI @('invoke','CheckCli')|Out-Null
  UI @('wait-for','CliCheckSummary','--value',$(if($locale -eq 'ja'){'2件のモデル候補'}else{'2 model'}),'--contains','-t','30000')|Out-Null
  UI @('invoke','OnboardingDefer')|Out-Null
  UI @('invoke','NavAgentSettings')|Out-Null
  UI @('wait-for','WorkspaceRoot','-t','5000')|Out-Null
  Assert ((Value 'WorkspaceRoot') -eq $real) 'Junction recovery did not switch to the actual workspace'
  Assert ((Value 'TrustedWorkspaceRoots').Contains($real)) 'Junction target was not explicitly added to trust'
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
  UI @('invoke','SettingsCliReviewTrust')|Out-Null
  UI @('wait-for','CliTrustDialog','-t','5000')|Out-Null
  Assert ((Name 'CliTrustTargetPath').Contains($data)) 'Trust review omitted the target path'
  Assert (-not (Element 'CliTrustOriginalPath')) 'Normal folder was incorrectly described as a link'
  UI @('screenshot','-o',(Join-Path $root ($locale+'-settings-trust.png')))|Out-Null
  UI @('invoke','CloseButton')|Out-Null
  UI @('wait-for','CliTrustDialog','--gone','-t','5000')|Out-Null
  Assert ((Value 'TrustedWorkspaceRoots') -eq $agent) 'Cancel changed trusted roots'
  UI @('invoke','SettingsCliReviewTrust')|Out-Null
  UI @('wait-for','CliTrustDialog','-t','5000')|Out-Null
  UI @('invoke','PrimaryButton')|Out-Null
  Wait-Summary $(if($locale -eq 'ja'){'2件のモデル候補'}else{'2 model'})
  $roots=(Value 'TrustedWorkspaceRoots') -split '[\r\n]+'
  Assert ($roots -contains $agent -and $roots -contains $data) 'Trust recovery lost existing roots or omitted the workspace'
  UI @('invoke','SaveSettings')|Out-Null
  $savedFile=Join-Path $data 'settings.json';$until=[DateTime]::UtcNow.AddSeconds(8)
  do{
   $saved=Get-Content $savedFile -Raw|ConvertFrom-Json
   if(($saved.TrustedWorkspaceRoots -split '[\r\n]+') -contains $data){break}
   Start-Sleep -Milliseconds 100
  }while([DateTime]::UtcNow -lt $until)
  Assert (Test-Path $savedFile) 'Explicit Save did not persist recovered settings'
  $saved=Get-Content $savedFile -Raw|ConvertFrom-Json
  Assert (($saved.TrustedWorkspaceRoots -split '[\r\n]+') -contains $data) 'Saved settings omitted the trusted workspace'
  # A new path invalidates the old error and its action immediately.
  Set-Field 'TrustedWorkspaceRoots' $agent
  Check $(if($locale -eq 'ja'){'信頼するフォルダー'}else{'Trusted Folders'})
  Set-Field 'WorkspaceRoot' $real
  Assert (-not (Element 'SettingsCliReviewTrust')) 'Stale trust action survived a workspace edit'
  Set-Field 'WorkspaceRoot' $data
  Set-Field 'TrustedWorkspaceRoots' ''
  Set-Field 'AgentExecutable' (Join-Path $agent 'not-installed.exe')
  Check 'not-installed.exe'
  Set-Field 'AgentExecutable' (Join-Path $agent 'codex.exe')
  Check $(if($locale -eq 'ja'){'2件のモデル候補'}else{'2 model'})
  Capture 'recovered'
  Stop-TestApp
  Launch $true
  UI @('invoke','NavAgentSettings')|Out-Null
  Set-Field 'WorkspaceRoot' $data
  Assert (((Value 'TrustedWorkspaceRoots') -split '[\r\n]+') -contains $data) 'Trusted workspace did not survive restart'
  UI @('invoke','AgentProvider')|Out-Null
  UI @('invoke','ProviderOptionCodex')|Out-Null
  Set-Field 'WorkspaceRoot' $data
  Set-Field 'AgentExecutable' (Join-Path $agent 'codex.exe')
  Check $(if($locale -eq 'ja'){'モデル候補の取得で失敗'}else{'Model discovery failed'})
  Assert ((Summary).Contains($(if($locale -eq 'ja'){'バージョン確認は成功'}else{'version check succeeded'}))) 'Failed model discovery hid the successful version check'
  Capture 'model-discovery-failure'
  $results.Add(@{locale=$locale;passed=$true;checks=@('Documents default without saving','wizard error banner','wizard junction review and consent','wizard cancel without save','target replaced during consent is rejected','actual workspace recovery','missing workspace','relative path','missing folder with path','trust scope review','cancel preserves roots','explicit trust and automatic recheck','preserves existing roots','trust survives save and restart','stale action invalidation','missing executable','recovery and models','model discovery failure stage')})
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
