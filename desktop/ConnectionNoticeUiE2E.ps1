param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference='Stop'
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an unpackaged portable payload below desktop/artifacts.'}
$exe=Join-Path $payload 'ChatAgentBridge.Desktop.exe'
if(-not (Test-Path $exe)){throw "Portable executable not found: $exe"}
$data=Join-Path $artifactRoot ('connection-notice-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $data|Out-Null
Set-Content (Join-Path $data '.cab-connection-notice-ui') 'Isolated Mattermost notice test'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Security
$app=$null;$server=$null;$endpoint=$null;$checks=[Collections.Generic.List[string]]::new()
[xml]$textResources=Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Localization/DesktopStrings.ja.resx') -Raw
$connectNotice=$textResources.SelectSingleNode("/root/data[@name='InboxConnectedChatNotice']/value").InnerText
$disconnectNotice=$textResources.SelectSingleNode("/root/data[@name='InboxDisconnectChatNotice']/value").InnerText
function Assert([bool]$Ok,[string]$Message){if(-not $Ok){throw $Message}}
function Pass([string]$Name){$checks.Add($Name);Write-Output "PASS $Name"}
function Write-Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 10|Set-Content (Join-Path $data $Name) -Encoding utf8}
function UI([string[]]$Arguments){$out=winapp ui @Arguments -a $app.Id --json 2>&1;if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' '): $($out -join ' ')"};$out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)}
function El([string]$Id){(Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Wait-Fixture([scriptblock]$Condition,[int]$Seconds=10){$end=[DateTime]::UtcNow.AddSeconds($Seconds);do{$s=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3;if(& $Condition $s){return $s};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $end);throw 'Mattermost fixture timed out.'}
function Navigate([string]$Id){$item=El $Id;if($null -eq $item -or $item.Current.IsOffscreen){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke',$Id)|Out-Null;Start-Sleep -Milliseconds 150}
function Save-Notice([bool]$Value){UI @('invoke','SaveConnections')|Out-Null;$end=[DateTime]::UtcNow.AddSeconds(8);do{$saved=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json;if($saved.PostConnectionNotice -eq $Value){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);throw "PostConnectionNotice=$Value did not save."}
function Connect-Mattermost{$null=Wait-Fixture {param($s)$s.connections -eq 0};Navigate 'NavChat';UI @('invoke','ConnectMattermost')|Out-Null;$null=Wait-Fixture {param($s)$s.connections -eq 1} 15;$end=[DateTime]::UtcNow.AddSeconds(8);do{$status=(El 'ChatStatus').Current.Name;if($status.Contains('接続しました。許可ユーザーの依頼を待っています。')){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);throw "Mattermost did not confirm connection: '$status'"}
function Disconnect-Mattermost([int]$Replies){UI @('invoke','DisconnectChat')|Out-Null;$null=Wait-Fixture {param($s)$s.connections -eq 0 -and $s.replies.Count -ge $Replies} 10}
function Start-App{$start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=[IO.Path]::GetTempPath();$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data);$script:app=[Diagnostics.Process]::Start($start);$end=[DateTime]::UtcNow.AddSeconds(20);do{$script:app.Refresh();if($script:app.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);Assert ($script:app.MainWindowHandle -ne [IntPtr]::Zero) 'App window did not appear.';UI @('wait-for','NavConnections','-t','15000')|Out-Null}
function Stop-App{if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force;$null=$app.WaitForExit(5000)};$script:app=$null}
try{
    $portFile=Join-Path $data 'port.txt';$node=(Get-Command node -ErrorAction Stop).Source
    $fixture=Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $nodeStart=[Diagnostics.ProcessStartInfo]::new($node);$nodeStart.UseShellExecute=$false;$nodeStart.CreateNoWindow=$true;$nodeStart.ArgumentList.Add($fixture);$nodeStart.ArgumentList.Add($portFile)
    $server=[Diagnostics.Process]::Start($nodeStart);$end=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $portFile)-and[DateTime]::UtcNow -lt $end){Start-Sleep -Milliseconds 100}
    Assert (Test-Path $portFile) 'Mattermost fixture did not start.';$script:endpoint='http://127.0.0.1:'+(Get-Content $portFile -Raw).Trim()
    $token='dpapi:'+[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    Write-Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$data;AgentProvider='custom';AgentMode='exec';AgentExecutable='unused';AgentArguments='';TrustedWorkspaceRoots=$data;MattermostServerUrl=$endpoint;MattermostChannelId='ui-channel';MattermostToken=$token;MattermostAllowedUserIds='alice';ChatApprovalMode='always';ChatResultMode='manual';AutoConnectProvider='none'}
    Write-Json 'onboarding.json' @{schemaVersion=3;step=4;status='completed'}
    $policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Current consent policy not found.'
    Write-Json 'consent.json' @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    Write-Json 'language.json' @{Language='ja'};Write-Json 'appearance.json' @{Theme='dark'}
    Write-Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
    Write-Json 'agent-connections.json' @{SchemaVersion=1;Profiles=@()};Write-Json 'sessions.json' @{SchemaVersion=1;Bindings=@();Workspaces=@()};Write-Json 'history.json' @{SchemaVersion=1;Records=@()};Write-Json 'audit.json' @{SchemaVersion=1;Records=@()}
    Assert ((Get-Content (Join-Path $data 'settings.json') -Raw) -notmatch 'PostConnectionNotice') 'Fixture must omit the setting to verify its default.'
    Start-App
    Navigate 'NavConnections';UI @('scroll-into-view','PostConnectionNotice')|Out-Null
    $toggle=(El 'PostConnectionNotice').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    Assert ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Missing setting did not default off.'
    Pass 'Legacy settings show the Mattermost notice toggle off'

    Connect-Mattermost;Start-Sleep -Milliseconds 400;$state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert ($state.replies.Count -eq 0) 'Default-off connection posted a notice.'
    Pass 'Connection is confirmed with no connect notice by default'
    Disconnect-Mattermost 0;Start-Sleep -Milliseconds 400;$state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert ($state.replies.Count -eq 0) 'Default-off disconnect posted a notice.'
    Pass 'Default-off setting suppresses both connection and disconnection notices'

    Navigate 'NavConnections';UI @('scroll-into-view','PostConnectionNotice')|Out-Null;$toggle=(El 'PostConnectionNotice').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle();Assert ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) 'Toggle did not turn on.';Save-Notice $true
    Connect-Mattermost;$state=Wait-Fixture {param($s)$s.connections -eq 1 -and $s.replies.Count -ge 1}
    Assert (@($state.replies|Where-Object {$_.message -eq $connectNotice}).Count -eq 1) 'Enabled connection did not post exactly one notice.'
    Pass 'Saved-on setting posts one notice on reconnect'

    Disconnect-Mattermost 2
    $state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert (@($state.replies|Where-Object {$_.message -eq $disconnectNotice}).Count -eq 1) 'Enabled disconnect did not post exactly one notice.'
    Navigate 'NavConnections';UI @('scroll-into-view','PostConnectionNotice')|Out-Null;$toggle=(El 'PostConnectionNotice').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle();Assert ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Toggle did not turn off.';Save-Notice $false
    Connect-Mattermost;Start-Sleep -Milliseconds 400;$state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert ($state.replies.Count -eq 2) 'Saved-off reconnect posted a connect notice.'
    Pass 'Saved-off setting prevents the next connect notice'
    Disconnect-Mattermost 2
    Start-Sleep -Milliseconds 400;$state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert ($state.replies.Count -eq 2) 'Saved-off disconnect posted a notice.'
    Stop-App;Start-App;Navigate 'NavConnections';UI @('scroll-into-view','PostConnectionNotice')|Out-Null
    $toggle=(El 'PostConnectionNotice').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    Assert ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Restart did not restore saved-off setting.'
    Pass 'Restart restores saved-off setting'
    Connect-Mattermost;Disconnect-Mattermost 2
    Start-Sleep -Milliseconds 400;$state=Invoke-RestMethod "$endpoint/test/results" -TimeoutSec 3
    Assert ($state.replies.Count -eq 2) 'Restarted saved-off setting posted a lifecycle notice.'
    Pass 'Restarted saved-off setting suppresses both notices'
} catch { $checks.Add("FAIL: $($_.Exception.Message)");Write-Output "FAIL $($_.Exception.Message)";throw }
finally{if($app){Stop-App};if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force};@{Checks=@($checks);DataDirectory=$data;Executable=$exe}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $data 'results.json') -Encoding utf8;Write-Output "Connection notice UI results: $data"}
