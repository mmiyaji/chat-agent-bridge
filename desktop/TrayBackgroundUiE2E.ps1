param([Parameter(Mandatory)][string]$AppDirectory,[switch]$OneTimeTray)

$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$exe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
if (-not $appRoot.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path (Join-Path $appRoot 'AppxManifest.xml')) -or -not (Test-Path $exe)) {
    throw 'Use the unpackaged test payload below desktop/artifacts.'
}
if (@(Get-Process ChatAgentBridge.Desktop -ErrorAction SilentlyContinue | Where-Object Path -eq $exe).Count) {
    throw 'The supplied test payload is already running.'
}

$run = Join-Path $artifacts ('tray-hidden-chat-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $run 'data'
$workspace = Join-Path $data 'workspace'
$portFile = Join-Path $run 'fixture-port.txt'
$agentScript = Join-Path $run 'slow-agent.cjs'
$agentLog = Join-Path $run 'agent-processes.jsonl'
$fixture = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
$node = (Get-Command node -ErrorAction Stop).Source
$app = $null; $server = $null; $hwnd = [IntPtr]::Zero; $endpoint = $null
$checks = [Collections.Generic.List[object]]::new()
$previousAgentLog = $env:CAB_TEST_AGENT_LOG
New-Item -ItemType Directory -Path $run,$data,$workspace -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Security
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TrayBackgroundUiNative {
    [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessageW(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
}
'@

function Assert([bool]$Value,[string]$Message) { if(-not $Value){throw $Message} }
function Pass([string]$Name) { $checks.Add(@{name=$Name;passed=$true}); Write-Output "PASS $Name" }
function UI([string[]]$Arguments) {
    if(-not $app -or $app.HasExited){throw 'The isolated app exited unexpectedly.'}
    $out=& winapp ui @Arguments -a $app.Id --json 2>&1
    if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' ') $($out -join ' ')"}
    return $out -join "`n"
}
function Wait-Visible([bool]$Visible,[int]$Seconds=6) {
    $end=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { if([TrayBackgroundUiNative]::IsWindowVisible($hwnd) -eq $Visible){return}; Start-Sleep -Milliseconds 50 } while([DateTime]::UtcNow -lt $end)
    throw "Main window visibility did not become $Visible."
}
function Hide-App {
    Assert ([TrayBackgroundUiNative]::PostMessageW($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)) 'Could not request window close.'
    if($OneTimeTray){
        UI @('wait-for','WindowCloseConfirmation','-t','5000')|Out-Null
        UI @('invoke','SecondaryButton')|Out-Null
    }
    Wait-Visible $false
    if($OneTimeTray){Assert (-not (Get-Content (Join-Path $data 'background.json') -Raw|ConvertFrom-Json).CloseToTray) 'One-time tray choice enabled the persistent setting.'}
}
function Read-Fixture { Invoke-RestMethod "$endpoint/test/results" }
function Wait-Fixture([scriptblock]$Condition,[int]$Seconds=15) {
    $end=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { $snapshot=Read-Fixture; if(& $Condition $snapshot){return $snapshot}; Start-Sleep -Milliseconds 150 } while([DateTime]::UtcNow -lt $end)
    throw 'Loopback chat fixture did not reach the expected state.'
}
function Post([string]$Id,[string]$Thread,[string]$Text) {
    $body=@{id=$Id;thread=$Thread;user='alice';text=$Text;mention=$false;plain=$false}|ConvertTo-Json -Compress
    $response=Invoke-RestMethod "$endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Assert ($response.delivered -eq 1) 'The loopback request was not delivered to the connected app.'
}
function Start-App {
    $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.WorkingDirectory=[IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
    $script:app=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavChat','-t','20000')|Out-Null
    UI @('invoke','NavChat')|Out-Null
    UI @('wait-for','ConnectMattermost','-t','10000')|Out-Null
    $end=[DateTime]::UtcNow.AddSeconds(8)
    do { $app.Refresh();$candidate=[IntPtr]$app.MainWindowHandle;if($candidate -ne [IntPtr]::Zero){$script:hwnd=$candidate;break};Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $end)
    Assert ($hwnd -ne [IntPtr]::Zero) 'Could not find the isolated app window.'
    [uint32]$owner=0;[void][TrayBackgroundUiNative]::GetWindowThreadProcessId($hwnd,[ref]$owner)
    Assert ($owner -eq $app.Id) 'The test HWND is not owned by the isolated app process.'
}
function Restore-FromTray {
    # WM_APP+0x2B1; NIN_SELECT with v4 icon ID 1 in HIWORD(lParam).
    $l=[IntPtr][long]((1 -shl 16) -bor 0x0400)
    Assert ([TrayBackgroundUiNative]::PostMessageW($hwnd,0x82B1,[IntPtr]::Zero,$l)) 'Tray activation callback could not be posted.'
    Wait-Visible $true
}
function Approve([string]$Prompt) {
    UI @('wait-for',$Prompt,'-t','15000')|Out-Null
    UI @('click',$Prompt)|Out-Null
    UI @('wait-for','ApproveChat','-t','10000')|Out-Null
    UI @('invoke','ApproveChat')|Out-Null
    UI @('wait-for','ChatRunConfirmation','-t','10000')|Out-Null
    UI @('invoke','PrimaryButton')|Out-Null
    UI @('wait-for','ChatRunConfirmation','--gone','-t','3000')|Out-Null
}
function Select-MenuItem([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $tree = winapp ui inspect -a $app.Id --interactive --json | ConvertFrom-Json
        foreach ($window in $tree.windows) {
            foreach ($item in $window.elements) {
                if ($item.type -eq 'MenuItem' -and $item.name -eq $Name) {
                    winapp ui invoke $item.selector -w $window.hwnd --json | Out-Null
                    if ($LASTEXITCODE) { throw 'Native menu invocation failed.' }
                    return
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Native tray menu item was not available: $Name"
}
try {
    $server=Start-Process -FilePath $node -ArgumentList @('"'+$fixture+'"','"'+$portFile+'"') -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while(-not (Test-Path $portFile) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}
    Assert (Test-Path $portFile) 'Loopback Mattermost fixture did not start.'
    $endpoint='http://127.0.0.1:'+(Get-Content $portFile -Raw).Trim()
    @'
let input='';
require('node:fs').appendFileSync(process.env.CAB_TEST_AGENT_LOG,JSON.stringify({pid:process.pid,startedAt:new Date().toISOString()})+'\n');
process.stdin.setEncoding('utf8');
process.stdin.on('data',part=>input+=part);
process.stdin.on('end',()=>{
  if(input.includes('tray-slow-cancel')) return setTimeout(()=>process.stdout.write('unexpected-completion'),120000);
  setTimeout(()=>process.stdout.write('tray-hidden-result: '+input),600);
});
'@ | Set-Content -LiteralPath $agentScript -Encoding utf8

    $protected='dpapi:'+[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $policySource=Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw
    $policy=[regex]::Match($policySource,'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Could not determine the current onboarding policy version.'
    @{SchemaVersion=1;WorkspaceRoot=$workspace;AgentExecutable=$node;AgentArguments=('"'+$agentScript+'"');AgentMode='exec';AgentProvider='custom';AutoConnectProvider='mattermost';
      MattermostServerUrl=$endpoint;MattermostChannelId='ui-channel';MattermostToken=$protected;MattermostAllowedUserIds='alice';
      AgentPermissionMode='inherit';TrustedWorkspaceRoots=$workspace;ChatApprovalMode='always';ChatResultMode='auto'} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
    @{schemaVersion=3;step=4;status='completed'}|ConvertTo-Json|Set-Content (Join-Path $data 'onboarding.json') -Encoding utf8
    @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $data 'consent.json') -Encoding utf8
    @{Language='ja'}|ConvertTo-Json|Set-Content (Join-Path $data 'language.json') -Encoding utf8
    @{Theme='dark'}|ConvertTo-Json|Set-Content (Join-Path $data 'appearance.json') -Encoding utf8
    @{CloseToTray=(-not $OneTimeTray)}|ConvertTo-Json|Set-Content (Join-Path $data 'background.json') -Encoding utf8
    $catalog=Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw
    @{schemaVersion=1;autoUpdateEnabled=$false;lastCheckedUtc=[DateTimeOffset]::UtcNow.ToString('o');lastUpdatedUtc=[DateTimeOffset]::UtcNow.ToString('o');etag=$null;catalogJson=$catalog}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $data 'pricing-auto.json') -Encoding utf8
    $env:CAB_TEST_AGENT_LOG=$agentLog

    Start-App
    $null=Wait-Fixture {param($s)$s.connections -eq 1} 20
    Pass 'Auto-connect uses only the isolated localhost Mattermost fixture'
    Hide-App
    $thread='tray-hidden-'+[guid]::NewGuid().ToString('N')
    $first='tray-first-'+[guid]::NewGuid().ToString('N')
    Post ('post-'+$first) $thread $first
    $pending=Wait-Fixture {param($s) @($s.replies|Where-Object {$_.root_id -eq $thread}).Count -ge 1} 15
    Assert (-not (Test-Path $agentLog)) 'A chat request launched the CLI before local approval.'
    Pass 'Hidden app receives a pending request without starting the CLI'

    Restore-FromTray
    Approve $first
    Hide-App
    $firstDone=Wait-Fixture {param($s) @($s.replies|Where-Object {$_.root_id -eq $thread -and $_.message.Contains('tray-hidden-result')}).Count -ge 1} 25
    Assert (Test-Path $agentLog) 'The approved CLI did not start.'
    Pass 'Restored approval runs the CLI and auto-posts its result while hidden again'

    $slow='tray-slow-cancel-'+[guid]::NewGuid().ToString('N')
    $slowThread='tray-slow-thread-'+[guid]::NewGuid().ToString('N')
    Post ('post-'+$slow) $slowThread $slow
    $null=Wait-Fixture {param($s) @($s.replies|Where-Object {$_.root_id -eq $slowThread}).Count -ge 1} 15
    Restore-FromTray
    Approve $slow
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {if(Test-Path $agentLog){$lines=@(Get-Content $agentLog|ForEach-Object {$_|ConvertFrom-Json});if($lines.Count -ge 2){break}};Start-Sleep -Milliseconds 100} while([DateTime]::UtcNow -lt $deadline)
    Assert ($lines.Count -ge 2) 'The second slow CLI process did not start.'
    $childId=[int]$lines[-1].pid
    $child=Get-Process -Id $childId -ErrorAction Stop
    UI @('invoke','NavChat')|Out-Null
    Hide-App
    Assert (-not $child.HasExited -and -not $app.HasExited) 'Hiding the window stopped the active CLI or app.'
    Pass 'Approved custom Node CLI remains alive while the main window is hidden'

    [void][TrayBackgroundUiNative]::PostMessageW($hwnd,0x82B1,[IntPtr]::Zero,[IntPtr][long]((1 -shl 16)-bor 0x007B))
    $resx=Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Localization/DesktopStrings.ja.resx'
    [xml]$resources=Get-Content $resx -Raw
    Select-MenuItem $resources.SelectSingleNode("/root/data[@name='TrayMenuExit']/value").InnerText
    Assert ($app.WaitForExit(12000)) 'Tray Exit did not finish app shutdown.'
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {try{Get-Process -Id $childId -ErrorAction Stop|Out-Null}catch{break};Start-Sleep -Milliseconds 100} while([DateTime]::UtcNow -lt $deadline)
    Assert (@(Get-Process -Id $childId -ErrorAction SilentlyContinue).Count -eq 0) 'Complete exit left the child CLI running.'
    $null=Wait-Fixture {param($s)$s.connections -eq 0} 8
    Start-Sleep -Seconds 2
    Assert ((Read-Fixture).connections -eq 0) 'Chat transport reconnected after complete exit.'
    $savedHistory=Get-Content (Join-Path $data 'history.json') -Raw | ConvertFrom-Json
    Assert (@($savedHistory.Records|Where-Object Status -eq 'cancelled').Count -eq 1) 'Shutdown did not save the cancelled execution to history.'
    Pass 'Tray complete exit cancels the child CLI, closes the socket, and prevents reconnect'
} catch {
    $checks.Add(@{name='Hidden chat tray E2E';passed=$false;detail=$_.Exception.Message})
    throw
} finally {
    if($app -and -not $app.HasExited){try{[void][TrayBackgroundUiNative]::PostMessageW($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)}catch{};if(-not $app.WaitForExit(3000)){Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue}}
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    if (Test-Path $agentLog) {
        foreach ($entry in (Get-Content $agentLog | ForEach-Object { $_ | ConvertFrom-Json })) {
            $ownedChild = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$entry.pid)" -ErrorAction SilentlyContinue
            if ($ownedChild -and $ownedChild.CommandLine -and $ownedChild.CommandLine.Contains($agentScript)) {
                Stop-Process -Id ([int]$entry.pid) -Force -ErrorAction SilentlyContinue
            }
        }
    }
    if ($null -eq $previousAgentLog) { Remove-Item Env:CAB_TEST_AGENT_LOG -ErrorAction SilentlyContinue }
    else { $env:CAB_TEST_AGENT_LOG = $previousAgentLog }
    @{checks=@($checks);dataDirectory=$data;endpoint=$endpoint}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $run 'results.json') -Encoding utf8
    Write-Output "Tray background chat results: $run"
}

if(@($checks|Where-Object{-not $_.passed}).Count){exit 1}
