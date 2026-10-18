param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en')][string]$Language = 'ja'
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$dataPath = Join-Path $artifactRoot ('conversation-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dataPath) | Out-Null
Set-Content (Join-Path $dataPath '.cab-conversation-ui') 'Isolated offline conversation UI test'
$checks = [Collections.Generic.List[object]]::new()
$server = $null; $appProcess = $null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Security
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ConversationUiNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
'@
function Write-Json([string]$Name, $Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8 }
function UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $appProcess.Id --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $($Arguments -join ' ')" }
    return $output
}
function Root { [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle) }
function Element([string]$Id) {
    (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Check([string]$Name,[scriptblock]$Body) {
    & $Body
    $checks.Add(@{name=$Name;passed=$true})
    Write-Output "PASS $Name"
}
function Navigate([string]$Id) {
    $item = Element $Id
    if ($null -eq $item -or $item.Current.IsOffscreen) { UI @('invoke','TogglePaneButton') | Out-Null }
    UI @('invoke',$Id) | Out-Null
    Start-Sleep -Milliseconds 200
}
function Select-FirstRow([string]$Id) {
    $list = Element $Id
    Assert-True ($null -ne $list) "Missing list $Id"
    $row = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    Assert-True ($null -ne $row) "No row in $Id"
    $pattern = $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 180
}
function Value([string]$Id) { (Element $Id).GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty) }
function Has-Text([string]$Text) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Text)
    $null -ne (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Enabled([string]$Id) { $element=Element $Id; $null -ne $element -and $element.Current.IsEnabled }
function Set-Search([string]$Id,[string]$Text) {
    (Element $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 150
}
function Choose([string]$Id,[string]$Name) {
    $combo = Element $Id
    $pattern = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand()
    Start-Sleep -Milliseconds 120
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id))
    # WinUI exposes the same popup item through its owner and PopupHost; deduplicate runtime IDs.
    $matches = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) |
        Where-Object {-not $_.Current.IsOffscreen} | Group-Object { $_.GetRuntimeId() -join ',' } | ForEach-Object {$_.Group[0]})
    Assert-True ($matches.Count -eq 1) "Expected one choice '$Name' for $Id"
    $matches[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $pattern.Collapse()
    Start-Sleep -Milliseconds 150
}
function Assert-Visible([string]$Id) {
    $element=Element $Id
    Assert-True ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) "Control is not visible: $Id"
    $outer=(Root).Current.BoundingRectangle; $rect=$element.Current.BoundingRectangle
    Assert-True ($rect.Left -ge $outer.Left -and $rect.Right -le $outer.Right -and $rect.Bottom -le $outer.Bottom) "Control is clipped: $Id"
}
function Resize([int]$Width,[int]$Height=720) {
    $window = $appProcess.MainWindowHandle
    $scale = [ConversationUiNative]::GetDpiForWindow($window)/96.0
    Assert-True ([ConversationUiNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) 'Resize failed'
    Start-Sleep -Milliseconds 250
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 300 # Let ListView insertion animations settle before visual review.
    UI @('screenshot','--focus','-w',"$($appProcess.MainWindowHandle)",'-o',(Join-Path $dataPath "$Name.png")) | Out-Null
}
function Post([string]$Id,[string]$Thread,[string]$User,[string]$Text) {
    $body = @{id=$Id;thread=$Thread;user=$User;text=$Text} | ConvertTo-Json -Compress
    $result = Invoke-RestMethod "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Assert-True ($result.delivered -eq 1) 'Expected one connected offline transport'
    Start-Sleep -Milliseconds 350
}
try {
    $portFile = Join-Path $dataPath 'port.txt'
    $serverScript = Join-Path $PSScriptRoot 'fixtures/conversation-chat-server.cjs'
    $server = Start-Process (Get-Command node).Source -ArgumentList @(('"'+$serverScript+'"'),('"'+$portFile+'"')) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $portFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True (Test-Path $portFile) 'Offline transport did not start'
    $script:endpoint = 'http://127.0.0.1:' + (Get-Content $portFile -Raw).Trim()
    $agentFile = Join-Path $dataPath 'echo-agent.cjs'
    @'
let input=''; process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => input+=chunk);
process.stdin.on('end', () => setTimeout(() => process.stdout.write('offline-result: '+input), input.includes('slow') ? 12000 : 100));
'@ | Set-Content -LiteralPath $agentFile -Encoding utf8
    $protected = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    Write-Json 'settings.json' @{
        SchemaVersion=1;WorkspaceRoot=$dataPath;AgentProvider='custom';AgentMode='exec';AgentExecutable=(Get-Command node).Source
        AgentArguments=('"'+$agentFile+'"');AgentPermissionMode='inherit';TrustedWorkspaceRoots=$dataPath
        MattermostServerUrl=$script:endpoint;MattermostChannelId='ui-channel';MattermostToken=$protected
        MattermostAllowedUserIds='alice,bob';ChatApprovalMode='always';ChatResultMode='manual';AutoConnectProvider='none'
    }
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme=$(if($Language -eq 'ja'){'dark'}else{'light'})}
    $bindings = @(for ($i=0;$i -lt 42;$i++) {
        $provider = if ($i%2 -eq 0) {'slack'} else {'mattermost'}
        $origin = if ($provider -eq 'slack') {'slack'} else {'a'*64}
        $owner = if ($i -eq 2) {'bob'} else {'alice'}
        $scopeIndex = if ($i -eq 2) {0} else {$i}
        @{Key=@{Provider=$provider;ChannelId='channel-'+$scopeIndex;ThreadId='thread-'+$scopeIndex;UserId=$owner;Origin=$origin}
          WorkspaceRoot=(Join-Path $dataPath ('project-'+$i));AgentExecutable='codex';AgentArguments='exec --json -'
          SessionId=([guid]::NewGuid().ToString());UpdatedAt=([DateTimeOffset]::UtcNow.AddMinutes(-$i).ToString('o'));Model='fixture-model';ReasoningEffort='low'}
    })
    Write-Json 'sessions.json' @{SchemaVersion=1;Bindings=$bindings;Workspaces=@()}
    Write-Json 'history.json' @{SchemaVersion=1;Records=@(@{
        Id=([guid]::NewGuid().ToString());StartedAt=([DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('o'))
        FinishedAt=([DateTimeOffset]::UtcNow.AddMinutes(-4).ToString('o'));Source='slack';ChannelId='channel-0';ThreadId='thread-0';UserId='alice'
        WorkspaceRoot=$dataPath;Status='completed';ExitCode=0;SessionId=$bindings[0].SessionId
    })}
    Write-Json 'audit.json' @{SchemaVersion=1;Records=@()}
    $launch = & winapp run $AppDirectory --detach --json --args ('--data-dir "'+$dataPath+'"') 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Packaged app launch failed' }
    $launch | Set-Content (Join-Path $dataPath 'launch.json')
    $launched = $launch | ConvertFrom-Json
    $appProcess = Get-Process -Id $launched.processId
    UI @('wait-for','NavHistory','-t','15000') | Out-Null
    # Test steps follow below. No real chat service or AI provider is involved.
    Navigate 'NavHistory'
    UI @('wait-for','SavedSessionsList','-t','8000') | Out-Null
    Resize 1280
    Check 'Saved conversation counts, sorting and provider filtering' {
        UI @('wait-for','SavedSessionsCount','--value','42','--contains','-t','3000') | Out-Null
        $first = (Element 'SavedSessionsList').FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
        Assert-True ($first.Current.Name.Contains('project-0')) 'Newest saved conversation is not first'
        Capture 'history-all-conversations'
        Choose 'SavedSessionsProviderFilter' 'Slack'
        UI @('wait-for','SavedSessionsCount','--value','21','--contains','-t','3000') | Out-Null
        Set-Search 'SavedSessionsSearch' 'bob'
        Select-FirstRow 'SavedSessionsList'
        Assert-Visible 'EditSessionModel'
        Choose 'SavedSessionsProviderFilter' $(if($Language -eq 'ja'){'すべてのサービス'}else{'All services'})
        Set-Search 'SavedSessionsSearch' ''
    }
    Check 'Saved conversation search and empty results' {
        Set-Search 'SavedSessionsSearch' 'project-2'
        Select-FirstRow 'SavedSessionsList'
        Capture 'history-search'
        Set-Search 'SavedSessionsSearch' 'no-such-conversation'
        Assert-True (-not (Enabled 'EditSessionModel')) 'Actions enabled without a matching selected conversation'
    }
    Check 'Refresh keeps the exact selected conversation' {
        Set-Search 'SavedSessionsSearch' 'project-0'
        Select-FirstRow 'SavedSessionsList'
        UI @('invoke','RefreshHistory') | Out-Null
        Start-Sleep -Milliseconds 500
        Assert-True (Enabled 'EditSessionModel') 'Refresh lost selected conversation'
        UI @('scroll-into-view','SessionIdentifiers') | Out-Null
        (Element 'SessionIdentifiers').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        UI @('wait-for','SelectedSessionId','--value',$bindings[0].SessionId,'-t','3000') | Out-Null
    }
    Capture 'history-wide'
    Check 'Narrow saved conversation detail and return to list' {
        Resize 500 600
        if (-not (Element 'SavedSessionsList').Current.IsOffscreen) { UI @('click','project-0') | Out-Null }
        Capture 'history-narrow-detail'
        Assert-Visible 'BackToSessionsButton'
        Assert-Visible 'EditSessionModel'
        Assert-Visible 'DetachSession'
        UI @('invoke','BackToSessionsButton') | Out-Null
        Assert-True (-not (Element 'SavedSessionsList').Current.IsOffscreen) 'Narrow list is not accessible after Back'
        Capture 'history-narrow-list'
        Select-FirstRow 'SavedSessionsList'
        # SelectionItem.Select on the same row does not imply an open command; clicking must also work.
        if (-not (Element 'SavedSessionsList').Current.IsOffscreen) { UI @('click','project-0') | Out-Null }
        UI @('wait-for','BackToSessionsButton','-t','3000') | Out-Null
        Resize 1280
    }
    Check 'Start fresh requires confirmation and cancel preserves the binding' {
        UI @('scroll-into-view','DetachSession') | Out-Null
        UI @('invoke','DetachSession') | Out-Null
        UI @('wait-for','DetachSessionConfirmation','-t','3000') | Out-Null
        UI @('invoke','CloseButton') | Out-Null
        $saved = Get-Content (Join-Path $dataPath 'sessions.json') -Raw | ConvertFrom-Json
        Assert-True ($saved.Bindings.Count -eq 42) 'Cancelling detached a session'
    }
    Check 'Start fresh removes only the selected owner binding' {
        UI @('invoke','DetachSession') | Out-Null
        UI @('wait-for','DetachSessionConfirmation','-t','3000') | Out-Null
        UI @('invoke','PrimaryButton') | Out-Null
        UI @('wait-for','SavedSessionsCount','--value','41','--contains','-t','5000') | Out-Null
        $saved = Get-Content (Join-Path $dataPath 'sessions.json') -Raw | ConvertFrom-Json
        Assert-True ($saved.Bindings.Count -eq 41) 'Unexpected binding count after fresh start'
        Assert-True (@($saved.Bindings|Where-Object SessionId -eq $bindings[0].SessionId).Count -eq 0) 'Selected association was not removed'
        Assert-True (@($saved.Bindings|Where-Object SessionId -eq $bindings[2].SessionId).Count -eq 1) 'Another owner in the same thread was affected'
    }
    Navigate 'NavChat'
    Check 'Offline transport connects' {
        UI @('invoke','ConnectMattermost') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            $connected = Invoke-RestMethod "$script:endpoint/test/results"
            if ($connected.connections -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-True ($connected.connections -eq 1) 'No authenticated fixture connection'
    }
    Check 'Requests group by conversation and remain separated by requester' {
        Post 'message-1' 'shared-thread' 'alice' 'alpha initial request'
        Post 'message-2' 'shared-thread' 'alice' 'alpha next request'
        Post 'message-3' 'shared-thread' 'bob' 'beta separate owner'
        Post 'message-4' 'other-thread' 'alice' 'gamma slow request'
        Post 'message-5' 'hidden-thread' 'mallory' 'must not appear'
        UI @('wait-for','InboxConversationCount','--value','3','--contains','-t','5000') | Out-Null
        Set-Search 'InboxSearch' 'alpha'
        Select-FirstRow 'InboxConversations'
        UI @('wait-for','InboxRequestCount','--value','2','--contains','-t','3000') | Out-Null
        Assert-True (Enabled 'ApproveChat') 'Selected pending request cannot be approved'
        Assert-Visible 'ApproveChat'
        Assert-Visible 'SendChatResult'
        Capture 'inbox-wide'
        Post 'message-6' 'shared-thread' 'alice' 'alpha newly arrived request'
        UI @('wait-for','InboxSelectedPrompt','--value','alpha initial request','--contains','-t','3000') | Out-Null
        UI @('wait-for','InboxRequestCount','--value','3','--contains','-t','3000') | Out-Null
        Set-Search 'InboxSearch' ''
        Capture 'inbox-all-conversations'
        Choose 'InboxStateFilter' $(if($Language -eq 'ja'){'承認待ち'}else{'Approval pending'})
        UI @('wait-for','InboxConversationCount','--value','3','--contains','-t','3000') | Out-Null
        Choose 'InboxStateFilter' $(if($Language -eq 'ja'){'すべて'}else{'All'})
        Set-Search 'InboxSearch' 'must not appear'
        Assert-True (-not (Enabled 'ApproveChat')) 'Nonmatching conversation still actionable'
    }
    Check 'Selected result belongs to selected request' {
        Set-Search 'InboxSearch' 'beta'
        Select-FirstRow 'InboxConversations'
        UI @('wait-for','ApproveChat','-p','IsEnabled','--value','True','-t','5000') | Out-Null
        UI @('invoke','ApproveChat') | Out-Null
        UI @('wait-for','ChatRunConfirmation','-t','3000') | Out-Null
        UI @('invoke','PrimaryButton') | Out-Null
        UI @('wait-for','ChatOutput','--value','offline-result: beta separate owner','--contains','-t','8000') | Out-Null
        UI @('wait-for','SendChatResult','-p','IsEnabled','--value','True','-t','5000') | Out-Null
        UI @('invoke','SendChatResult') | Out-Null
        UI @('wait-for','ReplyConfirmation','-t','3000') | Out-Null
        UI @('invoke','CloseButton') | Out-Null
        $replies = (Invoke-RestMethod "$script:endpoint/test/results").replies
        Assert-True (@($replies|Where-Object message -like '*offline-result: beta*').Count -eq 0) 'Cancelling shared a result'
        UI @('invoke','SendChatResult') | Out-Null
        UI @('wait-for','ReplyConfirmation','-t','3000') | Out-Null
        UI @('invoke','PrimaryButton') | Out-Null
        UI @('wait-for','SendChatResult','-p','IsEnabled','--value','True','-t','5000') | Out-Null
        $replies = @((Invoke-RestMethod "$script:endpoint/test/results").replies | Where-Object message -like '*offline-result: beta*')
        Assert-True ($replies.Count -eq 1 -and $replies[0].root_id -eq 'shared-thread' -and $replies[0].channel_id -eq 'ui-channel') 'Result was not shared to the selected thread'
        Set-Search 'InboxSearch' 'alpha'
        Select-FirstRow 'InboxConversations'
        Assert-True (-not ((Value 'ChatOutput') -like '*beta separate owner*')) 'Other request result leaked into selected output'
        Assert-True (-not (Enabled 'SendChatResult')) 'Another request result can be shared from this selection'
    }
    Check 'Stop only affects selected active request' {
        Set-Search 'InboxSearch' 'gamma'
        Select-FirstRow 'InboxConversations'
        UI @('wait-for','ApproveChat','-p','IsEnabled','--value','True','-t','5000') | Out-Null
        UI @('invoke','ApproveChat') | Out-Null
        UI @('wait-for','ChatRunConfirmation','-t','3000') | Out-Null
        UI @('invoke','PrimaryButton') | Out-Null
        UI @('wait-for','CancelChat','-p','IsEnabled','--value','True','-t','5000') | Out-Null
        Choose 'InboxStateFilter' $(if($Language -eq 'ja'){'実行中'}else{'Running'})
        UI @('wait-for','InboxConversationCount','--value','1','--contains','-t','3000') | Out-Null
        Choose 'InboxStateFilter' $(if($Language -eq 'ja'){'すべて'}else{'All'})
        Set-Search 'InboxSearch' 'alpha'
        Select-FirstRow 'InboxConversations'
        Assert-True (-not (Enabled 'CancelChat')) 'Stop targets a different selected conversation'
        Set-Search 'InboxSearch' 'gamma'
        Select-FirstRow 'InboxConversations'
        UI @('invoke','CancelChat') | Out-Null
        UI @('wait-for','CancelChat','-p','IsEnabled','--value','False','-t','5000') | Out-Null
    }
    Check 'Narrow inbox list and detail are reachable' {
        Resize 500 600
        if ((Element 'InboxConversations').Current.BoundingRectangle.Height -le 0) { UI @('invoke','InboxBackToConversations') | Out-Null }
        Capture 'inbox-narrow-list'
        Select-FirstRow 'InboxConversations'
        if (-not (Element 'InboxConversations').Current.IsOffscreen) { UI @('click','gamma slow request') | Out-Null }
        Start-Sleep -Milliseconds 200
        Capture 'inbox-narrow-detail'
        Assert-Visible 'InboxBackToConversations'
        Assert-Visible 'ApproveChat'
        Assert-Visible 'SendChatResult'
        UI @('invoke','InboxBackToConversations') | Out-Null
        Assert-True (-not (Element 'InboxConversations').Current.IsOffscreen) 'Inbox list inaccessible'
    }
    Resize 1280
    UI @('invoke','DisconnectChat') | Out-Null
} catch {
    if ($appProcess -and -not $appProcess.HasExited) {
        try { Capture 'failure' } catch { }
    }
    $checks.Add(@{name='Conversation UI regression';passed=$false;detail=$_.Exception.Message})
    Write-Output "FAIL $($_.Exception.Message)"
} finally {
    Write-Json 'results.json' @{passed=($checks.Count -gt 0 -and @($checks|Where-Object{-not $_.passed}).Count -eq 0);checks=@($checks)}
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id }
    Write-Output "Conversation UI results: $dataPath"
}
if (@($checks|Where-Object{-not $_.passed}).Count -gt 0 -or $checks.Count -eq 0) { exit 1 }
