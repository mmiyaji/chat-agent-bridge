param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en')][string]$Language = 'ja',
    [switch]$Baseline,
    [switch]$AccessLinkOnly,
    [switch]$NarrowAccessOnly,
    [switch]$RefreshOnly,
    [switch]$CompactConversationsOnly
)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$fixtureAppVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe')).ProductVersion.Split('+')[0]
$dataPath = Join-Path $artifactRoot ('history-browser-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dataPath) | Out-Null
Set-Content (Join-Path $dataPath '.cab-history-browser-ui') 'Isolated offline history browser UI test'
$checks = [Collections.Generic.List[object]]::new()
$appProcess = $null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class HistoryBrowserUiNative {
    [StructLayout(LayoutKind.Explicit, Size = 40)] public struct Input {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput {
        public int Dx; public int Dy; public uint Data; public uint Flags; public uint Time; public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static Input Mouse(uint flags, int dx = 0) => new() { Type = 0, Mouse = new MouseInput { Dx = dx, Flags = flags } };
    public static bool DragBy(int dx) => SendInput(3, new[] { Mouse(0x0002), Mouse(0x0001, dx), Mouse(0x0004) }, Marshal.SizeOf<Input>()) == 3;
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
    $idCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $element=(Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$idCondition)
    if ($element -and -not $element.Current.IsOffscreen) { return $element }
    # WinUI can host flyouts in a separate window outside the main UIA tree.
    $processCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)
    foreach($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$processCondition)){
        $popupElement=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$idCondition)
        if($popupElement -and -not $popupElement.Current.IsOffscreen){return $popupElement}
    }
    return $element
}
function Has-Text([string]$Text) {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Text)
    return $null -ne (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Assert-NarrowTableAlignment([string]$ListId,[string[]]$HeaderIds,[string]$Kind) {
    $list=Element $ListId
    $scroll=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    Assert-True ($scroll.Current.HorizontallyScrollable) "$Kind table did not enable horizontal scrolling at the narrow window width."
    $rowsChecked=0
    $seenHeaderIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($percent in @(0,25,50,75,100)) {
        $scroll.SetScrollPercent($percent,-1)
        Start-Sleep -Milliseconds 200
        $rows=@($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)) |
            Where-Object {-not $_.Current.IsOffscreen})
        Assert-True ($rows.Count -gt 0) "$Kind table has no visible rows at the narrow window width."
        $rowsChecked=[Math]::Max($rowsChecked,$rows.Count)
        $headers=@($HeaderIds | ForEach-Object {$id=$_;$element=Element $id;if($element){$bounds=$element.Current.BoundingRectangle;[pscustomobject]@{Id=$id;Element=$element;X=[double]$bounds.X;Width=[double]$bounds.Width;Name=$element.Current.Name}}} | Where-Object {$_.Width -gt 0 -and [double]::IsFinite($_.X)} | Sort-Object X)
        $headerSummary=($headers | ForEach-Object {'{0}@{1}:{2}' -f $_.Name,$_.X,$_.Width}) -join ', '
        Assert-True ($headers.Count -ge 1) "$Kind table exposed no visible column headers at horizontal offset $percent."
        foreach($header in $headers){[void]$seenHeaderIds.Add($header.Id)}
        if($Kind -eq 'Audit') {
            foreach($row in $rows) {
                $bounds=$row.Current.BoundingRectangle
                Assert-True ($bounds.Width -gt 0 -and $bounds.Height -gt 0) 'An audit row disappeared while the table was horizontally scrolled.'
            }
            Capture "history-audit-narrow-horizontal-$percent"
            continue
        }
        foreach($row in $rows) {
            $cellCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
            $cellElements=@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cellCondition))
            $cells=@($cellElements | ForEach-Object {
                $bounds=$_.Current.BoundingRectangle
                [pscustomobject]@{Element=$_;X=[double]$bounds.X;Width=[double]$bounds.Width;Name=$_.Current.Name}
            } | Where-Object {$_.Width -gt 0 -and [double]::IsFinite($_.X)} | Sort-Object X)
            Assert-True ($cells.Count -ge $headers.Count) "$Kind table row has fewer visible cells than its headers at horizontal offset $percent ($($cells.Count)/$($headers.Count)); headers=$headerSummary; cells=$(($cells | ForEach-Object {'{0}@{1}:{2}' -f $_.Name,$_.X,$_.Width}) -join ', ')."
            for($i=0;$i -lt $headers.Count;$i++) {
                $expectedIconOffset=if($Kind -eq 'Execution' -and $headers[$i].Name -match '結果|Result'){20}else{0}
                $difference=[Math]::Abs(($cells[$i].X-$headers[$i].X)-$expectedIconOffset)
                Assert-True ($difference -le 2) "$Kind table header/cell misalignment in '$($headers[$i].Name)' (icon offset $expectedIconOffset; row cell '$($cells[$i].Name)') at horizontal offset $percent ($difference DIP)."
            }
        }
    }
    $unseen=@($HeaderIds | Where-Object {-not $seenHeaderIds.Contains($_)})
    Assert-True ($unseen.Count -eq 0) "$Kind table did not expose all columns over the horizontal scroll range: $($unseen -join ', ')."
    $checks.Add(@{name="Narrow $Kind header/row alignment while horizontally scrolling";passed=$true;rowsChecked=$rowsChecked})
}
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
function Drag-Column([string]$Id,[int]$DeltaX) {
    $element=Element $Id
    Assert-True ($null -ne $element) "Column resize handle is not visible: $Id"
    $bounds=$element.Current.BoundingRectangle
    $x=[int]($bounds.X+$bounds.Width/2); $y=[int]($bounds.Y+$bounds.Height/2)
    Assert-True ([HistoryBrowserUiNative]::SetCursorPos($x,$y)) 'Could not position mouse over the column divider.'
    Start-Sleep -Milliseconds 60
    Assert-True ([HistoryBrowserUiNative]::DragBy($DeltaX)) 'Native mouse drag input was not accepted.'
    Start-Sleep -Milliseconds 300
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 300
    UI @('screenshot','-w',"$($appProcess.MainWindowHandle)",'-o',(Join-Path $dataPath "$Name.png")) | Out-Null
}
function Assert-Visible([string]$Id) {
    $element=Element $Id
    Assert-True ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) "Control is not visible: $Id"
}
function Ensure-SessionAction([string]$Id) {
    $element=Element $Id
    if ($null -eq $element -or $element.Current.IsOffscreen -or $element.Current.BoundingRectangle.Height -le 0) {
        $bar=Element 'SessionActionsCommandBar'
        Assert-True ($null -ne $bar -and -not $bar.Current.IsOffscreen) 'Session actions command bar is not visible.'
        $moreCondition=[System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,'MoreButton')
        $more=$bar.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$moreCondition)
        Assert-True ($null -ne $more) 'CommandBar overflow button was not found.'
        UI @('invoke','MoreButton') | Out-Null
        UI @('wait-for',$Id,'-t','4000') | Out-Null
        $element=Element $Id
    }
    Assert-True ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Height -gt 0) "Session action is not reachable: $Id"
    return $element
}
function Close-SessionOverflow {
    $detach=Element 'DetachSession'
    if ($null -ne $detach -and -not $detach.Current.IsOffscreen) {
        UI @('invoke','MoreButton') | Out-Null
    }
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
    # Batched list updates keep the selected conversation in view, so reset the scroll position to read the true first row.
    $listScroll=$null
    try { $listScroll=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch {}
    if ($listScroll -and $listScroll.Current.VerticallyScrollable) { $listScroll.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 250 }
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $row=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    Assert-True ($null -ne $row) 'No visible saved conversation row.'
    $textCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
    $labels=$row.FindAll([System.Windows.Automation.TreeScope]::Descendants,$textCondition)
    return (@($labels | ForEach-Object {$_.Current.Name}) -join ' ') + ' ' + $row.Current.Name
}
function Assert-FirstRowOnScreenWithoutScroll([string]$ExpectedText,[string]$Step) {
    Start-Sleep -Milliseconds 400
    $list=Element 'SavedSessionsList'
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $row=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    Assert-True ($null -ne $row) "$Step : no saved conversation row."
    Assert-True (-not $row.Current.IsOffscreen) "$Step : the first row is off-screen right after changing the sort (the list kept the selected row in view instead of showing the top)."
    $listScroll=$null
    try { $listScroll=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch {}
    if ($listScroll -and $listScroll.Current.VerticallyScrollable) { Assert-True ($listScroll.Current.VerticalScrollPercent -lt 1) "$Step : list is scrolled away from the top ($($listScroll.Current.VerticalScrollPercent)%)." }
    $textCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
    $text=(@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,$textCondition) | ForEach-Object {$_.Current.Name}) -join ' ') + ' ' + $row.Current.Name
    Assert-True ($text.Contains($ExpectedText)) "$Step : visible first row was '$text', expected '$ExpectedText'."
}
function Get-VisibleAuditRow {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    foreach($row in (Element 'AuditHistoryList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)) {
        if (-not $row.Current.IsOffscreen -and $null -ne (Get-AuditContextCell $row)) { return $row }
    }
    return $null
}
function Get-AuditContextCell([System.Windows.Automation.AutomationElement]$Row) {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AuditContextCell')
    return $Row.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Get-ElementText([System.Windows.Automation.AutomationElement]$Element) {
    if (-not [string]::IsNullOrWhiteSpace($Element.Current.Name)) { return $Element.Current.Name }
    try { return $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return '' }
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
    if ($null -eq (Element 'RenameSession') -or (Element 'RenameSession').Current.IsOffscreen) {
        Click-FirstRow
    }
    Ensure-SessionAction 'RenameSession' | Out-Null
    UI @('invoke','RenameSession') | Out-Null
    UI @('wait-for','SessionDisplayName','-t','5000') | Out-Null
    Capture 'history-rename-dialog'
    (Element 'SessionDisplayName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Name)
    Invoke-RenameDialogButton 'PrimaryButton'
    Start-Sleep -Milliseconds 350
}
function Invoke-RenameDialogButton([string]$Id) {
    $dialog=Element 'RenameSessionDialog'
    Assert-True ($null -ne $dialog) 'Rename dialog is missing.'
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $button=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    Assert-True ($null -ne $button) "Rename dialog button '$Id' is missing."
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
function Wait-SelectedName([string]$Id,[string]$Expected) {
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    do {
        $actual=Selected-Name $Id
        if($actual -eq $Expected){return}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Expected '$Expected' for $Id, got '$actual'."
}
function Open-SessionFilters {
    UI @('invoke','SavedSessionsAdvancedFilters') | Out-Null
    UI @('wait-for','SavedSessionsProviderFilter','-t','5000') | Out-Null
}
function Close-SessionFilters {
    [void][HistoryBrowserUiNative]::SetForegroundWindow($appProcess.MainWindowHandle)
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    # After the sort combo box collapses the Escape key can be consumed by the combo box itself; use the flyout's Close button then.
    $closeDeadline=[DateTime]::UtcNow.AddSeconds(1.5)
    while ($null -ne (Element 'SavedSessionsProviderFilter') -and [DateTime]::UtcNow -lt $closeDeadline) { Start-Sleep -Milliseconds 100 }
    if ($null -ne (Element 'SavedSessionsProviderFilter')) { UI @('invoke','CloseSessionFilters') | Out-Null }
    Element-Gone 'SavedSessionsProviderFilter'
}
try {
    Assert-True ($appRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must be below desktop/artifacts.'
    Assert-True (-not (Test-Path (Join-Path $appRoot 'AppxManifest.xml'))) 'Use an unpackaged portable app payload.'
    $folders=@('project-alpha-long-folder','project-beta','project-gamma','project-delta')
    foreach ($folder in $folders) { [IO.Directory]::CreateDirectory((Join-Path $dataPath $folder)) | Out-Null }
    $aliases=@('Release preparation','Client report review','日本語スプリント記録','Architecture decision notes','Quarterly planning','Long running migration')
    $bindingCount=if($CompactConversationsOnly){3}else{42}
    $bindings=@(for($i=0;$i -lt $bindingCount;$i++) {
        $pair=[int][math]::Floor($i/2); $provider=if($pair%2 -eq 0){'slack'}else{'mattermost'}
        $user=if($provider -eq 'slack'){if($i%2 -eq 0){'U00000001'}else{'U00000002'}}else{if($i%2 -eq 0){('a'*25)+'1'}else{('b'*25)+'2'}}
        $origin=if($provider -eq 'slack'){'slack'}else{'a'*64}
        $workspace=Join-Path $dataPath $folders[$pair%$folders.Count]
        $channel=if($provider -eq 'slack'){'C'+$pair.ToString('D8')}else{('c'*18)+$pair.ToString('D8')}
        $key=@{Provider=$provider;ChannelId=$channel;ThreadId="shared-thread-$pair";UserId=$user;Origin=$origin}
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
    $records[0].StartedAt=[DateTimeOffset]::UtcNow.ToString('o')
    $records[0].FinishedAt=[DateTimeOffset]::UtcNow.ToString('o')
    if ($RefreshOnly) {
        $recordList = [Collections.Generic.List[object]]::new()
        foreach ($record in $records) { $recordList.Add($record) }
        for ($index = 0; $index -lt 449; $index++) {
            $recordList.Add(@{Id=[guid]::NewGuid().ToString();StartedAt=[DateTimeOffset]::UtcNow.AddMinutes(-$index).ToString('o');FinishedAt=[DateTimeOffset]::UtcNow.ToString('o')
                Source='slack';ChannelId=$bindings[0].Key.ChannelId;ThreadId=$bindings[0].Key.ThreadId;UserId=$bindings[0].Key.UserId;WorkspaceRoot=$bindings[0].WorkspaceRoot
                Status='completed';ExitCode=0;SessionId=$bindings[0].SessionId;AgentModel='fixture-model';AgentReasoningEffort='low'})
        }
        $records = $recordList
    }
    $identityEntries=@{}
    foreach($binding in $bindings) {
        $key=$binding.Key
        $site=if($key.Provider -eq 'slack'){'Example workspace'}else{'Example company'}
        $userName=if($key.UserId -in @('U00000001',(('a'*25)+'1'))){'@alice (Alice)'}else{'@bob (Bob)'}
        $channelName=if($key.Provider -eq 'slack'){'release-planning'}else{'開発相談'}
        foreach($kind in @('user','channel')) {
            $entityId=if($kind -eq 'user'){$key.UserId}else{$key.ChannelId}
            $identityEntries["$($key.Provider)/$($key.Origin)/$kind/$entityId"]=@{
                key=@{provider=$key.Provider;origin=$key.Origin;kind=$kind;id=$entityId}
                siteName=$site;channelName=$(if($kind -eq 'channel'){$channelName}else{$null})
                userName=$(if($kind -eq 'user'){$userName}else{$null});isDirectMessage=$false
                success=$true;checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
            }
        }
    }
    if(-not $Baseline) { Write-Json 'chat-identities.json' @{version=1;entries=@($identityEntries.Values)} }
    Write-Json 'sessions.json' @{SchemaVersion=1;Bindings=$bindings;Workspaces=@()}
    Write-Json 'history.json' @{SchemaVersion=1;Records=$records}
    $linkedUserId='U00000009'
    $auditRecords=@(@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Kind='completed';ExecutionId=$records[0].Id;Source=$records[0].Source;ChannelId=$records[0].ChannelId;ThreadId=$records[0].ThreadId})
    if(-not $Baseline -and -not $CompactConversationsOnly){
        foreach($fixture in @(
            @{Service='slack';UserId=$linkedUserId;Name='Expected access subject'},
            @{Service='mattermost';UserId=$linkedUserId;Name='Same ID on another service'},
            @{Service='slack';UserId="$($linkedUserId)0";Name='Different user with the same ID prefix'},
            @{Service='slack';UserId='U_FALLBACK_0001';Name=$null},
            @{Service='mattermost';UserId='U_FALLBACK_0001';Name=$null},
            @{Service='slack';UserId='U_UNKNOWN_0001';Name=$null}
        )){
            $auditRecords+=@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Kind='allowed_user_added';Source=$fixture.Service;ChannelId='';ThreadId='';
                Access=@{UserId=$fixture.UserId;DisplayName=$fixture.Name;Actor='Local';Method='settings'}}
        }
    }
    Write-Json 'audit.json' @{SchemaVersion=1;Records=$auditRecords}
    Write-Json 'allowed-users.json' @(
        @{Provider='slack';UserId='U_FALLBACK_0001';DisplayName='Slack directory name';AddedAt=[DateTimeOffset]::UtcNow.ToString('o');Method='settings';Actor='Local'},
        @{Provider='mattermost';UserId='U_FALLBACK_0001';DisplayName='Mattermost directory name';AddedAt=[DateTimeOffset]::UtcNow.ToString('o');Method='settings';Actor='Local'}
    )
    Write-Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$dataPath;AgentProvider='custom';AgentMode='exec';AgentExecutable='not-used';AgentArguments='';TrustedWorkspaceRoots=$dataPath;AutoConnectProvider='none';SlackAllowedUserIds=$linkedUserId;MattermostAllowedUserIds=$(if($AccessLinkOnly -and -not $Baseline){$linkedUserId}else{''})}
    Write-Json 'portable-update.json' @{schemaVersion=1;automaticChecksEnabled=$false;includePrereleases=$true}
    Write-Json 'update-notice.json' @{SchemaVersion=1;LastObservedVersion=$fixtureAppVersion;PendingVersion=$null;DismissedVersion=$fixtureAppVersion}
    Write-Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    Write-Json 'tips.json' @{Seen=@('chat-connect')}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme=$(if($Language -eq 'ja'){'dark'}else{'light'})}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=[IO.Path]::GetTempPath(); $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataPath)
    $appProcess=[Diagnostics.Process]::Start($start)
    Write-Json 'launch.json' @{ProcessId=$appProcess.Id;DataPath=$dataPath}
    UI @('wait-for','NavHistory','-t','15000') | Out-Null
    if(-not $Baseline -and -not $CompactConversationsOnly){
        if($NarrowAccessOnly){Resize 500 600}else{Resize 1280 800}
        foreach($visit in @('first','revisit')){
            Check "Allowed user link keeps access mode, service and exact ID on $visit visit" {
                Navigate 'NavConnections'
                UI @('invoke','ConnectionScopeCommon') | Out-Null
                if($AccessLinkOnly){
                    Check 'Common allowed-user list recovers audit-only names per service' {
                        $textType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
                        $deadline=[DateTime]::UtcNow.AddSeconds(8)
                        do {
                            $names=@((Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,$textType) | ForEach-Object {$_.Current.Name})
                            if(($names -join "`n").Contains('Expected access subject') -and ($names -join "`n").Contains('Same ID on another service')){break}
                            Start-Sleep -Milliseconds 150
                        } while([DateTime]::UtcNow -lt $deadline)
                        $joined=$names -join "`n"
                        Assert-True ($joined.Contains('Expected access subject') -and $joined.Contains($linkedUserId)) 'Slack allowed-user row did not recover the name and ID from its audit record.'
                        Assert-True ($joined.Contains('Same ID on another service') -and $joined.Contains($linkedUserId)) 'Mattermost allowed-user row did not recover the name and ID from its audit record.'
                        Capture 'allowed-users-audit-name-fallback'
                    }
                }
                if($AccessLinkOnly){
                    $historyId=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AllowedUserHistory')
                    $historyButtons=@((Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,$historyId))
                    Assert-True ($historyButtons.Count -eq 2) "Expected one history button per same-ID service row; found $($historyButtons.Count)."
                    $slackHistory=@($historyButtons | Where-Object {$_.Current.Name.Contains('Expected access subject')})
                    Assert-True ($slackHistory.Count -eq 1) 'Could not identify the Slack history action by the audit-only user name.'
                    $slackHistory[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                }else{
                    UI @('invoke','AllowedUserHistory') | Out-Null
                }
                UI @('wait-for','AuditHistoryList','-t','10000') | Out-Null
                Assert-True ((Element 'AccessHistoryTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'Access mode is not selected'
                Assert-True ((Selected-Name 'AuditServiceFilter') -eq 'Slack') 'Service selection did not survive navigation'
                Assert-True ((Element 'HistoryRecordSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $linkedUserId) 'User ID filter was not set'
                # The detail drawer stays open across page visits. Below 1000 DIP of table width it overlays the table with light dismiss,
                # so a row click would only dismiss it. Close it first so the row click must open the details itself.
                if(Element 'CloseRecordDetail'){
                    UI @('invoke','CloseRecordDetail') | Out-Null
                    Element-Gone 'CloseRecordDetail'
                }
                $type=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
                if($NarrowAccessOnly){
                    $auditList=Element 'AuditHistoryList'
                    $scroll=$auditList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                    Assert-True ($scroll.Current.HorizontallyScrollable) 'Access table must remain horizontally scrollable at 500 DIP.'
                    $scroll.SetScrollPercent(100,-1)
                    Start-Sleep -Milliseconds 200
                }
                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                do {
                    $rows=(Element 'AuditHistoryList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$type)
                    if($rows.Count -eq 1 -and $rows[0].Current.Name.Contains('Expected access subject')){break}
                    Start-Sleep -Milliseconds 100
                } while([DateTime]::UtcNow -lt $deadline)
                Assert-True ($rows.Count -eq 1 -and $rows[0].Current.Name.Contains('Expected access subject')) 'Wrong service or partial user ID leaked into the results'
                $contextCell=Get-AuditContextCell $rows[0]
                Assert-True ($null -ne $contextCell -and $contextCell.Current.Name.Contains('Expected access subject') -and $contextCell.Current.Name.Contains($linkedUserId)) "Access target cell did not show the exact target name and ID: $(if($contextCell){$contextCell.Current.Name}else{'missing AuditContextCell'})"
                if($NarrowAccessOnly){
                    Assert-True (-not $contextCell.Current.IsOffscreen -and $contextCell.Current.BoundingRectangle.Width -gt 0) 'Target user name and ID are not reachable at the rightmost horizontal offset.'
                }
                $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                UI @('click',$rows[0].Current.Name) | Out-Null
                Start-Sleep -Milliseconds 300
                $detailType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
                $detailTexts=@((Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,$detailType) | ForEach-Object {$_.Current.Name})
                $fullIdentityDetail=@($detailTexts | Where-Object {$_.Contains('Expected access subject') -and $_.Contains($linkedUserId) -and $_.Contains('Local')})
                Assert-True ($fullIdentityDetail.Count -gt 0) 'Access record details lost the full target identity and actor.'
                Capture $(if($NarrowAccessOnly){"access-narrow-rightmost-$visit"}else{"access-link-$visit"})
            }
        }
        if($AccessLinkOnly){
            Check 'Unknown access subject falls back to its exact ID' {
                (Element 'HistoryRecordSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('U_UNKNOWN_0001')
                $auditRowType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                do {
                    $rows=(Element 'AuditHistoryList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$auditRowType)
                    if($rows.Count -eq 1 -and $rows[0].Current.Name.Contains('U_UNKNOWN_0001')){break}
                    Start-Sleep -Milliseconds 100
                } while([DateTime]::UtcNow -lt $deadline)
                Assert-True ((Selected-Name 'AuditServiceFilter') -eq 'Slack') 'Unknown user search lost the selected service.'
                Assert-True ($rows.Count -eq 1) 'Unknown user search returned unrelated access records.'
                $contextCell=Get-AuditContextCell $rows[0]
                Assert-True ($null -ne $contextCell -and $contextCell.Current.Name -eq 'U_UNKNOWN_0001') "Unknown access subject did not fall back to its ID: $(if($contextCell){$contextCell.Current.Name}else{'missing AuditContextCell'})"
                Capture 'access-unknown-id-fallback'
            }
            Check 'Access directory name fallback is scoped to service and ID' {
                (Element 'HistoryRecordSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('U_FALLBACK_0001')
                $auditRowType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                do {
                    $rows=(Element 'AuditHistoryList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$auditRowType)
                    if($rows.Count -eq 1 -and $rows[0].Current.Name.Contains('U_FALLBACK_0001')){break}
                    Start-Sleep -Milliseconds 100
                } while([DateTime]::UtcNow -lt $deadline)
                Assert-True ((Selected-Name 'AuditServiceFilter') -eq 'Slack') 'Directory fallback search lost the selected service.'
                Assert-True ($rows.Count -eq 1) 'Directory fallback matched a same-ID record from another service.'
                $contextCell=Get-AuditContextCell $rows[0]
                Assert-True ($null -ne $contextCell -and $contextCell.Current.Name -eq 'Slack directory name (U_FALLBACK_0001)') "Directory fallback used the wrong service identity: $(if($contextCell){$contextCell.Current.Name}else{'missing AuditContextCell'})"
                Capture 'access-directory-name-fallback'
            }
            Write-Json 'result.json' @{passed=$true;language=$Language;checks=$checks}
            Write-Output "PASS Allowed user access history E2E: $dataPath"
            return
        }
        UI @('invoke','ClearRecordFilters') | Out-Null
        UI @('invoke','SavedConversationsTab') | Out-Null
    }
    Navigate 'NavHistory'
    UI @('wait-for','SavedSessionsList','-t','10000') | Out-Null
    if ($CompactConversationsOnly) {
        Resize 1280 800
        Check 'Three compact conversations remain selectable with matching details' {
            Wait-Count 3 3
            $list=Element 'SavedSessionsList'
            $rowType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
            $rows=@($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,$rowType) | Where-Object {-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Height -gt 0})
            Assert-True ($rows.Count -eq 3) "Expected three visible compact conversations, got $($rows.Count)."
            $targetIndex=1
            $target=$rows[$targetIndex]
            $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            UI @('click',$target.Current.Name) | Out-Null
            Wait-Text $bindings[$targetIndex].DisplayName
            $identifierExpander=Element 'SessionIdentifiers'
            $identifierExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            Start-Sleep -Milliseconds 150
            $selectedId=Element 'SelectedSessionId'
            Assert-True ((Get-ElementText $selectedId) -eq $bindings[$targetIndex].SessionId) "Selected detail session ID did not match row ${targetIndex}: '$(Get-ElementText $selectedId)'."
            $selectedUserIdentity=Element 'SelectedSessionUserIdentity'
            Assert-True ($selectedUserIdentity.Current.Name.Contains($bindings[$targetIndex].Key.UserId) -and $selectedUserIdentity.Current.Name.Contains('@bob (Bob)')) 'Selected conversation detail did not preserve the matching user name and ID.'
            $selection=$list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            Assert-True ($selection.Count -eq 1 -and $selection[0].Current.Name.Contains($bindings[$targetIndex].DisplayName)) 'Selected row and conversation detail refer to different conversations.'
            Capture 'history-three-compact-conversations-detail'
        }
        Write-Json 'result.json' @{passed=$true;language=$Language;compactConversationsOnly=$true;checks=$checks}
        Write-Output "PASS Compact conversation history E2E: $dataPath"
        return
    }
    if ($RefreshOnly) {
        $refreshButton = Element 'RefreshHistory'
        $readyDeadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            $refreshButton = Element 'RefreshHistory'
            if ($refreshButton -and $refreshButton.Current.IsEnabled) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $readyDeadline)
        Assert-True ($refreshButton -and $refreshButton.Current.IsEnabled) 'History refresh is not ready.'
        $idleProgress = Element 'RefreshHistoryProgressRing'
        $idleProgressHidden=$idleProgress.Current.IsOffscreen -or $idleProgress.Current.BoundingRectangle.Width -le 0
        Assert-True ($idleProgressHidden) 'History refresh spinner is still shown while idle.'
        $refreshIdleScreenshot=Join-Path $dataPath 'history-refresh-idle.png'
        UI @('screenshot','-o',$refreshIdleScreenshot) | Out-Null
        $buttonBoundsBefore = $refreshButton.Current.BoundingRectangle
        $buttonNameBefore = $refreshButton.Current.Name
        $statusElement = Element 'HistoryStatus'
        $statusBoundsBefore = $statusElement.Current.BoundingRectangle
        $statusNameBefore = $statusElement.Current.Name
        Assert-True ([string]::IsNullOrWhiteSpace($statusNameBefore)) 'History fixture did not load successfully before refreshing.'
        $refreshButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $refreshButton = Element 'RefreshHistory'
        $observedBusy = -not $refreshButton.Current.IsEnabled
        $buttonBoundsDuring = $refreshButton.Current.BoundingRectangle
        $buttonNameDuring = $refreshButton.Current.Name
        $statusElement = Element 'HistoryStatus'
        $statusBoundsDuring = $statusElement.Current.BoundingRectangle
        $duringStatus = $statusElement.Current.Name
        $refreshIndicator = Element 'RefreshHistoryProgressRing'
        $progressObserved = $refreshIndicator -and $refreshIndicator.Current.BoundingRectangle.Width -gt 0 -and -not $refreshIndicator.Current.IsOffscreen
        $progressName = if ($refreshIndicator) { $refreshIndicator.Current.Name } else { '' }
        $busyProgressBounds = if ($refreshIndicator) { $refreshIndicator.Current.BoundingRectangle } else { [System.Windows.Rect]::Empty }
        $indicatorPositionStable = [Math]::Abs($busyProgressBounds.Width - 16) -le 1 -and [Math]::Abs($busyProgressBounds.Height - 16) -le 1
        $refreshScreenshot = Join-Path $dataPath 'history-refresh-inflight.png'
        UI @('screenshot','-o',$refreshScreenshot) | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $refreshButton = Element 'RefreshHistory'
            if ($refreshButton.Current.IsEnabled) { break }
            $observedBusy = $true
            Start-Sleep -Milliseconds 20
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-True ($refreshButton.Current.IsEnabled) 'History refresh did not finish.'
        $statusElement = Element 'HistoryStatus'
        $statusBoundsAfter = $statusElement.Current.BoundingRectangle
        Assert-True ($statusElement.Current.Name -eq $statusNameBefore -and $duringStatus -eq $statusNameBefore) 'History refresh showed transient status text.'
        Assert-True ([Math]::Abs($statusBoundsDuring.Top - $statusBoundsBefore.Top) -lt 1 -and [Math]::Abs($statusBoundsDuring.Height - $statusBoundsBefore.Height) -lt 1 -and [Math]::Abs($statusBoundsAfter.Top - $statusBoundsBefore.Top) -lt 1 -and [Math]::Abs($statusBoundsAfter.Height - $statusBoundsBefore.Height) -lt 1) 'History status row moved or resized during refresh.'
        $buttonBoundsAfter = $refreshButton.Current.BoundingRectangle
        Assert-True ($buttonNameDuring -eq $buttonNameBefore -and $refreshButton.Current.Name -eq $buttonNameBefore -and [Math]::Abs($buttonBoundsDuring.Width - $buttonBoundsBefore.Width) -lt 1 -and [Math]::Abs($buttonBoundsAfter.Width - $buttonBoundsBefore.Width) -lt 1) 'History refresh button content or width changed.'
        $expectedProgressName = if ($Language -eq 'ja') { '履歴を更新しています' } else { 'Refreshing history' }
        Assert-True ($observedBusy -and $progressObserved -and $progressName.Contains($expectedProgressName) -and $indicatorPositionStable) 'History refresh busy state did not show the 16x16 spinner in the reserved icon footprint.'
        $checks.Add(@{name='Refresh uses a stable status row and button';passed=$true;busyObserved=$observedBusy;progressVisibleToAutomation=$progressObserved;progressName=$progressName})
        Write-Json 'result.json' @{passed=$true;language=$Language;refreshOnly=$true;historyCount=$records.Count;checks=$checks;screenshot=$refreshScreenshot;processId=$appProcess.Id}
        Write-Output "PASS History refresh UI: $dataPath"
        return
    }
    Resize 1280 800
    $screenshotName = if($Baseline){"baseline-history-$Language"}else{'history-wide'}
    Capture $screenshotName
    if (-not $Baseline) {
        Wait-Count 42 42
        Check 'Meaningful conversation names and matched activity status/count' {
            Open-SessionFilters
            if ($Language -eq 'ja') { $allProvider='すべてのサービス'; $allWorkspace='すべての作業フォルダー'; $recent='最近更新した順' }
            else { $allProvider='All services'; $allWorkspace='All workspaces'; $recent='Recently updated' }
            Wait-SelectedName 'SavedSessionsProviderFilter' $allProvider
            Wait-SelectedName 'SavedSessionsWorkspaceFilter' $allWorkspace
            Wait-SelectedName 'SavedSessionsSort' $recent
            Close-SessionFilters
            $first=Get-FirstRowText
            Assert-True ($first.Contains('Release preparation')) 'The newest session did not show its saved display name.'
            Assert-True ($first -notmatch $bindings[0].SessionId) 'The session identifier was used as the conversation title.'
            Assert-True ($first.Contains('Example workspace') -and $first.Contains('@alice (Alice)') -and $first.Contains('#release-planning')) 'Cached site, channel, and user names are missing.'
            Assert-True (-not $first.Contains($bindings[0].Key.ThreadId) -and -not $first.Contains($bindings[0].Key.UserId) -and -not $first.Contains($bindings[0].Key.ChannelId)) 'Opaque IDs leaked into the conversation row.'
            Select-FirstRow 'SavedSessionsList' | Out-Null
            $expectedActivity=if($Language -eq 'ja'){'最終: 完了 · 1回実行'}else{'Last: Completed · Runs: 1'}
            Assert-True ($first.Contains($expectedActivity)) 'The first conversation is not joined to exactly its own completed execution.'
            Capture 'history-wide'
        }
        Check 'Names are searchable and execution/audit history use the same chat identity' {
            Set-Search 'SavedSessionsSearch' '@alice'
            Wait-Count 21 42
            Set-Search 'SavedSessionsSearch' 'Example workspace'
            Wait-Count 22 42
            UI @('invoke','ClearSessionFilters') | Out-Null
            Wait-Count 42 42
            UI @('invoke','ExecutionHistoryTab') | Out-Null
            (Element 'ExecutionHistoryModeItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 250
            $visibleText=@((Element 'ExecutionHistoryList').FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)) | Where-Object {-not $_.Current.IsOffscreen} | ForEach-Object {$_.Current.Name}) -join "`n"
            Assert-True ($visibleText.Contains('@alice (Alice)') -and $visibleText.Contains('#release-planning')) 'Execution history is missing its human-readable chat identity.'
            Assert-True (-not $visibleText.Contains($bindings[0].Key.ThreadId)) 'Execution history exposes thread IDs before expanding details.'
            Capture 'history-execution-names'
            Resize 500 600
            Assert-NarrowTableAlignment 'ExecutionHistoryList' @('RunSortStarted','RunSortConversation','RunSortUser','RunSortResult','RunSortModel','RunSortDuration','RunSortTokens') 'Execution'
            Capture 'history-execution-narrow-horizontal-scroll'
            Resize 1280 800
            UI @('invoke','AuditHistoryTab') | Out-Null
            Start-Sleep -Milliseconds 200
            Assert-True (Has-Text 'Release preparation · #release-planning · @alice (Alice)') 'Audit history is not linked to the correct execution identity.'
            Capture 'history-audit-names'
            # The widened time/action columns make the audit table scroll horizontally at 500 DIP even before any column is dragged.
            # Record that baseline so the later check can prove the dragged conversation column adds no extra overflow.
            Resize 500 600
            $baselineScroll=(Element 'AuditHistoryList').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current
            Resize 1280 800
            $beforeDrag=(Element 'AuditResizeConversation').Current.BoundingRectangle.X
            Drag-Column 'AuditResizeConversation' -80
            $afterDrag=(Element 'AuditResizeConversation').Current.BoundingRectangle.X
            $wideConversationWidth=$afterDrag-(Element 'AuditConversationColumn').Current.BoundingRectangle.X
            Assert-True ($afterDrag -lt $beforeDrag - 40) "Dragging the conversation divider left did not reduce the column width (divider X $beforeDrag -> $afterDrag, column width $wideConversationWidth)."
            Capture 'history-audit-resized-wide'
            Resize 500 600
            # Reveal the rightmost column; its divider is clipped when the table is scrolled to the left.
            $auditScrollPattern=(Element 'AuditHistoryList').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            $auditScrollPattern.SetScrollPercent(100,-1)
            Start-Sleep -Milliseconds 250
            $narrowConversationWidth=(Element 'AuditResizeConversation').Current.BoundingRectangle.X-(Element 'AuditConversationColumn').Current.BoundingRectangle.X
            $auditRow=Get-VisibleAuditRow
            $narrowContextWidth=if($auditRow){$cc=Get-AuditContextCell $auditRow; if($cc){$cc.Current.BoundingRectangle.Width}else{0}}else{0}
            Assert-True ($narrowConversationWidth -ge 150) "The conversation column header collapsed at 500 DIP after resizing (width $narrowConversationWidth, expected >= 150)."
            Assert-True ($narrowContextWidth -ge 150) "The conversation/target cell collapsed at 500 DIP after resizing (width $narrowContextWidth, expected >= 150)."
            # The column now has a 160 DIP floor: it only has room to shrink further when the wide-window width was above that floor.
            Assert-True ($narrowConversationWidth -le $wideConversationWidth + 1 -and ($wideConversationWidth -lt 200 -or $narrowConversationWidth -lt $wideConversationWidth - 40)) "The conversation column did not shrink with the narrow viewport after resizing (wide $wideConversationWidth, narrow $narrowConversationWidth)."
            Write-Output "INFO audit conversation column: wide $wideConversationWidth DIP, at 500 DIP header $narrowConversationWidth DIP, cell $narrowContextWidth DIP"
            $auditScroll=$auditScrollPattern.Current
            Assert-True ($auditScroll.HorizontalViewSize -ge $baselineScroll.HorizontalViewSize - 0.5) "The resized conversation column caused extra horizontal overflow at 500 DIP (visible $($auditScroll.HorizontalViewSize)% vs $($baselineScroll.HorizontalViewSize)% before resizing)."
            Capture 'history-audit-narrow-resized'
            Resize 1280 800
            Drag-Column 'AuditResizeTime' 150
            Drag-Column 'AuditResizeAction' 50
            Resize 500 600
            Capture 'history-audit-narrow-horizontal-scroll'
            Assert-NarrowTableAlignment 'AuditHistoryList' @('AuditTimeColumn','AuditActionColumn','AuditServiceColumn','AuditConversationColumn') 'Audit'
            Resize 1280 800
            UI @('invoke','SavedConversationsTab') | Out-Null
        }
        Check 'Workspace, provider, and display-name search combine correctly' {
            Open-SessionFilters
            Choose 'SavedSessionsWorkspaceFilter' (Join-Path $dataPath $folders[0])
            Choose 'SavedSessionsProviderFilter' 'Slack'
            Close-SessionFilters
            Wait-Count 12 42
            Set-Search 'SavedSessionsSearch' 'Release preparation'
            Wait-Count 2 42
            Assert-True ((Get-FirstRowText).Contains('Release preparation')) 'Alias search did not find the matching session.'
            Capture 'history-filtered'
            UI @('invoke','ClearSessionFilters') | Out-Null
            Wait-Count 42 42
            Assert-True ((Element 'SavedSessionsSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '') 'Clear filters did not clear the search.'
            Open-SessionFilters
            $provider=Selected-Name 'SavedSessionsProviderFilter'
            $workspace=Selected-Name 'SavedSessionsWorkspaceFilter'
            if ($Language -eq 'ja') { $allProvider='すべてのサービス'; $allWorkspace='すべての作業フォルダー' }
            else { $allProvider='All services'; $allWorkspace='All workspaces' }
            Assert-True ($provider -eq $allProvider) 'Provider filter did not return to the default.'
            Assert-True ($workspace -eq $allWorkspace) 'Workspace filter did not return to the default.'
            Close-SessionFilters
        }
        Check 'Recent, name, and usage sorts change the first conversation' {
            Open-SessionFilters
            if ($Language -eq 'ja') { $recent='最近更新した順'; $byName='会話名順'; $byUsage='実行回数が多い順' }
            else { $recent='Recently updated'; $byName='Name'; $byUsage='Most runs' }
            Choose 'SavedSessionsSort' $byName
            Assert-FirstRowOnScreenWithoutScroll 'Architecture decision notes' 'Name sort (selected row is no longer first)'
            Assert-True ((Get-FirstRowText).Contains('Architecture decision notes')) 'Name sort did not put the alphabetically first title at the top.'
            Choose 'SavedSessionsSort' $byUsage
            Assert-True ((Get-FirstRowText).Contains('日本語スプリント記録')) 'Usage sort did not put the eight-run conversation first.'
            Choose 'SavedSessionsSort' $recent
            Assert-True ((Get-FirstRowText).Contains('Release preparation')) 'Recent sort did not restore the newest conversation to the top.'
            Close-SessionFilters
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
            Ensure-SessionAction 'RenameSession' | Out-Null
            UI @('invoke','RenameSession') | Out-Null
            UI @('wait-for','SessionDisplayName','-t','5000') | Out-Null
            (Element 'SessionDisplayName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Cancelled rename')
            Invoke-RenameDialogButton 'CloseButton'
            Element-Gone 'RenameSessionDialog'
            Assert-RenameStored 0 'Renamed release preparation'

            Rename-Selected ''
            Wait-Text 'project-alpha-long-folder'
            Assert-RenameStored 0 ''
            Assert-RenameStored 1 $initialBobName
        }
        Check 'Narrow detail keeps actions reachable through CommandBar overflow' {
            Click-FirstRow
            Resize 500 600
            UI @('wait-for','BackToSessionsButton','-t','5000') | Out-Null
            Assert-Visible 'BackToSessionsButton'
            foreach ($action in @('EditSessionModel','SwitchSessionAgent','EditSessionExecutionPolicy','RenameSession','DetachSession')) {
                $element=Ensure-SessionAction $action
                Assert-True ($element.Current.IsEnabled) "Session action is disabled at narrow size: $action"
            }
            Close-SessionOverflow
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
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Resize 1280 800
            Assert-Visible 'SavedSessionsAdvancedFilters'
            UI @('invoke','SavedSessionsAdvancedFilters') | Out-Null
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
