param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/DiagnosticsUiE2E.ps1" -ArtifactRoot $AppDirectory -OnlyScenario @('none')
$testRoot=Join-Path $artifactsRoot ('migration-startup-ui-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot|Out-Null
$results=[Collections.Generic.List[object]]::new()
$policy=[regex]::Match((Get-Content "$PSScriptRoot/ChatAgentBridge.Core/OnboardingConsentStore.cs" -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
function Wait-Condition([scriptblock]$Check,[string]$Description){
 $until=[DateTime]::UtcNow.AddSeconds(15)
 do{if(&$Check){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until)
 throw "Timed out: $Description"
}
function Invoke-Id([string]$Id){(Element $Id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
try {
 foreach($language in @('ja','en')){
  $data=Join-Path $testRoot ('data-'+$language)
  Write-ScenarioFixture $data $language $(if($language -eq 'ja'){'dark'}else{'light'}) $policy
  $legacy=Join-Path $data 'audit.json'
  Set-Content -LiteralPath $legacy '{invalid legacy audit' -Encoding utf8
  $before=[IO.File]::ReadAllBytes($legacy)
  Start-TestApp $data
  Wait-ElementDirect 'StartupMigrationError' 15
  Wait-Condition {(Element 'StartupMigrationError').Current.IsOffscreen -eq $false} 'migration failure shown'
  if(Element 'NavOverview'){throw 'MainPage was constructed before migration succeeded.'}
  if(-not [Linq.Enumerable]::SequenceEqual[byte]($before,[IO.File]::ReadAllBytes($legacy))){throw 'Failed migration changed legacy source.'}
  UI @('screenshot','-o',(Join-Path $testRoot ('blocked-'+$language+'.png')))|Out-Null
  Record-Pass "$($language): migration failure blocks main pages and preserves source"
  Write-Json $legacy @{SchemaVersion=1;Records=@(@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Kind='completed';ExecutionId=$null;Source='local';ChannelId='';ThreadId=''})}
  Invoke-Id 'StartupMigrationRetry'
  Wait-ElementDirect 'NavOverview' 15
  if(Test-Path -LiteralPath $legacy){throw 'Successful retry did not migrate legacy audit.'}
  $ledger=Get-Content (Join-Path $data 'data-migrations.json') -Raw|ConvertFrom-Json
  if(@($ledger.Completed|Where-Object {$_.Id -eq 'audit-jsonl' -and $_.Version -eq 1}).Count -ne 1){throw 'Migration completion was not committed.'}
  Record-Pass "$($language): retry completes migration and opens main pages"
  Close-TestApp
  Start-TestApp $data
  Wait-ElementDirect 'NavOverview' 15
  Record-Pass "$($language): restart reuses completed schema"
  Close-TestApp
  Write-Json (Join-Path $data 'data-migrations.json') @{SchemaVersion=1;Completed=@(@{Id='audit-jsonl';Version=2;CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')})}
  Start-TestApp $data
  Wait-Condition {$e=Element 'StartupMigrationError';$e -and -not $e.Current.IsOffscreen} 'future schema refusal'
  if(Element 'NavOverview'){throw 'A future migration version was opened by an older registry.'}
  Invoke-Id 'StartupMigrationExit'
  if(-not $activeProcess.WaitForExit(10000)){throw 'Exit during blocked startup did not complete.'}
  if($activeProcess.ExitCode -ne 0){throw "Exit during blocked startup failed: $($activeProcess.ExitCode)"}
  Record-Pass "$($language): newer schema is blocked and Exit shuts down cleanly"
 }
} catch {
 Record-Fail ('migration startup: '+$_.Exception.Message)
 throw
} finally {
 Close-TestApp
 $results|ConvertTo-Json -Depth 5|Set-Content (Join-Path $testRoot 'results.json') -Encoding utf8
 Write-Output $testRoot
}
