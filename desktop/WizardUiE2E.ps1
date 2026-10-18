param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$FixtureExe,
    [Parameter(Mandatory)][string]$ServerUrl
)

$ErrorActionPreference = 'Stop'
$dataPath = [IO.Path]::GetFullPath($DataDir)
$fixturePath = [IO.Path]::GetFullPath($FixtureExe)
$artifactRoot = Join-Path $PSScriptRoot 'artifacts/onboarding-wizard-review'
$screenshots = Join-Path $artifactRoot 'screenshots'
$resultsPath = Join-Path $artifactRoot 'ui-results.json'
$results = [System.Collections.Generic.List[object]]::new()
$currentStep = 'preflight'
$currentSubstep = 'verify isolated empty settings directory'
$token = 'notification-fixture-token'

function Invoke-UiProcess([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command winapp -ErrorAction Stop).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $cliArguments = @('ui') + $Arguments + @('-a', "$AppPid")
    if ($cliArguments -notcontains '--json') { $cliArguments += '--json' }
    foreach ($argument in $cliArguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $start
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(45000)) { $child.Kill(); throw "winapp timed out during '$($Arguments[0])'." }
        return @{ ExitCode=$child.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally { $child.Dispose() }
}

function Invoke-UI([string[]]$Arguments) {
    $r = Invoke-UiProcess $Arguments
    if ($r.ExitCode -ne 0) { throw "winapp ui $($Arguments[0]) '$($Arguments[1])' failed: $($r.Error) $($r.Output)" }
    return $r.Output
}
function Get-Value([string]$Id) {
    $r = Invoke-UiProcess @('get-value', $Id, '--json')
    if ($r.ExitCode -ne 0) { return $null }
    try { return [string](($r.Output | ConvertFrom-Json).text) } catch { return $null }
}
function Get-Property([string]$Id,[string]$Property) {
    $r = Invoke-UiProcess @('get-property', $Id, '-p', $Property, '--json')
    if ($r.ExitCode -ne 0) { return $null }
    try { return [string](($r.Output | ConvertFrom-Json).properties.$Property) } catch { return $null }
}
function Wait-Value([string]$Id,[string]$Value,[int]$Seconds=15) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do { if ((Get-Value $Id) -like "*$Value*") { return $true }; Start-Sleep -Milliseconds 250 } while ([DateTimeOffset]::UtcNow -lt $until)
    return $false
}
function Wait-Progress([string]$Expected,[int]$Seconds=15) {
    $until = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do { if ((Get-Value 'OnboardingTitle') -match $Expected) { return $true }; Start-Sleep -Milliseconds 250 } while ([DateTimeOffset]::UtcNow -lt $until)
    return $false
}
function Capture([string]$Name) {
    $path = Join-Path $screenshots $Name
    Invoke-UI @('screenshot','-o',$path,'--focus') | Out-Null
    if (-not (Test-Path -LiteralPath $path)) { throw "Screenshot was not created: $Name" }
}
function Select-Candidate([string]$ComboId,[string]$Label,[switch]$Prefix) {
    Invoke-UI @('scroll-into-view',$ComboId) | Out-Null
    Invoke-UI @('invoke',$ComboId) | Out-Null
    $treeResult = Invoke-UiProcess @('inspect',$ComboId,'-d','6')
    if ($treeResult.ExitCode -ne 0) { throw "Could not inspect the opened ComboBox '$ComboId'." }
    try { $tree = $treeResult.Output | ConvertFrom-Json } catch { throw "The opened ComboBox '$ComboId' returned invalid UI tree JSON." }
    function Find-ListItem($node,[string]$wanted,[bool]$allowPrefix) {
        if ($null -eq $node) { return $null }
        if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string] -and $node -isnot [pscustomobject]) {
            foreach ($child in $node) { $found = Find-ListItem $child $wanted $allowPrefix; if ($found) { return $found } }
            return $null
        }
        if ($node -is [pscustomobject]) {
            if ($node.type -eq 'ListItem' -and $node.selector -and
                ([string]::Equals([string]$node.name,$wanted,[StringComparison]::Ordinal) -or ($allowPrefix -and ([string]$node.name).StartsWith($wanted,[StringComparison]::Ordinal)))) { return [string]$node.selector }
            foreach ($property in $node.PSObject.Properties) { $found = Find-ListItem $property.Value $wanted $allowPrefix; if ($found) { return $found } }
        }
        return $null
    }
    $selector = Find-ListItem $tree $Label ([bool]$Prefix)
    if (-not $selector) { throw "ComboBox '$ComboId' did not expose expected candidate '$Label'." }
    Invoke-UI @('invoke',$selector) | Out-Null
}
function Set-Value([string]$Id,[string]$Value) {
    Invoke-UI @('scroll-into-view',$Id) | Out-Null
    Invoke-UI @('set-value',$Id,$Value) | Out-Null
}
function Add-Pass([string]$Name) { $results.Add([pscustomobject]@{ name=$Name; passed=$true }) }
function Assert-That([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }

try {
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'AppPid is not running.' }
    if (-not (Test-Path -LiteralPath $dataPath -PathType Container)) { throw 'DataDir does not exist.' }
    $artifactRootPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
    if (-not $dataPath.StartsWith($artifactRootPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'DataDir must be inside desktop/artifacts.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-wizard-test'))) { throw 'The isolated wizard test marker is missing from DataDir.' }
    if (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json')) { throw 'Expected an empty settings directory; settings.json already exists.' }
    if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) { throw 'FixtureExe does not exist.' }
    $fixtureHost = Split-Path -Parent $fixturePath
    if (-not (Test-Path -LiteralPath (Join-Path $fixtureHost '.cab-offline-test-agent')) -or
        -not (Test-Path -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.dll')) -or
        -not (Test-Path -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.exe')) -or
        (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $fixtureHost 'ChatAgentBridge.TestAgent.exe') -Algorithm SHA256).Hash) {
        throw 'FixtureExe is not the marked offline TestAgent executable.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath 'work') -PathType Container)) { throw 'Expected the launcher to create DataDir/work.' }
    if ($ServerUrl -notmatch '^https?://' -or -not ([uri]$ServerUrl).IsLoopback) { throw 'ServerUrl must be a loopback HTTP(S) fixture URL.' }
    New-Item -ItemType Directory -Force -Path $screenshots | Out-Null
    if (-not (Wait-Progress '\[1\s*/\s*5\]' 30)) { throw 'The first-install wizard did not start at page 1 of 5.' }
    Capture '01-intro.png'
    Add-Pass 'empty data directory opens the five-page onboarding flow'

    $currentStep = 'page 1 architecture, terms, and privacy consent'
    $currentSubstep = 'verify architecture illustration and reject missing consent'
    Invoke-UI @('wait-for','WizardArchitecture','-t','10000') | Out-Null
    Assert-That ((Get-Value 'WizardArchitecture') -match 'Slack|Mattermost|PC') 'The architecture illustration is not exposed with its accessible description.'
    Invoke-UI @('scroll-into-view','WizardTerms') | Out-Null
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 500
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[1\s*/\s*5\]') 'The intro page advanced without either consent checkbox.'
    Assert-That ((Get-Property 'WizardStatus' 'IsOffscreen') -eq 'False') 'Consent validation feedback was hidden outside the scroll viewport.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $dataPath 'consent.json'))) 'Consent was saved without both selections.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json'))) 'Settings were saved before the AI setup page.'

    $currentSubstep = 'read terms policy dialog and close it'
    Invoke-UI @('invoke','WizardTerms') | Out-Null
    Assert-That (Wait-Value 'PolicyBody' '現状有姿' 10) 'The terms dialog did not show the current as-is policy text.'
    Invoke-UI @('invoke','PolicyClose') | Out-Null
    Invoke-UI @('wait-for','PolicyBody','--gone','-t','5000') | Out-Null
    $currentSubstep = 'read privacy policy dialog and close it'
    Invoke-UI @('invoke','WizardPrivacy') | Out-Null
    Assert-That ((Wait-Value 'PolicyBody' 'DPAPI' 10) -and (Wait-Value 'PolicyBody' '外部' 5)) 'The privacy dialog did not expose DPAPI and external-service disclosures.'
    Invoke-UI @('invoke','PolicyClose') | Out-Null
    Invoke-UI @('wait-for','PolicyBody','--gone','-t','5000') | Out-Null

    $currentSubstep = 'require both consent checkboxes before moving forward'
    Invoke-UI @('invoke','WizardAcceptTerms') | Out-Null
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 500
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[1\s*/\s*5\]') 'One consent checkbox was enough to advance.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $dataPath 'consent.json'))) 'Consent was saved after only one selection.'
    Invoke-UI @('invoke','WizardAcceptPrivacy') | Out-Null
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[2\s*/\s*5\]') 'Both consent selections did not advance to AI setup.'
    $consentFile = Join-Path $dataPath 'consent.json'
    Assert-That (Test-Path -LiteralPath $consentFile) 'Consent record was not created after both selections.'
    $consent = Get-Content -LiteralPath $consentFile -Raw | ConvertFrom-Json
    Assert-That (-not [string]::IsNullOrWhiteSpace([string]$consent.PolicyVersion)) 'Consent record has no policy version.'
    $consentDocument = [System.Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $consentFile -Raw))
    try { $acceptedAt = $consentDocument.RootElement.GetProperty('AcceptedAtUtc').GetDateTimeOffset() }
    finally { $consentDocument.Dispose() }
    Assert-That ($acceptedAt.Offset -eq [TimeSpan]::Zero) 'Consent AcceptedAtUtc is missing or not UTC.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json'))) 'Settings were saved before the AI setup page was completed.'
    Capture '02-step-agent.png'
    Add-Pass 'policy dialogs disclose required text; both explicit consents are required and saved before any settings'

    $currentStep = 'page 2 agent and workspace'
    $currentSubstep = 'reject invalid workspace and accept valid workspace'
    $invalid = Join-Path $dataPath 'does-not-exist'
    Select-Candidate 'WizardAgentProvider' 'Claude Code'
    Set-Value 'WizardWorkspaceRoot' $invalid
    Set-Value 'WizardAgentExecutable' $fixturePath
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 700
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[2\s*/\s*5\]') 'Invalid workspace advanced the wizard.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json'))) 'Invalid workspace caused settings to be saved.'
    $workspace = Join-Path $dataPath 'work'
    Set-Value 'WizardWorkspaceRoot' $workspace
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Valid agent and workspace did not advance to the CLI page.'
    Assert-That (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json')) 'AI setup did not save settings when advancing to CLI setup.'
    Capture '03-step-cli.png'
    Add-Pass 'workspace validation blocks bad path and accepts valid Claude fixture path'

    $currentStep = 'page 3 CLI and model'
    $currentSubstep = 'require CLI check before advancing'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 500
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[3\s*/\s*5\]') 'CLI page advanced without a CLI check.'
    $currentSubstep = 'reject an unavailable CLI executable'
    Invoke-UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-That (Wait-Progress '\[2\s*/\s*5\]') 'Back did not return to the agent settings page.'
    Set-Value 'WizardAgentExecutable' 'cab-wizard-nonexistent-cli'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Changing the executable prevented return to the CLI page.'
    Invoke-UI @('invoke','CheckCli') | Out-Null
    Assert-That (Wait-Value 'CliCheckSummary' '確認できませんでした' 20) 'CLI check did not report the missing executable.'
    Invoke-UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-That (Wait-Progress '\[2\s*/\s*5\]') 'Back did not return after the unavailable CLI result.'
    Set-Value 'WizardAgentExecutable' $fixturePath
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Restoring FixtureExe did not return to the CLI page.'
    $currentSubstep = 'check fixture CLI version and model catalog'
    Invoke-UI @('invoke','CheckCli') | Out-Null
    Assert-That (Wait-Value 'CliCheckSummary' 'モデル候補を確認しました' 35) 'CLI version/model check did not succeed.'
    Assert-That ((Get-Value 'CliVersion') -match 'Claude Code 1\.2\.3') 'The CLI version text did not identify the Claude fixture.'
    Assert-That ((Get-Value 'CliExecutablePath') -like "*$([IO.Path]::GetFileName($fixturePath))") 'Resolved executable path did not identify FixtureExe.'
    $currentSubstep = 'invalidate a successful CLI check after changing its executable signature'
    Invoke-UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-That (Wait-Progress '\[2\s*/\s*5\]') 'Back did not return to the agent settings page for signature invalidation.'
    Set-Value 'WizardAgentExecutable' 'cab-wizard-nonexistent-cli'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Changed CLI executable did not return to the CLI page.'
    Assert-That (Wait-Value 'CliCheckSummary' 'もう一度チェックしてください' 10) 'Changing the CLI executable did not invalidate the previous successful check.'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 500
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[3\s*/\s*5\]') 'CLI page advanced after the signature changed without a new check.'
    Invoke-UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-That (Wait-Progress '\[2\s*/\s*5\]') 'Back did not return to restore the fixture executable.'
    Set-Value 'WizardAgentExecutable' $fixturePath
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Restoring FixtureExe did not return to the CLI page.'
    Invoke-UI @('invoke','CheckCli') | Out-Null
    Assert-That (Wait-Value 'CliCheckSummary' 'モデル候補を確認しました' 35) 'Rechecking the restored fixture CLI failed.'
    $currentSubstep = 'select candidate model and reasoning effort'
    Select-Candidate 'WizardModelCandidates' 'Fixture Claude Large (fixture-claude-large)'
    Select-Candidate 'WizardReasoningEffort' 'high' -Prefix
    Assert-That ((Get-Value 'WizardModelCandidates') -like '*fixture-claude-large*') 'The CLI model candidate was not selected.'
    Assert-That ((Get-Value 'WizardReasoningEffort') -like '*high*') 'The CLI reasoning effort was not selected.'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[4\s*/\s*5\]') 'Successful CLI check did not advance to the chat page.'
    Invoke-UI @('invoke','OnboardingPrevious') | Out-Null
    Assert-That (Wait-Progress '\[3\s*/\s*5\]') 'Back did not return to the CLI page.'
    Assert-That ((Get-Value 'CliCheckSummary') -like '*候補を確認しました*') 'Returning to the CLI step lost its successful check.'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[4\s*/\s*5\]') 'Returning from the CLI page did not allow the checked setup to continue.'
    Capture '04-step-chat.png'
    Add-Pass 'CLI version and candidates are checked; fixture model and effort are selectable'

    $currentStep = 'page 4 chat connection and allowed users'
    $currentSubstep = 'verify missing Slack credentials block advancement'
    Select-Candidate 'WizardChatProvider' 'Slack'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 600
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[4\s*/\s*5\]') 'Missing Slack credentials advanced the wizard.'
    $currentSubstep = 'verify missing allowed user blocks Mattermost advancement'
    Select-Candidate 'WizardChatProvider' 'Mattermost'
    Set-Value 'WizardMattermostServerUrl' $ServerUrl
    Set-Value 'WizardMattermostChannelId' 'test-channel'
    Invoke-UI @('scroll-into-view','WizardMattermostToken') | Out-Null
    $passwordResult = Invoke-UiProcess @('set-value','WizardMattermostToken',$token)
    if ($passwordResult.ExitCode -ne 0) { throw 'winapp could not set the Mattermost PasswordBox value; UIA value support is required for this credential test.' }
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 600
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[4\s*/\s*5\]') 'Missing allowed user advanced the wizard.'
    $currentSubstep = 'set allowed user and advance'
    Set-Value 'WizardMattermostAllowedUserIds' 'test-member'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Assert-That (Wait-Progress '\[5\s*/\s*5\]') 'Valid Mattermost credentials and allowed user did not advance to safety setup.'
    Capture '05-step-safety.png'
    Add-Pass 'Slack and Mattermost validation block until required credentials and allowed user are entered'

    $currentStep = 'page 5 safety and connection'
    $currentSubstep = 'enable auto-connect and confirm conservative policy defaults'
    Invoke-UI @('scroll-into-view','WizardAutoConnect') | Out-Null
    Invoke-UI @('invoke','WizardAutoConnect') | Out-Null
    Assert-That ((Get-Value 'WizardAutoConnect') -match '^On$') 'Auto-connect toggle did not turn on.'
    Invoke-UI @('scroll-into-view','WizardChatApprovalMode') | Out-Null
    Assert-That ((Get-Value 'WizardChatApprovalMode') -match '毎回確認') 'Chat approval did not default to per-request confirmation.'
    Invoke-UI @('scroll-into-view','WizardChatResultMode') | Out-Null
    Assert-That ((Get-Value 'WizardChatResultMode') -match '手動で確認して共有') 'Chat result sharing did not default to manual review.'
    Invoke-UI @('scroll-into-view','WizardAgentPermissionMode') | Out-Null
    Assert-That ((Get-Value 'WizardAgentPermissionMode') -match 'CLIの既定値') 'Agent permissions did not default to the CLI setting.'
    $currentSubstep = 'reject completion while disconnected'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    Start-Sleep -Milliseconds 800
    Assert-That ((Get-Value 'OnboardingTitle') -match '\[5\s*/\s*5\]') 'Wizard completed without a chat connection.'
    $status = Get-Value 'WizardConnectionStatus'
    Assert-That ($status -notmatch '接続しました') 'Connection status unexpectedly says connected before the connect action.'
    $currentSubstep = 'connect to local fixture server'
    Invoke-UI @('scroll-into-view','WizardConnectMattermost') | Out-Null
    Invoke-UI @('invoke','WizardConnectMattermost') | Out-Null
    Assert-That (Wait-Value 'WizardConnectionStatus' '接続しました' 40) 'Mattermost did not connect to the provided fixture server.'
    Capture '06-step-connected.png'
    $currentSubstep = 'complete wizard and verify saved settings without printing credentials'
    Invoke-UI @('invoke','OnboardingNext') | Out-Null
    $settingsFile = Join-Path $dataPath 'settings.json'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $settingsFile) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Assert-That (Test-Path -LiteralPath $settingsFile) 'settings.json was not saved on completion.'
    $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    foreach ($pair in @(
        @('AgentProvider','claude'), @('AgentExecutable',$fixturePath), @('WorkspaceRoot',$workspace),
        @('AgentModel','fixture-claude-large'), @('AgentReasoningEffort','high'),
        @('MattermostServerUrl',$ServerUrl), @('MattermostChannelId','test-channel'),
        @('MattermostAllowedUserIds','test-member'), @('AutoConnectProvider','mattermost')
    )) { Assert-That ([string]$saved.($pair[0]) -eq [string]$pair[1]) "Saved settings field $($pair[0]) did not match expected value." }
    Assert-That (([string]$saved.MattermostToken).StartsWith('dpapi:') -and [string]$saved.MattermostToken -ne $token) 'Saved Mattermost token was absent or was not protected at rest.'
    $progressFile = Join-Path $dataPath 'onboarding.json'
    $completed = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (-not $completed -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $progressFile) {
            try { $completed = (Get-Content -LiteralPath $progressFile -Raw | ConvertFrom-Json).Status -eq 'completed' } catch { }
        }
        if (-not $completed) { Start-Sleep -Milliseconds 250 }
    }
    Assert-That $completed 'Onboarding completion status was not persisted before timeout.'
    Add-Pass 'connected wizard completes and persists selected settings and onboarding status'
}
catch {
    $detail = "Failed at line $([int]$_.InvocationInfo.ScriptLineNumber) during $currentStep / $currentSubstep. $($_.Exception.Message)"
    $results.Add([pscustomobject]@{ name=$currentStep; passed=$false; detail=$detail })
}
finally {
    $result = [ordered]@{
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        appPid = $AppPid
        dataDir = $dataPath
        checks = @($results)
        passed = (@($results | Where-Object { -not $_.passed }).Count -eq 0 -and $results.Count -gt 0)
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resultsPath) | Out-Null
    [IO.File]::WriteAllText($resultsPath,(ConvertTo-Json -InputObject $result -Depth 6),[Text.UTF8Encoding]::new($false))
}
Get-Content -LiteralPath $resultsPath -Raw
if (-not $result.passed) { exit 1 }
