param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$WorkingDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$OldArchivePath,
    [switch]$RollbackOnly,
    [ValidateSet('Ok', 'WindowClose')][string]$CloseAction = 'Ok',
    [ValidateSet('en', 'ja')][string]$Language = 'en',
    [switch]$KeepRunningOnFailure
)
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$workRoot = [IO.Path]::GetFullPath($WorkingDirectory)
function Resolve-TestArchive([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not $resolved.StartsWith($script:artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.StartsWith([IO.Path]::TrimEndingDirectorySeparator($script:workRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use locally built archives under desktop/artifacts or the specified working directory.'
    }
    return $resolved
}
$archive = Resolve-TestArchive $ArchivePath
$oldArchive = if ($OldArchivePath) { Resolve-TestArchive $OldArchivePath } else { $archive }
$testRoot = Join-Path $workRoot ('portable-update-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 15), [Text.UTF8Encoding]::new($false))
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Message) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}
function Get-AppElement([Diagnostics.Process]$Process, [string]$AutomationId) {
    if ($Process.HasExited) { return $null }
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $processCondition)
    foreach ($window in $windows) {
        try {
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
            $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($element) { return $element }
        } catch [Windows.Automation.ElementNotAvailableException] { }
    }
    return $null
}
function Wait-AppElement([Diagnostics.Process]$Process, [string]$AutomationId, [int]$Seconds, [string]$Message) {
    Wait-Until { return [bool](Get-AppElement $Process $AutomationId) } $Seconds $Message
    return Get-AppElement $Process $AutomationId
}
function Invoke-Element($Element, [string]$Message) {
    if (-not $Element) { throw $Message }
    try {
        $pattern = $null
        if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        elseif ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
        else { throw $Message }
    }
    catch { throw $Message }
}
function Stop-TestApp([Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return }
    Invoke-Element (Wait-AppElement $Process 'NavAbout' 20 'Test app did not become ready.') 'Test app navigation failed.'
    Invoke-Element (Wait-AppElement $Process 'AboutExit' 5 'About page exit control was not available.') 'Test app could not open exit confirmation.'
    Wait-AppElement $Process 'WindowCloseConfirmation' 5 'Exit confirmation did not appear.' | Out-Null
    Invoke-Element (Wait-AppElement $Process 'PrimaryButton' 5 'Exit confirmation button was not available.') 'Exit confirmation failed.'
    Wait-Until { $Process.Refresh(); return $Process.HasExited } 15 'Test app did not exit normally.'
}
function Get-HelperFailureDialog([Diagnostics.Process]$Helper) {
    if ($Helper.HasExited) { return $null }
    $output = & winapp ui list-windows -a $Helper.Id --json 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    try { $windows = @(($output -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop) } catch { return $null }
    foreach ($window in $windows) {
        if ([string]$window.className -ne '#32770' -or [long]$window.hwnd -le 0) { continue }
        try {
            $element = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]([long]$window.hwnd))
            if ($element.Current.ProcessId -eq $Helper.Id) { return $element }
        } catch [Windows.Automation.ElementNotAvailableException] { }
    }
    return $null
}
function Get-DialogText($Dialog) {
    $parts = [Collections.Generic.List[string]]::new()
    if ($Dialog.Current.Name) { $parts.Add([string]$Dialog.Current.Name) }
    $texts = $Dialog.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text))
    foreach ($text in $texts) { if ($text.Current.Name) { $parts.Add([string]$text.Current.Name) } }
    return $parts -join "`n"
}
function Capture-DialogScreenshot([long]$Hwnd, [string]$Path) {
    Assert ($Hwnd -gt 0) 'Refusing to capture a dialog with an invalid HWND.'
    $output = & winapp ui screenshot -w $Hwnd -o $Path 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -le 0) {
        throw "Could not capture the rollback failure dialog: $($output -join ' ')"
    }
}
function Dismiss-HelperMessage([Diagnostics.Process]$Helper, [string]$Method, [string]$ScreenshotPath, [string]$ExpectedText) {
    Wait-Until { return [bool](Get-HelperFailureDialog $Helper) } 15 'Rollback failure dialog (#32770) did not appear for the updater helper.'
    $dialog = Get-HelperFailureDialog $Helper
    $hwnd = [long]$dialog.Current.NativeWindowHandle
    Assert ($hwnd -gt 0) 'Rollback dialog has an invalid native HWND.'
    Assert ($dialog.Current.ProcessId -eq $Helper.Id) 'Rollback dialog belongs to a different process.'
    $dialogText = Get-DialogText $dialog
    Assert ($dialogText.Contains($ExpectedText, [StringComparison]::Ordinal)) 'Rollback dialog did not contain the localized previous-version restored message.'
    Capture-DialogScreenshot $hwnd $ScreenshotPath
    if ($Method -eq 'Ok') {
        $button = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, '2'))
        Assert ([bool]$button) 'Rollback dialog did not expose its native OK button (AutomationId 2).'
        Invoke-Element $button 'Could not invoke the rollback dialog OK button.'
    } else {
        $windowPattern = $null
        if (-not $dialog.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) {
            throw 'Rollback dialog did not expose its native titlebar close action.'
        }
        $windowPattern.Close()
    }
    return @{ hwnd=$hwnd; text=$dialogText; closeMethod=$Method; screenshot=$ScreenshotPath }
}

# Use the production stager against each real packaged ZIP, independently of the desktop UI.
$probe = Join-Path $testRoot 'probe'
New-Item -ItemType Directory -Path $probe | Out-Null
$coreProject = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'ChatAgentBridge.Core/ChatAgentBridge.Core.csproj'))
[IO.File]::WriteAllText((Join-Path $probe 'Probe.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="$coreProject" /></ItemGroup></Project>
"@)
[IO.File]::WriteAllText((Join-Path $probe 'Program.cs'), @'
using System.IO.Compression;
using System.Text.Json;
using ChatAgentBridge.Core.Updates;
using var zip = ZipFile.OpenRead(args[0]);
using var metadata = JsonDocument.Parse(zip.GetEntry("ChatAgentBridge.Portable/portable-release.json")!.Open());
var version = metadata.RootElement.GetProperty("releaseVersion").GetString()!;
var arch = metadata.RootElement.GetProperty("architecture").GetString()!;
var candidate = new PortableRelease(version, "v" + version, $"ChatAgentBridge.Portable-{version}-win-{arch}.zip", new Uri("https://github.com/mmiyaji/chat-agent-bridge"), "", 0, new Uri("https://github.com/mmiyaji/chat-agent-bridge"));
var stage = await PortableArchiveStager.ExtractAsync(args[0], args[1], candidate, default);
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(stage.Files));
'@)
function Stage-Archive([string]$Path, [string]$Destination, [string]$ManifestPath) {
    & dotnet run --project (Join-Path $probe 'Probe.csproj') -- $Path $Destination $ManifestPath
    if ($LASTEXITCODE) { throw "Production archive staging failed for '$Path'." }
    return (Get-Content -LiteralPath (Join-Path $Destination 'portable-release.json') -Raw | ConvertFrom-Json)
}

$results = [Collections.Generic.List[object]]::new()
$suitePassed = $false
$case = $null
$app = $null
$helper = $null
try {
    $cases = if ($RollbackOnly) { @($true) } else { @($false, $true) }
    foreach ($rollback in $cases) {
        $name = if ($rollback) { 'rollback' } else { 'success' }
        $case = Join-Path $testRoot $name
        $work = Join-Path $case 'work'
        $install = Join-Path $case 'install'
        $oldStage = Join-Path $work 'old-stage'
        $stage = Join-Path $work 'stage'
        New-Item -ItemType Directory -Path $work, $install | Out-Null
        $oldMetadata = Stage-Archive $oldArchive $oldStage (Join-Path $work 'old-staged-files.json')
        $metadata = Stage-Archive $archive $stage (Join-Path $work 'staged-files.json')
        Get-ChildItem -LiteralPath $oldStage -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse }
        $data = if ($rollback) { Join-Path $install 'Data' } else { Join-Path $case 'custom-data' }
        New-Item -ItemType Directory -Path $data | Out-Null
        Write-Json (Join-Path $data 'settings.json') @{ SchemaVersion=1; WorkspaceRoot=$data; AgentExecutable='unused'; AgentProvider='custom'; AgentMode='exec'; AgentArguments=''; TrustedWorkspaceRoots=$data; AutoConnectProvider='none' }
        Write-Json (Join-Path $data 'onboarding.json') @{ schemaVersion=3; step=4; status='completed' }
        $policy = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
        Write-Json (Join-Path $data 'consent.json') @{ PolicyVersion=$policy; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
        Write-Json (Join-Path $data 'pricing-auto.json') @{ schemaVersion=1; autoUpdateEnabled=$false; catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw) }
        [IO.File]::WriteAllText((Join-Path $data 'history-sentinel.txt'), 'private history remains')
        $settingsPath = Join-Path $data 'settings.json'
        $historyPath = Join-Path $data 'history-sentinel.txt'
        $settingsHash = (Get-FileHash -LiteralPath $settingsPath).Hash
        $historyHash = (Get-FileHash -LiteralPath $historyPath).Hash
        [IO.File]::WriteAllText((Join-Path $install 'old-managed.txt'), 'previous version')
        $oldManifest = Get-Content (Join-Path $install 'portable-files.json') -Raw | ConvertFrom-Json
        $oldManifest.files = @($oldManifest.files) + 'old-managed.txt'
        Write-Json (Join-Path $install 'portable-files.json') $oldManifest
        $originalVersion = [string]$oldMetadata.releaseVersion
        Assert (-not [string]::IsNullOrWhiteSpace($originalVersion)) 'Old archive did not provide a release version.'
        Assert ((Get-Content (Join-Path $install 'portable-release.json') -Raw | ConvertFrom-Json).releaseVersion -eq $originalVersion) 'Installed old archive version did not match its metadata.'

        if ($rollback) {
            # A correctly hashed release can still fail startup, e.g. invalid runtime configuration.
            [IO.File]::WriteAllText((Join-Path $stage 'ChatAgentBridge.Desktop.runtimeconfig.json'), '{}')
            $rows = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/')
                (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + ' *' + $relative
            })
            [IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), ($rows -join "`n") + "`n")
        }
        $files = @{}
        Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
            $files[[IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe'))
        $start.UseShellExecute=$false
        $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
        $app = [Diagnostics.Process]::Start($start)
        Wait-AppElement $app 'NavAbout' 30 'Initial test app did not start.' | Out-Null
        $nonce = [guid]::NewGuid().ToString('N')
        $planFile = Join-Path $work 'update-plan.json'
        Write-Json $planFile @{ SchemaVersion=1; InstallDirectory=$install; DataDirectory=$data; StagingDirectory=$stage; WorkDirectory=$work; ParentProcessId=$app.Id; ParentStartTimeUtcTicks=$app.StartTime.ToUniversalTime().Ticks; Files=$files; ReleaseVersion=$metadata.releaseVersion; Architecture=$metadata.architecture; Nonce=$nonce; Language=$Language }
        $helperExe = Join-Path $work 'ChatAgentBridge.Updater.exe'
        Copy-Item -LiteralPath (Join-Path $install 'ChatAgentBridge.Updater.exe') -Destination $helperExe
        $helperStart = [Diagnostics.ProcessStartInfo]::new($helperExe)
        $helperStart.UseShellExecute=$false; $helperStart.CreateNoWindow=$true
        foreach ($argument in @('--apply',$planFile,'--nonce',$nonce)) { $helperStart.ArgumentList.Add($argument) }
        $helper = [Diagnostics.Process]::Start($helperStart)
        Wait-Until { (Test-Path (Join-Path $work ('helper-ready-'+$nonce))) -or $helper.HasExited } 20 'Helper preflight timed out.'
        Assert (-not $helper.HasExited) 'Helper rejected the test plan.'
        Stop-TestApp $app
        $app.Dispose(); $app = $null
        $resultFile = Join-Path $work 'update-result.json'
        Wait-Until { Test-Path -LiteralPath $resultFile } 65 'Updater did not finish.'
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        $dialogResult = $null
        if ($rollback) {
            $expectedText = if ($Language -eq 'ja') { '以前の版に戻しました。' } else { 'The previous version has been restored.' }
            $dialogResult = Dismiss-HelperMessage $helper $CloseAction (Join-Path $work 'rollback-failure.png') $expectedText
        }
        Wait-Until { $helper.Refresh(); return $helper.HasExited } 15 'Helper did not exit after its failure dialog was closed.'
        Assert ($helper.ExitCode -eq $(if ($rollback) { 1 } else { 0 })) 'Updater helper returned an unexpected exit code.'
        Assert ($result.Success -eq (-not $rollback)) 'Unexpected update result.'
        Assert ($result.RolledBack -eq $rollback) 'Unexpected rollback result.'
        Assert ((Get-FileHash -LiteralPath $settingsPath).Hash -eq $settingsHash) 'Settings hash changed.'
        Assert ((Get-FileHash -LiteralPath $historyPath).Hash -eq $historyHash) 'History sentinel hash changed.'
        Assert (Test-Path (Join-Path $work 'backup/old-managed.txt')) 'Previous managed file was not backed up.'
        Assert ((Test-Path (Join-Path $install 'old-managed.txt')) -eq $rollback) 'Managed files were not applied/restored.'
        $running = @(Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $install 'ChatAgentBridge.Desktop.exe') })
        Assert ($running.Count -eq 1) 'Exactly one updated/restored app must be running.'
        $restoredProcess = [Diagnostics.Process]::GetProcessById($running[0].ProcessId)
        Wait-AppElement $restoredProcess 'NavAbout' 30 'Updated/restored app did not finish opening.' | Out-Null
        $actualVersion = [string](Get-Content (Join-Path $install 'portable-release.json') -Raw | ConvertFrom-Json).releaseVersion
        $expectedVersion = if ($rollback) { $originalVersion } else { [string]$metadata.releaseVersion }
        Assert ($actualVersion -eq $expectedVersion) "Installed version '$actualVersion' did not match expected version '$expectedVersion'."
        if ($rollback) { Assert ($actualVersion -eq $originalVersion) 'Rollback did not restore the pre-update version.' }
        Stop-TestApp $restoredProcess
        $restoredProcess.Dispose()
        if (-not $rollback) { Assert (-not (Test-Path $stage)) 'Successful update retained the extracted payload.' }
        $results.Add(@{
            case=$name; success=$true; expectedVersion=$expectedVersion; actualVersion=$actualVersion
            helperExitCode=$helper.ExitCode; closeMethod=$(if ($dialogResult) { $dialogResult.closeMethod } else { $null })
            dialogScreenshot=$(if ($dialogResult) { $dialogResult.screenshot } else { $null })
            dialogTextVerified=[bool]$dialogResult; settingsHashPreserved=$true; settingsSha256=$settingsHash
            historyHashPreserved=$true; historySha256=$historyHash; restartConfirmed=$true
        })
        Write-Output "PASS $name : real helper, localized failure dialog, app restart, preserved data and managed-file handling"
        $helper.Dispose(); $helper = $null
    }
    Write-Json (Join-Path $testRoot 'results.json') $results.ToArray()
    $suitePassed = $true
    Write-Output "Artifacts: $testRoot"
}
catch {
    Write-Json (Join-Path $testRoot 'results.json') @{ success=$false; failedCase=$(if ($case) { Split-Path $case -Leaf } else { $null }); error=$_.Exception.Message; completedCases=$results.ToArray() }
    if ($KeepRunningOnFailure) { Write-Output "Failure artifacts and any live test processes retained under: $testRoot" }
    throw
}
finally {
    # These executables were copied and launched only below this unique test root.
    if ($suitePassed -or -not $KeepRunningOnFailure) {
        Get-CimInstance Win32_Process | Where-Object {
            $_.Name -in @('ChatAgentBridge.Desktop.exe','ChatAgentBridge.Updater.exe') -and
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}
