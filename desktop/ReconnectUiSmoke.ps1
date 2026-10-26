# Requires a CABPortable=true, CABDiscordFixture=true publish. No real service or CLI is used.
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/DiagnosticsUiE2E.ps1" -ArtifactRoot $AppDirectory -OnlyScenario @('none')
$assemblyBytes = [IO.File]::ReadAllBytes((Join-Path $artifactRootFull 'ChatAgentBridge.Desktop.dll'))
if (-not [Text.Encoding]::UTF8.GetString($assemblyBytes).Replace([string][char]0,'').Contains('CAB_DISCORD_FIXTURE_URL')) {
    throw 'Refusing to connect: this build does not contain the loopback-only Discord fixture branch.'
}
$reportRoot = Join-Path $artifactsRoot ('reconnect-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$results = [Collections.Generic.List[object]]::new()
$policy = [regex]::Match((Get-Content "$PSScriptRoot/ChatAgentBridge.Core/OnboardingConsentStore.cs" -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
function Wait-Until([scriptblock]$Check,[string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { if (& $Check) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}
function Invoke-Id([string]$Id) { (Element $Id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
foreach ($case in @(@{Language='ja';Theme='dark'},@{Language='en';Theme='light'})) {
    $fixture = $null
    $testRoot = Join-Path $reportRoot ([guid]::NewGuid().ToString('D'))
    $data = Join-Path $testRoot 'data'
    $workspace = Join-Path $testRoot 'workspace'
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    $name = $case.Language + '-' + $case.Theme
    try {
        Write-ScenarioFixture $data $case.Language $case.Theme $policy
        Set-Content -LiteralPath (Join-Path $data '.cab-discord-fixture') -Value 'Offline reconnect UI fixture' -Encoding utf8
        $portFile = Join-Path $testRoot 'port.txt'
        $serverScript = Join-Path $PSScriptRoot 'fixtures/discord-chat-server.cjs'
        $fixture = Start-Process -FilePath (Get-Command node).Source -WindowStyle Hidden -PassThru -ArgumentList @(('"'+$serverScript+'"'),('"'+$portFile+'"'))
        Wait-Until { Test-Path -LiteralPath $portFile } 'loopback server'
        $endpoint = 'http://127.0.0.1:' + (Get-Content -LiteralPath $portFile -Raw).Trim()
        $token = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
            [Text.Encoding]::UTF8.GetBytes('offline-discord-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
        Write-Json (Join-Path $data 'settings.json') @{
            SchemaVersion=1;WorkspaceRoot=$workspace;TrustedWorkspaceRoots=$workspace
            AgentProvider='custom';AgentMode='exec';AgentExecutable=(Join-Path $workspace 'nonexistent-test-cli.exe');AgentArguments=''
            ChatApprovalMode='always';ChatResultMode='manual';PostConnectionNotice=$false;AutoConnectProvider='discord'
            DiscordBotToken=$token;DiscordAllowedUserIds='123456789012345678';DiscordAllowedGuildIds='223456789012345678'
        }
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.UseShellExecute=$false; $start.WorkingDirectory=$artifactRootFull
        $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
        $start.Environment['CAB_DISCORD_FIXTURE_URL']=$endpoint
        $script:activeProcess=[Diagnostics.Process]::Start($start)
        Wait-Until { $activeProcess.Refresh();$activeProcess.MainWindowHandle -ne 0 } 'app window'
        Wait-ElementDirect 'NavChat' 20
        (Element 'NavChat').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $scale = [DiagnosticsUiNative]::GetDpiForWindow($activeProcess.MainWindowHandle) / 96.0
        $width = if ($case.Language -eq 'ja') { 500 } else { 600 }
        [void][DiagnosticsUiNative]::SetWindowPos($activeProcess.MainWindowHandle,[IntPtr]::Zero,30,30,[int]($width*$scale),[int](860*$scale),0x0014)
        Start-Sleep -Milliseconds 250
        $resourcePath = Join-Path $PSScriptRoot ('ChatAgentBridge.Desktop/Localization/DesktopStrings' + $(if($case.Language -eq 'ja'){'.ja'}else{''}) + '.resx')
        $ready = (([xml](Get-Content $resourcePath -Raw)).root.data | Where-Object name -eq 'InboxConnectionReadyLabel').value
        Wait-Until { (Invoke-RestMethod "$endpoint/test/results").connected -eq 1 -and (Element 'InboxConnectionLabel').Current.Name -eq $ready } 'initial connected state'
        if (-not (Element 'ReconnectChat').Current.IsEnabled -or -not (Element 'DisconnectChat').Current.IsEnabled) { throw 'Connected actions are disabled.' }
        foreach ($id in @('ReconnectChat','DisconnectChat')) {
            $bounds=(Element $id).Current.BoundingRectangle; $window=(Root).Current.BoundingRectangle
            if ($bounds.Width -le 0 -or $bounds.Left -lt $window.Left -or $bounds.Right -gt $window.Right) { throw "$id is clipped in a narrow window." }
        }
        UI @('screenshot','-o',(Join-Path $testRoot 'connected.png')) | Out-Null
        $initial = Element 'ConnectDiscord'
        if (-not $initial.Current.IsOffscreen -and $initial.Current.BoundingRectangle.Width -gt 0) { throw 'Initial connect action remained visible while connected.' }
        Record-Pass "$name connected actions and palette are visible"
        $before = (Invoke-RestMethod "$endpoint/test/results").gatewayEvents
        Invoke-Id 'ReconnectChat'
        Wait-Until { $state=Invoke-RestMethod "$endpoint/test/results"; $state.connected -eq 1 -and $state.gatewayEvents -gt $before -and (Element 'InboxConnectionLabel').Current.Name -eq $ready } 'manual reconnect and fresh READY'
        Record-Pass "$name reconnect button establishes a fresh gateway session"
        Invoke-Id 'DisconnectChat'
        Wait-Until { (Invoke-RestMethod "$endpoint/test/results").connected -eq 0 -and ((Element 'DisconnectChat').Current.IsOffscreen -or (Element 'DisconnectChat').Current.BoundingRectangle.Width -le 0) } 'disconnect'
        if (-not (Element 'ReconnectChat').Current.IsEnabled) { throw 'Reconnect is unavailable after disconnect.' }
        Invoke-Id 'ReconnectChat'
        Wait-Until { (Invoke-RestMethod "$endpoint/test/results").connected -eq 1 -and (Element 'InboxConnectionLabel').Current.Name -eq $ready } 'reconnect after disconnect'
        $state=Invoke-RestMethod "$endpoint/test/results"
        if ($state.messages.Count -ne 0 -or $state.inboundRequests.Count -ne 0 -or $state.uploads.Count -ne 0) { throw 'Connection actions unexpectedly posted a message or transferred a file.' }
        Record-Pass "$name disconnect/reconnect works without chat posts or execution"
    }
    catch { Record-Fail "$name reconnect UI" $_.Exception.Message }
    finally {
        Close-TestApp
        if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id; $fixture.WaitForExit(5000) | Out-Null }
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'results.json') -Encoding utf8
Write-Output $reportRoot
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
