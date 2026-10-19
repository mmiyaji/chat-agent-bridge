param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en')][string]$Language = 'ja',
    [switch]$Baseline
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$dataPath = Join-Path $artifactRoot ('history-browser-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dataPath) | Out-Null
Set-Content (Join-Path $dataPath '.cab-history-browser-ui') 'Isolated offline history browser UI test'
$checks = [Collections.Generic.List[object]]::new()
$appProcess = $null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class HistoryBrowserUiNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
Add-Type -AssemblyName System.Windows.Forms
function Write-Json([string]$Name, $Value) { $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $dataPath $Name) -Encoding utf8 }
function UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $appProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed ($LASTEXITCODE): $($Arguments -join ' '): $($output -join ' ')" }
    return $output
}
function Root { [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle) }
function Element([string]$Id) {
    (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Has-Text([string]$Text) {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Text)
    return $null -ne (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Navigate([string]$Id) {
    $item = Element $Id
    if ($null -eq $item -or $item.Current.IsOffscreen) { UI @('invoke','TogglePaneButton') | Out-Null }
    UI @('invoke',$Id) | Out-Null
    Start-Sleep -Milliseconds 250
}
function Select-FirstRow([string]$Id) {
    $list=Element $Id
    $row=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    Assert-True ($null -ne $row) "No row in $Id"
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 200
    return $row
}
function Click-FirstRow {
    $row=Select-FirstRow 'SavedSessionsList'
    UI @('click',$row.Current.Name) | Out-Null
    Start-Sleep -Milliseconds 200
}
function Set-Search([string]$Id,[string]$Text) {
    (Element $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 180
}
function Choose([string]$Id,[string]$Name) {
    $combo=Element $Id
    $pattern=$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand(); Start-Sleep -Milliseconds 120
    $condition=[System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id))
    $matches=@([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) |
        Where-Object {-not $_.Current.IsOffscreen} | Group-Object { $_.GetRuntimeId() -join ',' } | ForEach-Object {$_.Group[0]})
    Assert-True ($matches.Count -eq 1) "Expected one choice '$Name' for $Id"
    $matches[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $pattern.Collapse(); Start-Sleep -Milliseconds 180
}
function Resize([int]$Width,[int]$Height) {
    $scale=[HistoryBrowserUiNative]::GetDpiForWindow($appProcess.MainWindowHandle)/96.0
    Assert-True ([HistoryBrowserUiNative]::SetWindowPos($appProcess.MainWindowHandle,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int]($Height*$scale),0x0010)) 'Resize failed'
    Start-Sleep -Milliseconds 250
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 300
    UI @('screenshot','-w',"$($appProcess.MainWindowHandle)",'-o',(Join-Path $dataPath "$Name.png")) | Out-Null
}
function Assert-Visible([string]$Id) {
    $element=Element $Id
    Assert-True ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) "Control is not visible: $Id"
}
function Wait-Text([string]$Text,[int]$Seconds=8) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Text)
        if ($null -ne (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for visible text: $Text"
}
function Get-FirstRowText {
    $list=Element 'SavedSessionsList'
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $row=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    Assert-True ($null -ne $row) 'No visible saved conversation row.'
    $textCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
    $labels=$row.FindAll([System.Windows.Automation.TreeScope]::Descendants,$textCondition)
    return (@($labels | ForEach-Object {$_.Current.Name}) -join ' ') + ' ' + $row.Current.Name
}
function Wait-Count([int]$Visible,[int]$Total=42,[int]$Seconds=8) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $name=(Element 'SavedSessionsCount').Current.Name
        if ($Language -eq 'ja') { $wanted="会話 $Total 件中 $Visible 件を表示" } else { $wanted="Showing $Visible of $Total conversations" }
        if ($name -eq $wanted) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "Expected history count '$wanted', got '$name'"
}
function Start-App {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=[IO.Path]::GetTempPath(); $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath)
    $script:appProcess=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavHistory','-t','15000') | Out-Null
    Navigate 'NavHistory'
    UI @('wait-for','SavedSessionsList','-t','10000') | Out-Null
}
function Rename-Selected([string]$Name) {
    if ($null -eq (Element 'RenameSession')) {
        Click-FirstRow
        UI @('wait-for','RenameSession','-t','5000') | Out-Null
    }
    UI @('invoke','RenameSession') | Out-Null
    UI @('wait-for','SessionDisplayName','-t','5000') | Out-Null
    Capture 'history-rename-dialog'
    (Element 'SessionDisplayName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Name)
    UI @('invoke','PrimaryButton') | Out-Null
    Start-Sleep -Milliseconds 350
}
function Assert-RenameStored([int]$Index,[string]$Expected) {
    $saved=Get-Content -LiteralPath (Join-Path $dataPath 'sessions.json') -Raw | ConvertFrom-Json
    $actual=$saved.Bindings[$Index].DisplayName
    $matches=if ([string]::IsNullOrEmpty($Expected)) {[string]::IsNullOrEmpty($actual)} else {$actual -ceq $Expected}
    Assert-True $matches "Unexpected display name for fixture binding $Index"
}
function Check([string]$Name,[scriptblock]$Body) {
    & $Body
    $checks.Add(@{name=$Name;passed=$true})
    Write-Output "PASS $Name"
}
function Element-Gone([string]$Id,[int]$Seconds=5) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($null -eq (Element $Id)) { return }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $Id to close."
}
function Selected-Name([string]$Id) {
    $element=Element $Id
    try {
        $selected=$element.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($selected.Count -gt 0) { return $selected[0].Current.Name }
    } catch { }
    try { return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return $element.Current.Name }
}
try {
    Assert-True ($appRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be below desktop/artifacts.'
    Assert-True (-not (Test-Path (Join-Path $appRoot 'AppxManifest.xml'))) 'Use an unpackaged portable app payload.'
    $folders=@('project-alpha-long-folder','project-beta','project-gamma','project-delta')
    foreach ($folder in $folders) { [IO.Directory]::CreateDirectory((Join-Path $dataPath $folder)) | Out-Null }
    $aliases=@('Release preparation','Client report review','日本語スプリント記録','Architecture decision notes','Quarterly planning','Long running migration')
    $bindings=@(for($i=0;$i -lt 42;$i++) {
        $pair=[math]::Floor($i/2); $provider=if($pair%2 -eq 0){'slack'}else{'mattermost'}
        $user=if($i%2 -eq 0){'alice'}else{'bob'}; $origin=if($provider -eq 'slack'){'slack'}else{'a'*64}
        $workspace=Join-Path $dataPath $folders[$pair%$folders.Count]
        $key=@{Provider=$provider;ChannelId="shared-channel-$pair";ThreadId="shared-thread-$pair";UserId=$user;Origin=$origin}
        @{Key=$key;WorkspaceRoot=$workspace;AgentExecutable='codex';AgentArguments='exec --json -';SessionId=([guid]::NewGuid().ToString('N'))
          UpdatedAt=([DateTimeOffset]::UtcNow.AddMinutes(-$i).ToString('o'));Model='fixture-model';ReasoningEffort='low';DisplayName=$aliases[$i%$aliases.Count]}
    })
    $historyStatuses=@('completed','failed','cancelled','running')
    $records=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt $bindings.Count;$i++) {
        $binding=$bindings[$i]; $key=$binding.Key; $runCount=if($i -eq 8){8}else{1}
        for($run=0;$run -lt $runCount;$run++) {
            $status=$historyStatuses[($i+$run)%$historyStatuses.Count]
            $records.Add(@{Id=([guid]::NewGuid().ToString());StartedAt=([DateTimeOffset]::UtcNow.AddMinutes(-100+$i+$run).ToString('o'))
              FinishedAt=([DateTimeOffset]::UtcNow.AddMinutes(-99+$i+$run).ToString('o'));Source=$key.Provider;ChannelId=$key.ChannelId;ThreadId=$key.ThreadId
              UserId=$key.UserId;WorkspaceRoot=$binding.WorkspaceRoot;Status=$status;ExitCode=$(if($status -eq 'completed'){0}else{1});SessionId=$binding.SessionId
              AgentModel='fixture-model';AgentReasoningEffort='low';Usage=@{InputTokens=100+$i;OutputTokens=20;CachedInputTokens=0;CacheWriteInputTokens=0;Scope='last-request';Source='fixture';Model='fixture-model'}})
        }
    }
    # An execution with the same conversation/session ID but a different owner must not decorate another owner's saved session.
    $records += @{Id=([guid]::NewGuid().ToString());StartedAt=([DateTimeOffset]::UtcNow.AddMinutes(-2).ToString('o'));FinishedAt=([DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o'))
      Source='slack';ChannelId=$bindings[0].Key.ChannelId;ThreadId=$bindings[0].Key.ThreadId;UserId='mallory';WorkspaceRoot=$bindings[0].WorkspaceRoot
      Status='completed';ExitCode=0;SessionId=$bindings[0].SessionId;AgentModel='fixture-model';AgentReasoningEffort='low'}
    $records += @{Id=([guid]::NewGuid().ToString());StartedAt=([DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o'));FinishedAt=([DateTimeOffset]::UtcNow.ToString('o'))
      Source='mattermost';ChannelId='orphan-channel';ThreadId='orphan-thread';UserId='alice';WorkspaceRoot=(Join-Path $dataPath $folders[0])
      Status='failed';ExitCode=1;SessionId='orphan-stale-session-id';AgentModel='fixture-model';AgentReasoningEffort='low'}
    Write-Json 'sessions.json' @{SchemaVersion=1;Bindings=$bindings;Workspaces=@()}
    Write-Json 'history.json' @{SchemaVersion=1;Records=$records}
    Write-Json 'audit.json' @{SchemaVersion=1;Records=@()}
    Write-Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$dataPath;AgentProvider='custom';AgentMode='exec';AgentExecutable='not-used';AgentArguments='';TrustedWorkspaceRoots=$dataPath;AutoConnectProvider='none'}
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme=$(if($Language -eq 'ja'){'dark'}else{'light'})}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=[IO.Path]::GetTempPath(); $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath)
    $appProcess=[Diagnostics.Process]::Start($start)
    Write-Json 'launch.json' @{ProcessId=$appProcess.Id;DataPath=$dataPath}
    UI @('wait-for','NavHistory','-t','15000') | Out-Null
    Navigate 'NavHistory'
    UI @('wait-for','SavedSessionsList','-t','10000') | Out-Null
    Resize 1280 800
    $screenshotName = if($Baseline){"baseline-history-$Language"}else{'history-wide'}
    Capture $screenshotName
    if (-not $Baseline) {
        Wait-Count 42 42
        Check 'Meaningful conversation names and matched activity status/count' {
            $initialProvider=Selected-Name 'SavedSessionsProviderFilter'
            $initialWorkspace=Selected-Name 'SavedSessionsWorkspaceFilter'
            $initialSort=Selected-Name 'SavedSessionsSort'
            if ($Language -eq 'ja') { $allProvider='すべてのサービス'; $allWorkspace='すべての作業フォルダー'; $recent='最近更新した順' }
            else { $allProvider='All services'; $allWorkspace='All workspaces'; $recent='Recently updated' }
            Assert-True ($initialProvider -eq $allProvider) 'Initial provider filter is not set to All.'
            Assert-True ($initialWorkspace -eq $allWorkspace) 'Initial workspace filter is not set to All.'
            Assert-True ($initialSort -eq $recent) 'Initial sort is not set to Recently updated.'
            $first=Get-FirstRowText
            Assert-True ($first.Contains('Release preparation')) 'The newest session did not show its saved display name.'
            Assert-True ($first -notmatch $bindings[0].SessionId) 'The session identifier was used as the conversation title.'
            Select-FirstRow 'SavedSessionsList' | Out-Null
            $expectedActivity=if($Language -eq 'ja'){'最終: 完了 · 1回実行'}else{'Last: Completed · Runs: 1'}
            Assert-True ($first.Contains($expectedActivity)) 'The first conversation is not joined to exactly its own completed execution.'
            Capture 'history-wide'
        }
        Check 'Workspace, provider, and display-name search combine correctly' {
            Choose 'SavedSessionsWorkspaceFilter' (Join-Path $dataPath $folders[0])
            Choose 'SavedSessionsProviderFilter' 'Slack'
            Wait-Count 12 42
            Set-Search 'SavedSessionsSearch' 'Release preparation'
            Wait-Count 2 42
            Assert-True ((Get-FirstRowText).Contains('Release preparation')) 'Alias search did not find the matching session.'
            Capture 'history-filtered'
            UI @('invoke','ClearSessionFilters') | Out-Null
            Wait-Count 42 42
            Assert-True ((Element 'SavedSessionsSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '') 'Clear filters did not clear the search.'
            $provider=Selected-Name 'SavedSessionsProviderFilter'
            $workspace=Selected-Name 'SavedSessionsWorkspaceFilter'
            if ($Language -eq 'ja') { $allProvider='すべてのサービス'; $allWorkspace='すべての作業フォルダー' }
            else { $allProvider='All services'; $allWorkspace='All workspaces' }
            Assert-True ($provider -eq $allProvider) 'Provider filter did not return to the default.'
            Assert-True ($workspace -eq $allWorkspace) 'Workspace filter did not return to the default.'
        }
        Check 'Recent, name, and usage sorts change the first conversation' {
            if ($Language -eq 'ja') { $recent='最近更新した順'; $byName='会話名順'; $byUsage='実行回数が多い順' }
            else { $recent='Recently updated'; $byName='Name'; $byUsage='Most runs' }
            Choose 'SavedSessionsSort' $byName
            Assert-True ((Get-FirstRowText).Contains('Architecture decision notes')) 'Name sort did not put the alphabetically first title at the top.'
            Choose 'SavedSessionsSort' $byUsage
            Assert-True ((Get-FirstRowText).Contains('日本語スプリント記録')) 'Usage sort did not put the eight-run conversation first.'
            Choose 'SavedSessionsSort' $recent
            Assert-True ((Get-FirstRowText).Contains('Release preparation')) 'Recent sort did not restore the newest conversation to the top.'
        }
        Check 'Rename persists across refresh/restart, is owner-scoped, and empty restores the default' {
            $initialBobName=$bindings[1].DisplayName
            Select-FirstRow 'SavedSessionsList' | Out-Null
            Rename-Selected 'Renamed release preparation'
            Wait-Text 'Renamed release preparation'
            Assert-RenameStored 0 'Renamed release preparation'
            Assert-RenameStored 1 $initialBobName
            UI @('invoke','RefreshHistory') | Out-Null
            Wait-Text 'Renamed release preparation'
            Stop-Process -Id $appProcess.Id -Force
            $appProcess.WaitForExit(5000) | Out-Null
            Start-Sleep -Milliseconds 350
            Start-App
            Wait-Text 'Renamed release preparation'
            Capture 'history-after-restart'

            Click-FirstRow
            UI @('invoke','RenameSession') | Out-Null
            UI @('wait-for','SessionDisplayName','-t','5000') | Out-Null
            (Element 'SessionDisplayName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Cancelled rename')
            UI @('invoke','CloseButton') | Out-Null
            Element-Gone 'RenameSessionDialog'
            Assert-RenameStored 0 'Renamed release preparation'

            Rename-Selected ''
            Wait-Text 'project-alpha-long-folder'
            Assert-RenameStored 0 ''
            Assert-RenameStored 1 $initialBobName
        }
        Check 'Narrow detail keeps Back and rename actions visible' {
            Click-FirstRow
            Resize 500 600
            UI @('wait-for','BackToSessionsButton','-t','5000') | Out-Null
            Assert-Visible 'BackToSessionsButton'
            Assert-Visible 'RenameSession'
            Assert-Visible 'EditSessionModel'
            Assert-Visible 'EditSessionExecutionPolicy'
            Assert-Visible 'DetachSession'
            Capture 'history-narrow-detail'
            UI @('invoke','BackToSessionsButton') | Out-Null
            $list=Element 'SavedSessionsList'
            Assert-True ($null -ne $list -and -not $list.Current.IsOffscreen -and $list.Current.BoundingRectangle.Height -ge 160) 'Narrow conversation list has insufficient visible height.'
            $itemCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
            $visibleRows=@($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,$itemCondition) | Where-Object {-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Height -gt 0})
            Assert-True ($visibleRows.Count -ge 2) 'Fewer than two conversation rows are visible at narrow size.'
            Capture 'history-narrow-list'
            Assert-Visible 'SavedSessionsAdvancedFilters'
            UI @('invoke','SavedSessionsAdvancedFilters') | Out-Null
            Assert-Visible 'SavedSessionsProviderFilter'
            Assert-Visible 'SavedSessionsWorkspaceFilter'
            Assert-Visible 'SavedSessionsSort'
            Capture 'history-narrow-filters'
            UI @('invoke','SavedSessionsAdvancedFilters') | Out-Null
            Resize 1280 800
            Assert-Visible 'SavedSessionsProviderFilter'
            Assert-Visible 'SavedSessionsWorkspaceFilter'
            Assert-Visible 'SavedSessionsSort'
        }
    }
    Write-Json 'result.json' @{passed=$true;language=$Language;baseline=[bool]$Baseline;bindingCount=$bindings.Count;historyCount=$records.Count;checks=$checks;screenshot=(Join-Path $dataPath "$screenshotName.png");processId=$appProcess.Id}
    Write-Output "PASS History browser UI run completed: $dataPath"
} catch {
    $failureScreenshot=$null
    try { if($appProcess -and -not $appProcess.HasExited){$failureScreenshot=Join-Path $dataPath 'failure.png';Capture 'failure'} } catch { }
    Write-Json 'result.json' @{passed=$false;error=$_.Exception.Message;processId=$(if($appProcess){$appProcess.Id}else{$null});screenshot=$failureScreenshot;checks=$checks}
    throw
} finally {
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue }
}
