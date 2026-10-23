param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en')][string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$appExe = Join-Path $appRoot 'ChatAgentBridge.Desktop.exe'
$coreDll = Join-Path $appRoot 'ChatAgentBridge.Core.dll'
$dataPath = Join-Path $artifactRoot ('performance-persistence-smoke-' + [guid]::NewGuid().ToString('N'))
$helperPath = Join-Path $dataPath 'fixture-helper'
$checks = [Collections.Generic.List[object]]::new()
$appProcess = $null
$windowHandle = [IntPtr]::Zero

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Add-Pass([string]$Name) {
    $checks.Add(@{ name = $Name; passed = $true })
    Write-Output "PASS $Name"
}

function Invoke-Ui([string[]]$Arguments) {
    $result = & winapp ui @Arguments -a $script:appProcess.Id --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $($Arguments -join ' ')" }
    return $result
}

function Find-Element([string]$Id) {
    if ($script:windowHandle -eq [IntPtr]::Zero) { return $null }
    $root = [Windows.Automation.AutomationElement]::FromHandle($script:windowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Element([string]$Id, [int]$Seconds = 20) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $element = Find-Element $Id
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for UI element '$Id'."
}

function Has-Text([string]$Text) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($script:windowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Text)
    $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($item in $items) {
        if ($item.Current.Name.Contains($Text, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Wait-Text([string]$Text, [int]$Seconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Has-Text $Text) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for visible text '$Text'."
}

function Start-TestApp {
    $start = [Diagnostics.ProcessStartInfo]::new($script:appExe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.ArgumentList.Add('--data-dir')
    $start.ArgumentList.Add($script:dataPath)
    $script:appProcess = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($script:appProcess.HasExited) { throw 'The isolated app process exited during startup.' }
        $window = & winapp ui list-windows -a $script:appProcess.Id --json 2>$null | ConvertFrom-Json
        $match = @($window | Where-Object { $_.title -eq 'Chat Agent Bridge' } | Select-Object -First 1)
        if ($match.Count -gt 0 -and [long]$match[0].hwnd -ne 0) {
            $script:windowHandle = [IntPtr]([long]$match[0].hwnd)
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The isolated app window did not become ready.'
}

function Stop-TestApp {
    if ($script:appProcess -and -not $script:appProcess.HasExited) {
        Stop-Process -Id $script:appProcess.Id -Force -ErrorAction SilentlyContinue
        $script:appProcess.WaitForExit(5000) | Out-Null
    }
    $script:appProcess = $null
    $script:windowHandle = [IntPtr]::Zero
}

function Write-JsonFile([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function New-StatisticsFixture {
    $escapedCore = [Security.SecurityElement]::Escape($script:coreDll)
    [IO.Directory]::CreateDirectory($script:helperPath) | Out-Null
    $project = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="ChatAgentBridge.Core"><HintPath>$escapedCore</HintPath></Reference></ItemGroup></Project>
"@
    [IO.File]::WriteAllText((Join-Path $script:helperPath 'Fixture.csproj'), $project, [Text.UTF8Encoding]::new($false))
    $program = @'
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

if (args.Length != 1) throw new ArgumentException("Expected isolated data directory.");
var directory = args[0];
var workspace = Path.Combine(directory, "workspace");
Directory.CreateDirectory(workspace);
var started = DateTimeOffset.Now.AddMinutes(-5);
var sessionId = "11111111-1111-4111-8111-111111111111";
var usage = new AgentUsage
{
    InputTokens = 100, OutputTokens = 20, CachedInputTokens = 20,
    InputIncludesCache = true, Scope = "turn", Source = "fixture", Model = "persist-fixture-model"
};
ExecutionRecord Record(int suffix) => new()
{
    Id = Guid.Parse($"00000000-0000-4000-8000-{suffix:D12}"),
    StartedAt = started.AddSeconds(suffix), FinishedAt = started.AddSeconds(suffix + 1),
    Status = "completed", Source = "mattermost", ChannelId = "CACHECHANNEL01",
    ThreadId = "cache-thread-01", UserId = "CACHEUSER01", WorkspaceRoot = workspace,
    SessionId = sessionId, AgentModel = "persist-fixture-model", Usage = usage
};

var statistics = new ActivityStatisticsStore(directory);
await statistics.MigrateAsync([Record(1), Record(2)]);
await new ExecutionHistoryStore(directory).RecordAsync(Record(3));
var snapshot = await File.ReadAllTextAsync(Path.Combine(directory, "activity-statistics.json"));
var journal = await File.ReadAllTextAsync(Path.Combine(directory, "activity-statistics.journal.jsonl"));
if (snapshot.Contains("JournalGeneration", StringComparison.Ordinal) || !journal.EndsWith("\n", StringComparison.Ordinal))
    throw new InvalidDataException("Fixture did not create a legacy snapshot and committed journal event.");
Console.WriteLine("Created two snapshot executions and one journal execution.");
'@
    [IO.File]::WriteAllText((Join-Path $script:helperPath 'Program.cs'), $program, [Text.UTF8Encoding]::new($false))
    $output = & dotnet run --project (Join-Path $script:helperPath 'Fixture.csproj') --configuration Release -- $script:dataPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Core fixture helper failed: $($output -join ' ')" }
    Write-Output $output
}

try {
    $artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-True ($appRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) 'AppDirectory must point inside desktop/artifacts.'
    Assert-True (Test-Path -LiteralPath $appExe) 'ChatAgentBridge.Desktop.exe is missing from AppDirectory.'
    Assert-True (Test-Path -LiteralPath $coreDll) 'ChatAgentBridge.Core.dll is missing from AppDirectory.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml'))) 'Use an unpackaged app payload.'
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null
    $workspace = Join-Path $dataPath 'workspace'
    [IO.Directory]::CreateDirectory($workspace) | Out-Null
    New-Item -ItemType File -Path (Join-Path $dataPath '.cab-performance-persistence-test') | Out-Null

    $currentStep = 'synthetic-core-statistics-fixture'
    New-StatisticsFixture

    $currentStep = 'synthetic-offline-settings-and-cache'
    $serverUrl = 'http://127.0.0.1:9/'
    $canonicalOrigin = 'http://127.0.0.1:9/'
    $origin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($canonicalOrigin))).ToLowerInvariant()
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $channelId = 'CACHECHANNEL01'
    $userId = 'CACHEUSER01'
    $threadId = 'cache-thread-01'
    $sessionId = '11111111-1111-4111-8111-111111111111'

    Write-JsonFile (Join-Path $dataPath 'settings.json') @{
        SchemaVersion = 1; WorkspaceRoot = $workspace; AgentExecutable = 'not-used'; AgentArguments = ''
        AgentMode = 'exec'; AgentProvider = 'custom'; AutoConnectProvider = 'none'
        MattermostServerUrl = $serverUrl; MattermostChannelId = $channelId; MattermostToken = ''
        MattermostBypassProxy = $true; SlackBotToken = ''; SlackAppToken = ''
        TrustedWorkspaceRoots = $workspace; AgentPermissionMode = 'inherit'
        ChatApprovalMode = 'first'; ChatResultMode = 'manual'
    }
    Write-JsonFile (Join-Path $dataPath 'onboarding.json') @{ schemaVersion = 3; step = 4; status = 'completed' }
    Write-JsonFile (Join-Path $dataPath 'language.json') @{ Language = $Language }
    Write-JsonFile (Join-Path $dataPath 'appearance.json') @{ Theme = 'dark' }
    Write-JsonFile (Join-Path $dataPath 'sessions.json') @{
        SchemaVersion = 1
        Bindings = @(@{
            Key = @{ Provider = 'mattermost'; ChannelId = $channelId; ThreadId = $threadId; UserId = $userId; Origin = $origin }
            WorkspaceRoot = $workspace; AgentExecutable = 'not-used'; AgentArguments = ''; SessionId = $sessionId
            UpdatedAt = $now; Model = $null; ReasoningEffort = $null; DisplayName = 'Offline persistence fixture'
        })
        Workspaces = @(); Agents = @(); ArchivedBindings = @()
    }
    $label = @{
        Provider = 'mattermost'; Origin = $origin; Kind = 'site'; Id = 'site'
        SiteName = 'Cache Site Smoke'; ChannelName = $null; UserName = $null
        IsDirectMessage = $false; Success = $true; CheckedAtUtc = $now
    }
    $siteEntry = $label
    $channelEntry = $label.Clone(); $channelEntry.Kind = 'channel'; $channelEntry.Id = $channelId
    $channelEntry.ChannelName = 'Cache Room Smoke'
    $userEntry = $label.Clone(); $userEntry.Kind = 'user'; $userEntry.Id = $userId
    $userEntry.UserName = '@cache-user (Cache User)'
    $entries = foreach ($item in @($siteEntry, $channelEntry, $userEntry)) {
        @{
            key = @{ provider = $item.Provider; origin = $item.Origin; kind = $item.Kind; id = $item.Id }
            siteName = 'Cache Site Smoke'; channelName = $item.ChannelName; userName = $item.UserName
            isDirectMessage = $false; success = $true; checkedAtUtc = $item.CheckedAtUtc
        }
    }
    Write-JsonFile (Join-Path $dataPath 'chat-identities.json') @{ version = 1; entries = @($entries) }
    Write-JsonFile (Join-Path $dataPath 'audit.json') @{ SchemaVersion = 1; Records = @() }
    Assert-True ((Get-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Raw).Contains('"MattermostToken": ""')) 'Fixture settings must contain no Mattermost token.'
    Add-Pass 'Created isolated statistics and display-name fixtures with empty credentials and loopback-only server URL.'

    $currentStep = 'first-app-start'
    Start-TestApp
    Wait-Element 'NavHistory' | Out-Null
    Invoke-Ui @('invoke', 'NavHistory') | Out-Null
    Wait-Text 'Cache Site Smoke'
    Invoke-Ui @('screenshot', '-o', (Join-Path $dataPath 'first-history.png')) | Out-Null
    Stop-TestApp

    $currentStep = 'restart-offline-history-and-activity-verification'
    Start-TestApp
    Invoke-Ui @('invoke', 'NavHistory') | Out-Null
    Wait-Element 'SavedSessionsList' | Out-Null
    Invoke-Ui @('invoke', 'SavedConversationsTab') | Out-Null
    Wait-Text 'Cache Site Smoke'
    Wait-Text '@cache-user (Cache User)'
    Wait-Text '#Cache Room Smoke'
    Invoke-Ui @('screenshot', '-o', (Join-Path $dataPath 'restarted-conversations.png')) | Out-Null
    Add-Pass 'After restart, History shows the cached Mattermost site, user, and channel labels offline.'

    Invoke-Ui @('invoke', 'ExecutionHistoryTab') | Out-Null
    Wait-Element 'ExecutionHistoryList' | Out-Null
    Wait-Text '#Cache Room Smoke'
    Invoke-Ui @('invoke', 'ActivityTab') | Out-Null
    Wait-Text '360'
    $activityTotal = (Wait-Element 'ActivityTotal').Current.Name
    Assert-True ($activityTotal -eq '3') "Expected three native activity executions after journal replay, found '$activityTotal'."
    $headline = (Wait-Element 'UsageHeadline').Current.Name
    Assert-True ($headline.Contains('360') -and $headline.Contains('20.0%')) "Expected 360 tokens and a 20 percent cache rate after replay, found '$headline'."
    Invoke-Ui @('screenshot', '-o', (Join-Path $dataPath 'restarted-activity.png')) | Out-Null
    $scroll = (Wait-Element 'ActivityScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        if (-not (Find-Element 'UsageHeadline').Current.IsOffscreen) { break }
        $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 100
    }
    Assert-True (-not (Find-Element 'UsageHeadline').Current.IsOffscreen) 'Usage headline could not be brought onscreen.'
    Invoke-Ui @('screenshot', '-o', (Join-Path $dataPath 'restarted-usage.png')) | Out-Null
    Add-Pass 'After restart, native Activity and Usage views include the committed journal delta.'

    $journal = Get-Content -LiteralPath (Join-Path $dataPath 'activity-statistics.journal.jsonl') -Raw
    Assert-True ($journal.EndsWith("`n") -and $journal.Contains('persist-fixture-model')) 'The fixture journal is not a committed synthetic usage delta.'
    Assert-True (-not $journal.Contains($workspace) -and -not $journal.Contains($channelId) -and -not $journal.Contains($userId)) 'The statistics journal contains session or workspace identifiers.'
    $cacheRaw = Get-Content -LiteralPath (Join-Path $dataPath 'chat-identities.json') -Raw
    Assert-True (-not $cacheRaw.Contains('token') -and -not $cacheRaw.Contains('secret')) 'The synthetic identity cache contains credential text.'

    $result = @{
        passed = $true; checks = @($checks); dataDirectory = $dataPath
        synthetic = $true; externalChatHttp = 'disabled by empty tokens and loopback-only Mattermost URL'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-JsonFile (Join-Path $dataPath 'result.json') $result
    Write-Output "PASS performance persistence UI smoke completed: $dataPath"
}
catch {
    $checks.Add(@{ name = $currentStep; passed = $false; error = $_.Exception.Message })
    try {
        Write-JsonFile (Join-Path $dataPath 'result.json') @{
            passed = $false; step = $currentStep; errorType = $_.Exception.GetType().FullName
            checks = @($checks); dataDirectory = $dataPath; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        }
    } catch { }
    Write-Error "Performance persistence UI smoke failed at '$currentStep': $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
finally { Stop-TestApp }
