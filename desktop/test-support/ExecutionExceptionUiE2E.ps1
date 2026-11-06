param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$TestAgentPath,
    [ValidateSet('ja','en','both')][string]$Language = 'both'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sharedArtifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))
$artifactRoot = Join-Path $sharedArtifacts 'execution-error-e2e'
if ($Language -eq 'both') {
    foreach ($locale in @('en','ja')) { & $PSCommandPath -AppDirectory $AppDirectory -TestAgentPath $TestAgentPath -Language $locale }
    return
}
$runRoot = Join-Path $artifactRoot ("run-$Language-" + [guid]::NewGuid().ToString('N'))
$dataPath = Join-Path $runRoot 'private-data'
$workspace = Join-Path $runRoot 'workspace'
[IO.Directory]::CreateDirectory($dataPath) | Out-Null
[IO.Directory]::CreateDirectory($workspace) | Out-Null
Set-Content -LiteralPath (Join-Path $runRoot '.cab-live-output-fixture') 'Isolated offline live-output UI test'
$gate = Join-Path $runRoot '.cab-live-output-release'
$agent = [IO.Path]::GetFullPath($TestAgentPath)
if (-not (Test-Path -LiteralPath $agent -PathType Leaf) -or [IO.Path]::GetExtension($agent) -ne '.exe') { throw 'Pass the self-contained TestAgent.exe.' }
$version = & $agent --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join ' ') -notmatch 'offline fixture') { throw 'The executable is not the offline TestAgent fixture.' }
$appProcess = $null; $server = $null; $fixturePidFile = Join-Path $runRoot 'fixture-pids.txt'
$prior = @{}; foreach ($name in @('CAB_LIVE_OUTPUT_FIXTURE','CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS','CAB_LIVE_OUTPUT_FIXTURE_PID_FILE')) { $prior[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
$env:CAB_LIVE_OUTPUT_FIXTURE = '1'; $env:CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS = '700'; $env:CAB_LIVE_OUTPUT_FIXTURE_PID_FILE = $fixturePidFile
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function UI([string[]]$Arguments) { $output = & winapp ui @Arguments -a $appProcess.Id --json 2>&1; if ($LASTEXITCODE -ne 0) { throw "UI action failed: $($Arguments -join ' '): $($output -join ' ')" }; return $output }
function Root { [Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle) }
function Element([string]$Id) { (Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)) }
function Scoped([string]$Scope,[string]$Id) { $root = Element $Scope; if (-not $root) { return $null }; return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)) }
function Value([string]$Id,[string]$Scope='') { $el = if ($Scope) { Scoped $Scope $Id } else { Element $Id }; if (-not $el) { throw "Missing UI control $Id" }; try { return [string]$el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { try { return [string]$el.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1) } catch { return [string]$el.Current.Name } } }
function WaitContains([string]$Id,[string]$Text,[int]$Seconds=25,[string]$Scope='') { $until=[DateTimeOffset]::UtcNow.AddSeconds($Seconds); do { $el=if($Scope){Scoped $Scope $Id}else{Element $Id}; if($el){try{if((Value $Id $Scope).Contains($Text)){return}}catch{}}; Start-Sleep -Milliseconds 100 } while([DateTimeOffset]::UtcNow -lt $until); throw "Timed out waiting for '$Text' in $Id" }
function Go([string]$Id) { if (-not (Element $Id) -or (Element $Id).Current.IsOffscreen) { UI @('invoke','TogglePaneButton')|Out-Null }; UI @('invoke',$Id)|Out-Null; Start-Sleep -Milliseconds 200 }
function Post([string]$Id,[string]$Thread,[string]$Prompt) { $body=@{id=$Id;thread=$Thread;user='alice';text=$Prompt;mention=$false;plain=$false}|ConvertTo-Json -Compress; $reply=Invoke-RestMethod "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)); Assert ($reply.delivered -eq 1) 'Loopback transport is not connected.' }
function SelectRequest([string]$Prompt) {
    $search=Element 'InboxSearch'
    if(-not $search -or $search.Current.IsOffscreen){$back=Element 'InboxBackToConversations';if($back){UI @('invoke','InboxBackToConversations')|Out-Null;Start-Sleep -Milliseconds 150}}
    $search=Element 'InboxSearch'; if(-not $search){throw 'InboxSearch AutoSuggestBox is missing.'}
    $edit=$search.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
    if(-not $edit){throw 'InboxSearch did not expose an editable descendant.'}; $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Prompt)
    $list=Element 'InboxConversations'; $row=$null; $until=[DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        if($list){$rows=$list.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem));foreach($candidate in $rows){$names=@($candidate.Current.Name);$children=$candidate.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition);foreach($child in $children){$names+=$child.Current.Name};if(($names -join "`n").Contains($Prompt)){$row=$candidate;break}}}
        if(-not $row){Start-Sleep -Milliseconds 100}
    } while(-not $row -and [DateTimeOffset]::UtcNow -lt $until)
    if(-not $row){throw "No conversation row matched the incoming prompt '$Prompt'."}; $row.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); WaitContains 'InboxSelectedPrompt' $Prompt 10
}
function Approve([string]$Prompt) { SelectRequest $Prompt; UI @('wait-for','ApproveChat','-p','IsEnabled','--value','True','-t','10000')|Out-Null; UI @('invoke','ApproveChat')|Out-Null; UI @('wait-for','ChatRunConfirmation','-t','5000')|Out-Null; UI @('invoke','PrimaryButton')|Out-Null }
function WaitHistory([string]$Thread,[string]$Status) { $until=[DateTimeOffset]::UtcNow.AddSeconds(20); do { if(Test-Path (Join-Path $dataPath 'history.json')){try{$h=Get-Content (Join-Path $dataPath 'history.json') -Raw|ConvertFrom-Json;$r=@($h.records|Where-Object {$_.threadId -eq $Thread -and $_.status -eq $Status}|Select-Object -First 1);if($r.Count){return $r[0]}}catch{}}; Start-Sleep -Milliseconds 100 } while([DateTimeOffset]::UtcNow -lt $until); throw "Timed out for history status $Status" }
function Capture([string]$Name) { UI @('screenshot','-o',(Join-Path $runRoot $Name))|Out-Null }
function Resize([int]$Width) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ExecutionErrorUiNative { [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags); }
'@ -ErrorAction SilentlyContinue
    $scale=[ExecutionErrorUiNative]::GetDpiForWindow($appProcess.MainWindowHandle)/96.0
    if(-not [ExecutionErrorUiNative]::SetWindowPos($appProcess.MainWindowHandle,[IntPtr]::Zero,50,50,[int]($Width*$scale),[int](760*$scale),0x0010)){throw 'Could not resize the app window.'}
    Start-Sleep -Milliseconds 250
}
try {
    $node=(Get-Command node -ErrorAction Stop).Source; $portFile=Join-Path $dataPath 'port.txt'
    $server=Start-Process $node -ArgumentList @('"'+(Join-Path $repoRoot 'desktop\fixtures\file-attachments-chat-server.cjs')+'"','"'+$portFile+'"') -WindowStyle Hidden -PassThru
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10); while(-not(Test-Path $portFile) -and [DateTimeOffset]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}; if(-not(Test-Path $portFile)){throw 'Loopback fixture server did not start.'}
    $script:endpoint='http://127.0.0.1:'+(Get-Content $portFile -Raw).Trim()
    Add-Type -AssemblyName System.Security
    $protected='dpapi:'+ [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $settings=@{SchemaVersion=1;WorkspaceRoot=$workspace;AgentProvider='claude';AgentMode='exec';AgentExecutable=$agent;AgentArguments='';AgentPermissionMode='inherit';RequireApproval=$false;TrustedWorkspaceRoots=$workspace;AutoConnectProvider='none';ChatApprovalMode='always';ChatResultMode='manual';MattermostServerUrl=$script:endpoint;MattermostChannelId='ui-channel';MattermostToken=$protected;MattermostAllowedUserIds='alice'}
    $settings|ConvertTo-Json -Depth 8|Set-Content (Join-Path $dataPath 'settings.json') -Encoding utf8
    @{SchemaVersion=3;Step=4;Status='completed'}|ConvertTo-Json|Set-Content (Join-Path $dataPath 'onboarding.json') -Encoding utf8
    @{Language=$Language}|ConvertTo-Json|Set-Content (Join-Path $dataPath 'language.json') -Encoding utf8
    @{Theme='light'}|ConvertTo-Json|Set-Content (Join-Path $dataPath 'appearance.json') -Encoding utf8
    $appRoot=[IO.Path]::GetFullPath($AppDirectory); $exe=Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
    Assert ($appRoot.StartsWith($sharedArtifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $exe -PathType Leaf)) 'Portable app payload must be an isolated artifact under .artifacts.'
    $start=[Diagnostics.ProcessStartInfo]::new($exe); $start.UseShellExecute=$false; $start.WorkingDirectory=[IO.Path]::GetTempPath(); $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath); $appProcess=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavWorkbench','-t','15000')|Out-Null; Go 'NavChat'; UI @('invoke','ConnectMattermost')|Out-Null
    $connected=$false; $deadline=[DateTimeOffset]::UtcNow.AddSeconds(12); do{$state=Invoke-RestMethod "$script:endpoint/test/results";if($state.connections -eq 1){$connected=$true;break};Start-Sleep -Milliseconds 200}while([DateTimeOffset]::UtcNow -lt $deadline); Assert $connected 'Isolated Mattermost connection did not become ready.'
    Go 'NavChat'; Start-Sleep -Milliseconds 300
    $badPrompt='LIVE_RUN_ERROR_LONG isolated history-write fault scenario'; $badThread='execution-error-long'; Post 'execution-error-request' $badThread $badPrompt; Approve $badPrompt
    WaitContains 'LiveOutputText' 'LIVE_FIXTURE stderr arrived' 25 'ChatLiveOutput'
    $historyPath=Join-Path $dataPath 'history.json'; $running=Get-Content $historyPath -Raw|ConvertFrom-Json; $record=@($running.records|Where-Object {$_.threadId -eq $badThread -and $_.status -eq 'running'}|Select-Object -First 1)[0]; Assert ($null -ne $record) 'Running execution was not persisted before the fault.'
    $historyLock=[IO.File]::Open($historyPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    Set-Content -LiteralPath $gate 'release-long-output'
    WaitContains 'InboxSelectedState' $(if($Language -eq 'ja'){'実行／記録エラー'}else{'Execution or history error'}) 25
    $output=Value 'ChatOutput'; Assert ($output.Contains('Exception')) 'Exception type is missing from the displayed diagnostic.'; Assert ($output -match 'HRESULT 0x[0-9A-Fa-f]{8}' -or $output -match 'HRESULT 0x[0-9A-Fa-f]+') 'HRESULT is missing from the displayed diagnostic.'
    # UIA normalizes line endings from the TextBox, so allow a few characters of length drift while enforcing the 64,000 bound.
    Assert ($output.Contains('LIVE_FIXTURE_FINAL long-output-start|')) 'Long CLI output was not retained after the diagnostic.'; Assert ($output.Length -ge 63900 -and $output.Length -le 64000) "Bounded output exceeded its 64,000-character limit or was unexpectedly short: $($output.Length)"; Assert ($output.IndexOf('Exception',[StringComparison]::Ordinal) -lt $output.IndexOf('LIVE_FIXTURE_FINAL',[StringComparison]::Ordinal)) 'Diagnostic was hidden behind long CLI output.'
    WaitContains 'ChatStatus' $(if($Language -eq 'ja'){'履歴・監査の保存にも失敗しました。'}else{'Could not save history or audit data.'}) 10
    UI @('scroll-into-view','ChatOutput')|Out-Null; Capture "execution-error-$Language-long-output.png"
    $auditFiles=Get-ChildItem $dataPath -Filter 'audit-segment-*.jsonl' -Recurse; $events=@($auditFiles|ForEach-Object {Get-Content $_.FullName|ForEach-Object {try{$_|ConvertFrom-Json}catch{}}}); $event=@($events|Where-Object {$_.kind -eq 'execution_exception'}|Select-Object -First 1)[0]
    Assert ($null -ne $event) 'No persisted execution_exception audit event was found.'; Assert ($event.executionId -eq $record.id) 'Audit execution ID did not match the running request.'
    Assert ($event.executionDiagnostic.Cause -eq 0) 'Injected file-sharing failure did not classify as AccessDenied.'; Assert ($event.executionDiagnostic.exceptionType -eq 'UnauthorizedAccessException') 'Audit exception type did not match the injected write failure.'; Assert ($event.executionDiagnostic.hResult -eq '80070005') 'Audit HRESULT did not match the injected file-sharing failure.'
    $auditText=($auditFiles|ForEach-Object {Get-Content $_.FullName -Raw}) -join "`n"; Assert (-not $auditText.Contains('LIVE_RUN_ERROR_LONG')) 'The request text leaked into audit data.'; Assert (-not $auditText.Contains('offline-ui-fixture')) 'The mock transport credential leaked into audit data.'; Assert (@($event.executionDiagnostic.PSObject.Properties.Name|Sort-Object) -join ',' -eq 'Cause,ExceptionType,HResult') 'Audit diagnostic contained fields beyond the safe allowlist.'
    $replies=(Invoke-RestMethod "$script:endpoint/test/results").replies; Assert (-not (($replies|ForEach-Object {$_.message}) -join "`n"|Select-String 'Exception|HRESULT|long-output-start')) 'Exception diagnostics or CLI output were posted to chat.'
    $checks=[Collections.Generic.List[object]]::new(); $checks.Add(@{name='transport request approved and CLI emitted 65,500-character result before history-write exception';passed=$true}); $checks.Add(@{name='diagnostic type/HRESULT visible ahead of bounded CLI output; terminal UI state and warning shown';passed=$true;displayedCharacters=$output.Length;diagnosticType=$event.executionDiagnostic.exceptionType;hResult=$event.executionDiagnostic.hResult}); $checks.Add(@{name='execution_exception audit persisted safe type/HRESULT and matched execution ID without prompt/credential';passed=$true;executionId=$event.executionId});
    $historyLock.Dispose()
    $recoveryPrompt='LIVE_RUN_RECOVERY isolated recovery request'; $recoveryThread='execution-error-recovery'; Post 'execution-error-recovery-request' $recoveryThread $recoveryPrompt; Approve $recoveryPrompt; WaitContains 'LiveOutputText' 'LIVE_RUN_RECOVERY LIVE_FIXTURE assistant output arrived' 25 'ChatLiveOutput'; Set-Content -LiteralPath $gate 'release-recovery'; WaitContains 'ChatOutput' 'LIVE_FIXTURE_FINAL fixed final response' 25
    $success=WaitHistory $recoveryThread 'completed'; Assert ($success.status -eq 'completed') 'A later request did not complete after the history fault was removed.'; Capture "execution-error-$Language-recovery.png"; $checks.Add(@{name='subsequent transport request completed after isolated storage fault cleanup';passed=$true})
    $checks|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output "PASS $Language execution-error transport E2E; artifacts: $runRoot"
} catch {
    @{passed=$false;detail=$_.Exception.Message}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    if($appProcess -and -not $appProcess.HasExited){try{& winapp ui scroll-into-view ChatOutput -a $appProcess.Id 2>&1|Out-Null;& winapp ui screenshot -a $appProcess.Id -o (Join-Path $runRoot 'failure.png') 2>&1|Out-Null;$tree=& winapp ui inspect -a $appProcess.Id --json 2>&1;$tree|Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8}catch{}}
    throw
} finally {
    if($historyLock){$historyLock.Dispose()}; if($appProcess -and -not $appProcess.HasExited){Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue}; if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    if(Test-Path $fixturePidFile){foreach($line in Get-Content $fixturePidFile){$id=0;if([int]::TryParse($line,[ref]$id)){$p=Get-Process -Id $id -ErrorAction SilentlyContinue;if($p -and $p.ProcessName -eq 'ChatAgentBridge.TestAgent'){Stop-Process -Id $id -Force -ErrorAction SilentlyContinue}}}}
    foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
}
