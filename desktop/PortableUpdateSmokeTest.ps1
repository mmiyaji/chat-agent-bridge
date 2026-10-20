param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$WorkingDirectory = (Join-Path $PSScriptRoot 'artifacts')
)
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$workRoot = [IO.Path]::GetFullPath($WorkingDirectory)
if (-not $archive.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $archive.StartsWith([IO.Path]::TrimEndingDirectorySeparator($workRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a locally built archive under desktop/artifacts or the specified working directory.'
}
$testRoot = Join-Path $workRoot ('portable-update-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Message) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}
function Stop-TestApp($Process) {
    if ($Process.HasExited) { return }
    & winapp ui wait-for NavAbout -a $Process.Id -t 15000 --json | Out-Null
    if ($LASTEXITCODE) { throw 'Test app did not become ready.' }
    & winapp ui invoke NavAbout -a $Process.Id --json | Out-Null
    & winapp ui invoke AboutExit -a $Process.Id --json | Out-Null
    if ($LASTEXITCODE) { throw 'Test app could not exit normally.' }
    & winapp ui wait-for AboutExitConfirmation -a $Process.Id -t 5000 --json | Out-Null
    & winapp ui invoke PrimaryButton -a $Process.Id --json | Out-Null
    if ($LASTEXITCODE) { throw 'Exit confirmation failed.' }
    Wait-Until { $Process.HasExited } 15 'Test app did not exit.'
}
function Dismiss-HelperMessage($Helper) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Helper.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
        if ($button) { $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    }
}

# Use the production stager against the real packaged ZIP, independently of the desktop UI.
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

$results = [Collections.Generic.List[object]]::new()
try {
    foreach ($rollback in @($false, $true)) {
        $name = if ($rollback) { 'rollback' } else { 'success' }
        $case = Join-Path $testRoot $name
        $work = Join-Path $case 'work'
        $install = Join-Path $case 'install'
        $stage = Join-Path $work 'stage'
        New-Item -ItemType Directory -Path $work, $install | Out-Null
        & dotnet run --project (Join-Path $probe 'Probe.csproj') -- $archive $stage (Join-Path $work 'staged-files.json')
        if ($LASTEXITCODE) { throw 'Production archive staging failed.' }
        Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse }
        $data = if ($rollback) { Join-Path $install 'Data' } else { Join-Path $case 'custom-data' }
        New-Item -ItemType Directory -Path $data | Out-Null
        Write-Json (Join-Path $data 'settings.json') @{ SchemaVersion=1; WorkspaceRoot=$data; AgentExecutable='unused'; AgentProvider='custom'; AgentMode='exec'; AgentArguments=''; TrustedWorkspaceRoots=$data; AutoConnectProvider='none' }
        Write-Json (Join-Path $data 'onboarding.json') @{ SchemaVersion=3; Step=4; Status='completed' }
        $policy = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
        Write-Json (Join-Path $data 'consent.json') @{ PolicyVersion=$policy; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
        Write-Json (Join-Path $data 'pricing-auto.json') @{ schemaVersion=1; autoUpdateEnabled=$false; catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw) }
        [IO.File]::WriteAllText((Join-Path $data 'history-sentinel.txt'), 'private history remains')
        $settingsHash = (Get-FileHash (Join-Path $data 'settings.json')).Hash
        [IO.File]::WriteAllText((Join-Path $install 'old-managed.txt'), 'previous version')
        $oldManifest = Get-Content (Join-Path $install 'portable-files.json') -Raw | ConvertFrom-Json
        $oldManifest.files = @($oldManifest.files) + 'old-managed.txt'
        Write-Json (Join-Path $install 'portable-files.json') $oldManifest

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
        $metadata = Get-Content (Join-Path $stage 'portable-release.json') -Raw | ConvertFrom-Json
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe'))
        $start.UseShellExecute=$false
        $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
        $app = [Diagnostics.Process]::Start($start)
        & winapp ui wait-for NavAbout -a $app.Id -t 15000 --json | Out-Null
        if ($LASTEXITCODE) { throw 'Initial test app did not start.' }
        $nonce = [guid]::NewGuid().ToString('N')
        $planFile = Join-Path $work 'update-plan.json'
        Write-Json $planFile @{ SchemaVersion=1; InstallDirectory=$install; DataDirectory=$data; StagingDirectory=$stage; WorkDirectory=$work; ParentProcessId=$app.Id; ParentStartTimeUtcTicks=$app.StartTime.ToUniversalTime().Ticks; Files=$files; ReleaseVersion=$metadata.releaseVersion; Architecture=$metadata.architecture; Nonce=$nonce; Language='en' }
        $helperExe = Join-Path $work 'ChatAgentBridge.Updater.exe'
        Copy-Item -LiteralPath (Join-Path $install 'ChatAgentBridge.Updater.exe') -Destination $helperExe
        $helperStart = [Diagnostics.ProcessStartInfo]::new($helperExe)
        $helperStart.UseShellExecute=$false; $helperStart.CreateNoWindow=$true
        foreach ($argument in @('--apply',$planFile,'--nonce',$nonce)) { $helperStart.ArgumentList.Add($argument) }
        $helper = [Diagnostics.Process]::Start($helperStart)
        Wait-Until { (Test-Path (Join-Path $work ('helper-ready-'+$nonce))) -or $helper.HasExited } 20 'Helper preflight timed out.'
        Assert (-not $helper.HasExited) 'Helper rejected the test plan.'
        Stop-TestApp $app
        $resultFile = Join-Path $work 'update-result.json'
        Wait-Until { Test-Path -LiteralPath $resultFile } 65 'Updater did not finish.'
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        if ($rollback) { Dismiss-HelperMessage $helper }
        Wait-Until { $helper.HasExited } 15 'Helper did not exit.'
        Assert ($result.Success -eq (-not $rollback)) 'Unexpected update result.'
        Assert ($result.RolledBack -eq $rollback) 'Unexpected rollback result.'
        Assert ((Get-FileHash (Join-Path $data 'settings.json')).Hash -eq $settingsHash) 'Settings changed.'
        Assert ((Get-Content (Join-Path $data 'history-sentinel.txt') -Raw) -eq 'private history remains') 'History was lost.'
        Assert (Test-Path (Join-Path $work 'backup/old-managed.txt')) 'Previous managed file was not backed up.'
        Assert ((Test-Path (Join-Path $install 'old-managed.txt')) -eq $rollback) 'Managed files were not applied/restored.'
        $running = @(Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $install 'ChatAgentBridge.Desktop.exe') })
        Assert ($running.Count -eq 1) 'Exactly one updated/restored app must be running.'
        Stop-TestApp ([Diagnostics.Process]::GetProcessById($running[0].ProcessId))
        if (-not $rollback) { Assert (-not (Test-Path $stage)) 'Successful update retained the extracted payload.' }
        $results.Add(@{ case=$name; success=$true; dataPreserved=$true; restartConfirmed=$true })
        Write-Output "PASS $name : real helper, app restart, preserved data and managed-file handling"
    }
    Write-Json (Join-Path $testRoot 'results.json') $results.ToArray()
    Write-Output "Artifacts: $testRoot"
}
finally {
    # These executables were copied and launched only below this unique test root.
    Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('ChatAgentBridge.Desktop.exe','ChatAgentBridge.Updater.exe') -and
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
