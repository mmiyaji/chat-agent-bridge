param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$TestAgentPath,
    [ValidateSet('ja','en')][string]$Language = 'ja',
    [switch]$Portable,
    [switch]$KeepRunningOnFailure
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$runRoot = Join-Path $artifactRoot ('file-attachments-ui-' + [guid]::NewGuid().ToString('N'))
$dataPath = Join-Path $runRoot 'private-data'
$workspace = Join-Path $runRoot 'workspace'
[IO.Directory]::CreateDirectory($dataPath) | Out-Null
[IO.Directory]::CreateDirectory($workspace) | Out-Null
Set-Content -LiteralPath (Join-Path $runRoot '.cab-file-attachments-ui') 'Isolated offline attachment UI test'
Set-Content -LiteralPath (Join-Path $workspace 'baseline.txt') 'Present before the agent run.'
$script:checks = [Collections.Generic.List[object]]::new()
$script:server = $null; $script:appProcess = $null
$node = (Get-Command node -ErrorAction Stop).Source
$agentPath = if ($TestAgentPath) { [IO.Path]::GetFullPath($TestAgentPath) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/file-attachment-test-agent/ChatAgentBridge.TestAgent.exe')) }
if (-not (Test-Path $agentPath)) { throw "Publish ChatAgentBridge.TestAgent self-contained for win-x64 or pass -TestAgentPath: $agentPath" }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Security
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class AttachmentUiNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
'@
function Write-Json([string]$Name, $Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8 }
function UI([string[]]$Arguments) {
    if ($Arguments[0] -eq 'wait-for') { Wait-ForElement $Arguments; return '' }
    if ($Arguments[0] -eq 'scroll-into-view') { Scroll-ToElement $Arguments[1]; return '' }
    $output = & winapp ui @Arguments -a $script:appProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed ($LASTEXITCODE): $($Arguments -join ' '): $($output -join ' ')" }
    return $output
}
function Root {
    if ($null -eq $script:appProcess) { return $null }
    $script:appProcess.Refresh()
    $handle = $script:appProcess.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { return $null }
    [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Element([string]$Id) {
    $rootElement = Root
    if ($null -eq $rootElement) { return $null }
    $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Has-Text([string]$Text) {
    foreach ($element in (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.Name.Contains($Text)) { return $true }
    }
    return $false
}
function Automation-TextValues([System.Windows.Automation.AutomationElement]$Item) {
    $values = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrEmpty($Item.Current.Name)) { $values.Add($Item.Current.Name) }
    try {
        $valuePattern = $Item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if (-not [string]::IsNullOrEmpty($valuePattern.Current.Value)) { $values.Add($valuePattern.Current.Value) }
    } catch { }
    try {
        $textPattern = $Item.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $text = $textPattern.DocumentRange.GetText(-1)
        if (-not [string]::IsNullOrEmpty($text)) { $values.Add($text) }
    } catch { }
    return $values
}
function Wait-ForElement([string[]]$Arguments) {
    if ($Arguments.Count -lt 2) { throw 'wait-for requires an AutomationId.' }
    $id = $Arguments[1]
    $property = $null; $expected = $null; $contains = $false; $gone = $false; $timeoutMs = 5000
    for ($index = 2; $index -lt $Arguments.Count; $index++) {
        switch ($Arguments[$index]) {
            '-p' { if (++$index -ge $Arguments.Count) { throw 'wait-for -p requires a property.' }; $property = $Arguments[$index]; break }
            '--value' { if (++$index -ge $Arguments.Count) { throw 'wait-for --value requires a value.' }; $expected = $Arguments[$index]; break }
            '--contains' { $contains = $true; break }
            '--gone' { $gone = $true; break }
            '-t' { if (++$index -ge $Arguments.Count) { throw 'wait-for -t requires a timeout in milliseconds.' }; $timeoutMs = [int]$Arguments[$index]; break }
            default { throw "Unsupported wait-for option: $($Arguments[$index])" }
        }
    }
    if ($timeoutMs -lt 0) { throw 'wait-for timeout cannot be negative.' }

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    do {
        $rootElement = Root
        $item = if ($null -eq $rootElement) { $null } else { $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) }
        if ($gone) {
            if ($null -eq $item) { return }
        } elseif ($null -ne $item) {
            if ($null -eq $expected) { return }
            if ($property -eq 'IsEnabled') {
                $actual = [string]$item.Current.IsEnabled
                $matched = if ($contains) { $actual.Contains($expected, [StringComparison]::OrdinalIgnoreCase) } else { $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase) }
                if ($matched) { return }
            } elseif ($null -eq $property -or $property -in @('Name', 'Value', 'Text')) {
                $values = if ($property -eq 'Name') { @($item.Current.Name) } else { @(Automation-TextValues $item) }
                foreach ($actual in $values) {
                    if ($null -eq $actual) { continue }
                    $matched = if ($contains) { $actual.Contains($expected, [StringComparison]::OrdinalIgnoreCase) } else { $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase) }
                    if ($matched) { return }
                }
            } else { throw "Unsupported wait-for property: $property" }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $detail = if ($gone) { 'to disappear' } elseif ($null -ne $property) { "to have $property '$expected'" } else { "to match '$expected'" }
    throw "Timed out waiting for $id $detail."
}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Check([string]$Name,[scriptblock]$Body) {
    & $Body
    $script:checks.Add(@{name=$Name;passed=$true})
    Write-Output "PASS $Name"
}
function Enabled([string]$Id) { $element=Element $Id; $null -ne $element -and $element.Current.IsEnabled }
function Wait-Element([string]$Id,[int]$Seconds=8) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { $item=Element $Id; if ($null -ne $item) { return $item }; Start-Sleep -Milliseconds 150 }
    while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Id"
}
function Wait-ElementInView([string]$Id,[int]$Seconds=8) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $item=Element $Id
        if ($item -and -not $item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Height -gt 0) { return $item }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Id to come onscreen"
}
function Navigate([string]$Id) {
    $item=Element $Id
    if ($null -eq $item -or $item.Current.IsOffscreen) { UI @('invoke','TogglePaneButton') | Out-Null }
    UI @('invoke',$Id) | Out-Null
    Start-Sleep -Milliseconds 250
}
function Resize([int]$Width,[int]$Height=760) {
    $hwnd=$script:appProcess.MainWindowHandle
    $scale=[AttachmentUiNative]::GetDpiForWindow($hwnd)/96.0
    Assert-True ([AttachmentUiNative]::SetWindowPos($hwnd,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) 'Window resize failed'
    Start-Sleep -Milliseconds 250
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 250
    $script:appProcess.Refresh()
    $windowHandle = $script:appProcess.MainWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) { return }
    UI @('screenshot','--focus','-w',"$windowHandle",'-o',(Join-Path $dataPath $Name)) | Out-Null
}
function Scroll-ToElement([string]$Id) {
    $viewport=Element 'MainContentScroll'
    Assert-True ($null -ne $viewport) 'Main content scroll viewer is missing'
    $pattern=$viewport.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($pattern.Current.VerticallyScrollable) { $pattern.SetScrollPercent(-1,0) }
    for ($attempt=0;$attempt -lt 45;$attempt++) {
        $target=Element $Id
        if ($target -and -not $target.Current.IsOffscreen -and $target.Current.BoundingRectangle.Height -gt 0) { return }
        if (-not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 100) { break }
        $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,[System.Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 80
    }
    throw "Could not scroll to $Id"
}
function Choose([string]$Id,[string]$Name) {
    $combo=Element $Id
    Assert-True ($null -ne $combo) "Missing ComboBox $Id"
    $expand=$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand(); Start-Sleep -Milliseconds 150
    $condition=[System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:appProcess.Id))
    $matches=@([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) |
        Where-Object {-not $_.Current.IsOffscreen} | Group-Object { $_.GetRuntimeId() -join ',' } | ForEach-Object {$_.Group[0]})
    Assert-True ($matches.Count -eq 1) "Expected one '$Name' choice for $Id"
    $matches[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $expand.Collapse(); Start-Sleep -Milliseconds 150
}
function Select-FirstRow([string]$Id) {
    $list=Element $Id
    Assert-True ($null -ne $list) "Missing list $Id"
    $row=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    Assert-True ($null -ne $row) "No row in $Id"
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Select-Request([string]$Text) {
    $combo=Element 'ChatRequests'
    Assert-True ($null -ne $combo) 'Request picker is missing'
    $expand=$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand(); Start-Sleep -Milliseconds 150
    $items=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    $match=$null
    foreach ($candidate in $items) { if ($candidate.Current.ProcessId -eq $script:appProcess.Id -and $candidate.Current.Name.Contains($Text)) { $match=$candidate; break } }
    Assert-True ($null -ne $match) "Could not find request '$Text'"
    $match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $expand.Collapse(); Start-Sleep -Milliseconds 180
}
function Set-Value([string]$Id,[string]$Text) {
    UI @('set-value',$Id,$Text) | Out-Null
    Start-Sleep -Milliseconds 180
}
function Confirm-SensitiveSettingsAutoSave([string]$Property,[object]$ExpectedValue,[switch]$CommitTextInput) {
    # NumberBox edits save on focus loss; selection controls save through the normal debounce.
    if ($CommitTextInput) { Navigate 'NavChat' }
    UI @('wait-for','SensitiveSettingsConfirmation','-t','10000') | Out-Null
    UI @('invoke','PrimaryButton') | Out-Null
    UI @('wait-for','SensitiveSettingsConfirmation','--gone','-t','5000') | Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {
        $saved=Get-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.$Property -eq $ExpectedValue) {
            if (-not $CommitTextInput) { Navigate 'NavChat' }
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Sensitive setting $Property did not auto-save to '$ExpectedValue'."
}
function Post([string]$Id,[string]$Prompt) {
    $body=@{id=$Id;thread='attachments-thread';user='alice';text=$Prompt;mention=$false;plain=$false}|ConvertTo-Json -Compress
    $result=Invoke-RestMethod "$script:endpoint/test/request" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Assert-True ($result.delivered -eq 1) 'Expected one connected offline chat transport'
    Start-Sleep -Milliseconds 300
}
function Wait-Server([scriptblock]$Condition,[int]$Seconds=10) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { $snapshot=Invoke-RestMethod "$script:endpoint/test/results"; if (& $Condition $snapshot) { return $snapshot }; Start-Sleep -Milliseconds 250 }
    while ([DateTime]::UtcNow -lt $deadline)
    throw 'Offline chat fixture did not reach the expected state'
}
function Select-ConversationRequest([string]$Prompt) {
    Set-Value 'InboxSearch' $Prompt
    Select-FirstRow 'InboxConversations'
    Wait-Element 'ChatRequests' | Out-Null
    Select-Request $Prompt
    UI @('wait-for','ApproveChat','-p','IsEnabled','--value','True','-t','5000') | Out-Null
}
function Run-Request([string]$Id,[string]$Filename,[switch]$ExpectAutomaticAttachment) {
    $prompt="CREATE ATTACHMENT $Filename"
    Post $Id $prompt
    Select-ConversationRequest $prompt
    UI @('invoke','ApproveChat') | Out-Null
    Wait-Element 'ChatRunConfirmation' | Out-Null
    UI @('invoke','PrimaryButton') | Out-Null
    UI @('wait-for','ChatOutput','--value',"Created $Filename",'--contains','-t','12000') | Out-Null
    if (-not $ExpectAutomaticAttachment) {
        UI @('wait-for','SendChatAttachments','-p','IsEnabled','--value','True','-t','6000') | Out-Null
    }
}
function Open-AttachmentDialog {
    UI @('invoke','SendChatAttachments') | Out-Null
    Wait-Element 'AttachmentConfirmation' | Out-Null
    Wait-Element 'AttachmentFile1' | Out-Null
}
function Close-AttachmentDialog {
    UI @('invoke','CloseButton') | Out-Null
    UI @('wait-for','AttachmentConfirmation','--gone','-t','4000') | Out-Null
}
function Confirm-OneAttachment {
    $box=Wait-Element 'AttachmentFile1'
    $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    UI @('wait-for','PrimaryButton','-p','IsEnabled','--value','True','-t','3000') | Out-Null
    UI @('invoke','PrimaryButton') | Out-Null
    UI @('wait-for','AttachmentConfirmation','--gone','-t','5000') | Out-Null
}
function Select-Theme([string]$Theme) {
    Navigate 'NavOverview'
    UI @('scroll-into-view',$(if($Theme -eq 'light'){'ThemeLight'}else{'ThemeDark'})) | Out-Null
    UI @('invoke',$(if($Theme -eq 'light'){'ThemeLight'}else{'ThemeDark'})) | Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    do {
        $saved=Get-Content (Join-Path $dataPath 'appearance.json') -Raw | ConvertFrom-Json
        if ($saved.Theme -eq $Theme) { Start-Sleep -Milliseconds 300; return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Could not persist $Theme appearance"
}

try {
    $portFile=Join-Path $dataPath 'port.txt'
    $serverScript=Join-Path $PSScriptRoot 'fixtures/file-attachments-chat-server.cjs'
    $server=Start-Process $node -ArgumentList @('"'+$serverScript+'"','"'+$portFile+'"') -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $portFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True (Test-Path $portFile) 'Offline chat fixture did not start'
    $script:endpoint='http://127.0.0.1:'+(Get-Content $portFile -Raw).Trim()
    $protected='dpapi:'+[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('offline-ui-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    Write-Json 'settings.json' @{
        SchemaVersion=1;WorkspaceRoot=$workspace;AgentProvider='claude';AgentMode='exec';AgentExecutable=$agentPath;AgentArguments=''
        AgentPermissionMode='workspace-write';TrustedWorkspaceRoots=$workspace
        MattermostServerUrl=$script:endpoint;MattermostChannelId='ui-channel';MattermostToken=$protected
        MattermostAllowedUserIds='alice';ChatApprovalMode='always';ChatResultMode='manual';AutoConnectProvider='none'
        FileAttachmentApprovalMode='always';FileAttachmentMaxSizeMb=10;FileAttachmentMaxCount=3;FileAttachmentMaxTotalSizeMb=20
    }
    Write-Json 'onboarding.json' @{schemaVersion=3;step=4;status='completed'}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme='dark'}
    $env:CAB_FILE_ATTACHMENTS_WORKSPACE=$workspace
    $env:CAB_FILE_ATTACHMENTS_TRANSCRIPT=Join-Path $runRoot 'agent-calls.jsonl'
    if ($Portable) {
        $publish=[IO.Path]::GetFullPath($AppDirectory)
        $exe=Join-Path $publish 'ChatAgentBridge.Desktop.exe'
        if (-not (Test-Path $exe) -or $publish -notlike '*\desktop\artifacts\file-attachments-publish*') { throw 'Portable tests require the published attachment test app.' }
        $start=[Diagnostics.ProcessStartInfo]::new($exe); $start.UseShellExecute=$false; $start.WorkingDirectory=$dataPath
        $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath)
        $appProcess=[Diagnostics.Process]::Start($start)
    } else {
        $launch=& winapp run $AppDirectory --detach --json --args ('--data-dir "'+$dataPath+'"') 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Packaged app launch failed' }
        $started=$launch|ConvertFrom-Json; $appProcess=Get-Process -Id $started.processId
    }
    UI @('wait-for','NavChat','-t','15000') | Out-Null
    Navigate 'NavChat'
    UI @('invoke','ConnectMattermost') | Out-Null
    Wait-Server { param($s) $s.connections -eq 1 } | Out-Null

    Check 'Default always mode finds new files without an automatic dialog or upload' {
        Run-Request 'attachment-1' 'first.txt'
        $snapshot=Invoke-RestMethod "$script:endpoint/test/results"
        Assert-True ($snapshot.uploads.Count -eq 0) 'Default always mode uploaded a file automatically'
        $auditText=Get-Content (Join-Path $dataPath 'audit.json') -Raw
        $auditKinds=@((Get-Content (Join-Path $dataPath 'audit.json') -Raw | ConvertFrom-Json).Records | ForEach-Object Kind)
        Assert-True (-not ($auditKinds | Where-Object { $_ -like 'attachment_*' })) 'Default discovery/cancel wrote an attachment approval or send audit event'
        Assert-True (-not $auditText.Contains($workspace) -and -not $auditText.Contains('offline-ui-fixture')) 'Attachment audit exposed a workspace path or token'
        Assert-True ($null -eq (Element 'AttachmentConfirmation')) 'Default always mode opened a confirmation dialog before the user clicked the file action'
        Open-AttachmentDialog
        $checkBox=Element 'AttachmentFile1'
        Assert-True ($checkBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'A file was selected by default'
        Assert-True (-not (Enabled 'PrimaryButton')) 'Send was enabled with no selected files'
        Resize 980 740
        Capture 'dark-attachment-confirmation.png'
        Close-AttachmentDialog
        $snapshot=Invoke-RestMethod "$script:endpoint/test/results"
        Assert-True ($snapshot.uploads.Count -eq 0) 'Cancelling the file dialog uploaded a file'
        $script:checks.Add(@{name='Default always mode, empty selection, and cancel';passed=$true})
    }

    Check 'Light and dark dialogs plus narrow settings layout render' {
        Select-Theme 'light'
        Navigate 'NavChat'
        Open-AttachmentDialog
        Capture 'light-attachment-confirmation.png'
        Close-AttachmentDialog
        Navigate 'NavConnections'
        Resize 720 760
        Choose 'ConnectionsJump' $(if($Language -eq 'ja'){'生成ファイルの添付'}else{'File attachments'})
        $narrowSettings=Wait-ElementInView 'FileAttachmentApprovalMode'
        Capture 'light-settings-narrow.png'
        if ($Language -eq 'ja') {
            Resize 500 760
            $compactMode=Wait-ElementInView 'FileAttachmentApprovalMode'
            Capture 'ja-settings-500.png'
        }
        Resize 1160 840
        Select-Theme 'dark'
        Navigate 'NavConnections'
        Choose 'ConnectionsJump' $(if($Language -eq 'ja'){'生成ファイルの添付'}else{'File attachments'})
        $darkSettings=Wait-ElementInView 'FileAttachmentApprovalMode'
        Capture 'dark-settings.png'
        Navigate 'NavChat'
        $script:checks.Add(@{name='Light/dark dialog and narrow settings captures';passed=$true})
    }

    Check 'First mode requests explicit consent, then uploads later files automatically in the same session' {
        Navigate 'NavConnections'
        Choose 'FileAttachmentApprovalMode' $(if($Language -eq 'ja'){'スレッドの初回のみ確認'}else{'Ask once per conversation'})
        Confirm-SensitiveSettingsAutoSave 'FileAttachmentApprovalMode' 'first'
        Run-Request 'attachment-2' 'second.txt'
        $before=Invoke-RestMethod "$script:endpoint/test/results"
        Assert-True ($before.uploads.Count -eq 0) 'First mode uploaded before a user approved the first file'
        Open-AttachmentDialog
        Assert-True (Has-Text $(if($Language -eq 'ja'){'この選択を承認すると'}else{'Approving this selection also allows'})) 'First-consent future-upload warning is missing'
        Confirm-OneAttachment
        $second=Wait-Server { param($s) $s.uploads.Count -eq 1 -and @($s.replies|Where-Object {$_.root_id -eq 'attachments-thread' -and $_.file_ids}).Count -ge 1 }
        Assert-True ($second.uploads[0].name -eq 'second.txt' -and $second.replies[-1].root_id -eq 'attachments-thread') 'Manual attachment did not upload and post the selected file in the requested thread'
        $auditKinds=@((Get-Content (Join-Path $dataPath 'audit.json') -Raw | ConvertFrom-Json).Records | ForEach-Object Kind)
        Assert-True (@($auditKinds | Where-Object {$_ -eq 'attachment_approved'}).Count -eq 1 -and @($auditKinds | Where-Object {$_ -eq 'attachment_sent'}).Count -eq 1) 'Manual attachment approval/send audit events were not recorded'

        Run-Request 'attachment-3' 'third.txt' -ExpectAutomaticAttachment
        $third=Wait-Server { param($s) $s.uploads.Count -eq 2 }
        Assert-True (@($third.uploads|Where-Object name -eq 'third.txt').Count -eq 1) 'First mode did not upload the next eligible file automatically'
        Assert-True ($null -eq (Element 'AttachmentConfirmation')) 'First mode prompted again after same-session consent'
        $auditKinds=@((Get-Content (Join-Path $dataPath 'audit.json') -Raw | ConvertFrom-Json).Records | ForEach-Object Kind)
        Assert-True (@($auditKinds | Where-Object {$_ -eq 'attachment_approved'}).Count -eq 2 -and @($auditKinds | Where-Object {$_ -eq 'attachment_sent'}).Count -eq 2) 'Automatic attachment audit events were not recorded'

        Navigate 'NavConnections'
        Choose 'ConnectionsJump' $(if($Language -eq 'ja'){'生成ファイルの添付'}else{'File attachments'})
        Set-Value 'FileAttachmentMaxSizeMb' '11'
        Confirm-SensitiveSettingsAutoSave 'FileAttachmentMaxSizeMb' 11 -CommitTextInput
        Run-Request 'attachment-4' 'fourth.txt'
        $changed=Invoke-RestMethod "$script:endpoint/test/results"
        Assert-True ($changed.uploads.Count -eq 2) 'Changed limits reused old consent without asking'
        Open-AttachmentDialog
        Assert-True (Has-Text $(if($Language -eq 'ja'){'この選択を承認すると'}else{'Approving this selection also allows'})) 'Changed limits did not request fresh first-mode consent'
        Confirm-OneAttachment
        $final=Wait-Server { param($s) $s.uploads.Count -eq 3 }
        Assert-True (@($final.uploads|Where-Object name -eq 'fourth.txt').Count -eq 1) 'The newly approved file was not uploaded'
        $auditKinds=@((Get-Content (Join-Path $dataPath 'audit.json') -Raw | ConvertFrom-Json).Records | ForEach-Object Kind)
        Assert-True (@($auditKinds | Where-Object {$_ -eq 'attachment_approved'}).Count -eq 3 -and @($auditKinds | Where-Object {$_ -eq 'attachment_sent'}).Count -eq 3) 'Reconsented attachment audit events were not recorded'
        $calls=@(Get-Content (Join-Path $runRoot 'agent-calls.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
        Assert-True ($calls.Count -eq 4 -and @($calls|Where-Object { $_.args -contains '--resume' }).Count -eq 3) 'The Claude fixture did not resume the same saved session for later requests'
        $script:checks.Add(@{name='First-mode consent, same-session auto send, and changed-limit reconsent';passed=$true})
    }
    UI @('invoke','DisconnectChat') | Out-Null
} catch {
    if ($script:endpoint) { try { Write-Json 'server-results-on-failure.json' (Invoke-RestMethod "$script:endpoint/test/results") } catch { } }
    if ($appProcess -and -not $appProcess.HasExited) { try { Capture 'failure.png' } catch { } }
    $script:checks.Add(@{name='File attachment UI E2E';passed=$false;detail=$_.Exception.Message})
    Write-Output "FAIL $($_.Exception.Message)"
} finally {
    Write-Json 'results.json' @{passed=($script:checks.Count -gt 0 -and @($script:checks|Where-Object{-not $_.passed}).Count -eq 0);checks=@($script:checks)}
    $keep=$KeepRunningOnFailure -and @($script:checks|Where-Object{-not $_.passed}).Count -gt 0
    if ($appProcess -and -not $appProcess.HasExited -and -not $keep) { Stop-Process -Id $appProcess.Id }
    if ($server -and -not $server.HasExited -and -not $keep) { Stop-Process -Id $server.Id }
    Remove-Item Env:CAB_FILE_ATTACHMENTS_WORKSPACE -ErrorAction SilentlyContinue
    Remove-Item Env:CAB_FILE_ATTACHMENTS_TRANSCRIPT -ErrorAction SilentlyContinue
    Write-Output "File attachment UI results: $runRoot"
}
if (@($script:checks|Where-Object{-not $_.passed}).Count -gt 0 -or $script:checks.Count -eq 0) { exit 1 }
