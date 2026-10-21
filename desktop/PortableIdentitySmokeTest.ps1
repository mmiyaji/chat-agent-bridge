[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [string] $WorkingDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch] $RemovalOnly
)

# DEBUG ONLY: exercises existing Developer Mode registration, not production signed deployment.
# Never changes Developer Mode, certificate trust, or another installation's registration/processes.
$ErrorActionPreference = 'Stop'
$packageName = 'ChatAgentBridge.Portable'
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$workRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $archive.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $archive.StartsWith([IO.Path]::TrimEndingDirectorySeparator($workRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a locally built archive under desktop/artifacts or the selected working directory.'
}
$developerMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock').AllowDevelopmentWithoutDevLicense
if ($developerMode -ne 1) { throw 'Developer Mode is not already enabled. This debug-only test will not enable it.' }
if (@(Get-AppxPackage -Name $packageName).Count -ne 0) { throw 'A portable identity registration already exists; refusing to replace it.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this test without elevation.' }
$testRoot = Join-Path $workRoot ('portable-identity-smoke-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'install'
$identityLayout = Join-Path $testRoot 'debug-identity-layout'
$data = Join-Path $testRoot 'custom-data'
New-Item -ItemType Directory -Path $testRoot, $install, $identityLayout, $data | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PortableIdentitySmokeNative {
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr command, IntPtr parameter, uint flags, uint timeout, out IntPtr result);
    public static void DismissMessageBox(int processId) {
        EnumWindows((window, _) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != (uint)processId) return true;
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32770") {
                IntPtr result; SendMessageTimeout(window, 0x0111, new IntPtr(1), IntPtr.Zero, 2, 1000, out result);
            }
            return true;
        }, IntPtr.Zero);
    }
}
'@
$checks = [Collections.Generic.List[object]]::new()
$script:app = $null
$script:leafHash = $null
$trustBefore = $null
$script:identityHelperWork = $null

function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Write-Json([string] $Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false)) }
function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $Message) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $until)
    throw $Message
}
function Pass([string] $Name, $Details = $null) { $checks.Add(@{ name=$Name; status='PASS'; details=$Details }); Write-Output "PASS $Name" }
function Trust-Snapshot {
    $snapshot = [ordered]@{}
    foreach ($location in @('CurrentUser','LocalMachine')) {
        foreach ($name in @('TrustedPeople','Root')) {
            $store = [Security.Cryptography.X509Certificates.X509Store]::new($name, $location)
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
                $hashes = @($store.Certificates | ForEach-Object {
                    try { [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($_.RawData)) } finally { $_.Dispose() }
                } | Sort-Object)
                Assert (-not $script:leafHash -or $hashes -notcontains $script:leafHash) 'The test signing leaf is unexpectedly trusted.'
                $snapshot["$location/$name"] = $hashes
            }
            finally { $store.Dispose() }
        }
    }
    return ($snapshot | ConvertTo-Json -Compress -Depth 8)
}
function Own-Packages { return @(Get-AppxPackage -Name $packageName) }
function Assert-DebugRegistration {
    $packages = Own-Packages
    Assert ($packages.Count -eq 1) 'Expected exactly one debug identity package.'
    Assert ($packages[0].Publisher -ceq 'CN=mmiyaji' -and $packages[0].IsDevelopmentMode -eq $true -and
        [IO.Path]::GetFullPath($packages[0].InstallLocation).TrimEnd('\') -ieq [IO.Path]::GetFullPath($identityLayout).TrimEnd('\')) 'The identity package is not this test owned debug registration.'
    return $packages[0]
}
function Ui([string[]] $Arguments) {
    & winapp ui @Arguments --json | Out-Null
    if ($LASTEXITCODE) { throw "UI assertion/action failed: $($Arguments -join ' ')" }
}
function Element([string] $Id) {
    $script:app.Refresh()
    $root = [Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Expand-Group($Group) {
    $button = $Group.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
    foreach ($candidate in @($Group,$button)) {
        if ($null -eq $candidate) { continue }
        $pattern = $null
        if ($candidate.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$pattern)) {
            if ($pattern.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $pattern.Expand() }
            return
        }
        if ($candidate.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)) {
            if ($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) { $pattern.Toggle() }
            return
        }
        if ($candidate.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke(); return }
    }
    throw 'The settings expander has no supported expansion pattern.'
}
function Expand-Identity {
    Ui @('invoke','NavOverview','-a',"$($script:app.Id)")
    Ui @('wait-for','PortableIdentityExpander','-a',"$($script:app.Id)",'-t','10000')
    Ui @('scroll-into-view','PortableIdentityExpander','-a',"$($script:app.Id)")
    if ($null -eq (Element 'PortableIdentityConfigure')) {
        $expander = Element 'PortableIdentityExpander'
        Expand-Group $expander
    }
    Ui @('wait-for','PortableIdentityConfigure','-a',"$($script:app.Id)",'-t','10000')
    Ui @('scroll-into-view','PortableIdentityConfigure','-a',"$($script:app.Id)")
}
function Start-TestApp {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'ChatAgentBridge.Desktop.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=$install
    $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($data)
    [void]$start.Environment.Remove('CAB_PORTABLE_UPDATE_READY_EVENT')
    $script:app = [Diagnostics.Process]::Start($start)
    Ui @('wait-for','NavAbout','-a',"$($script:app.Id)",'-t','20000')
}
function Stop-TestApp {
    if ($null -eq $script:app -or $script:app.HasExited) { return }
    Assert ($script:app.Path -ieq (Join-Path $install 'ChatAgentBridge.Desktop.exe')) 'Refusing to stop an unrelated process.'
    Ui @('invoke','NavAbout','-a',"$($script:app.Id)")
    Ui @('invoke','AboutExit','-a',"$($script:app.Id)")
    Ui @('wait-for','WindowCloseConfirmation','-a',"$($script:app.Id)",'-t','5000')
    Ui @('invoke','PrimaryButton','-a',"$($script:app.Id)")
    Wait-Until { $script:app.HasExited } 15 'Owned test app did not exit.'
}
function Verify-Identity([string] $Label) {
    $runtime = & (Join-Path $PSScriptRoot 'test-support/NotificationNativeProbe/Get-ProbeProcessPackage.ps1') -ProcessId $script:app.Id | ConvertFrom-Json
    Assert ($runtime.hasPackageIdentity -and $runtime.packageFullName -ceq (Assert-DebugRegistration).PackageFullName) 'The actual app process did not obtain the owned package identity.'
    Verify-PortableClassification
    Pass $Label $runtime
}
function Verify-PortableClassification {
    Ui @('invoke','NavAbout','-a',"$($script:app.Id)")
    Ui @('wait-for','AboutTechnicalDetails','-a',"$($script:app.Id)",'-t','5000')
    if ($null -eq (Element 'AboutPackage')) {
        $group = Element 'AboutTechnicalDetails'
        Expand-Group $group
    }
    Ui @('wait-for','AboutPackage','-a',"$($script:app.Id)",'--value','Portable','-t','5000')
}
function Dismiss-OwnedHelper($Helper) {
    if ($Helper.HasExited) { return }
    Assert ($Helper.Path.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Refusing to dismiss an unrelated helper.'
    $filter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Helper.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $filter)
    foreach ($window in $windows) {
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
        if ($button) { $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    }
    # Win32 MessageBox windows are not always surfaced by UIA for a no-window helper.
    # Match only the already path-verified helper PID and standard dialog class; IDOK dismisses its error.
    [PortableIdentitySmokeNative]::DismissMessageBox($Helper.Id)
}

# A source-linked console invokes the production stager; no Windows registration APIs are in this probe.
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
var staged = await PortableArchiveStager.ExtractAsync(args[0], args[1], candidate, default);
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(staged.Files));
'@)

try {
    $initialStage = Join-Path $testRoot 'initial-stage'
    & dotnet run --project (Join-Path $probe 'Probe.csproj') -- $archive $initialStage (Join-Path $testRoot 'initial-files.json')
    if ($LASTEXITCODE) { throw 'Production archive staging failed.' }
    Get-ChildItem -LiteralPath $initialStage -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse }
    $metadata = Get-Content (Join-Path $install 'Identity/portable-identity.json') -Raw | ConvertFrom-Json
    $script:leafHash = $metadata.certificateSha256
    $trustBefore = Trust-Snapshot
    Write-Json (Join-Path $testRoot 'baseline.json') @{ debugOnly=$true; productionSignedRegistrationVerified=$false; developerMode=$developerMode; trust=$trustBefore }
    $negativeProbe = Join-Path $testRoot 'negative-registration-probe'
    New-Item -ItemType Directory -Path $negativeProbe | Out-Null
    $registrationSource = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/Deployment/PortableIdentityRegistration.cs'))
    [IO.File]::WriteAllText((Join-Path $negativeProbe 'Negative.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.26100.0</TargetFramework><SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$coreProject" /><Compile Include="$registrationSource" Link="PortableIdentityRegistration.cs" /></ItemGroup></Project>
"@)
    [IO.File]::WriteAllText((Join-Path $negativeProbe 'Program.cs'), @'
using System.Runtime.InteropServices;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;
using ChatAgentBridge_Desktop.Deployment;
var bundle = PortableIdentityBundle.LoadValidated(args[0]);
var info = await PortableIdentityRegistration.InspectAsync(args[0], bundle);
if (info.Active || info.RegisteredLocation is not null || info.CertificateTrusted) return 2;
try { await PortableIdentityRegistration.RegisterAsync(args[0], bundle, trustCertificate: false, allowRelocate: false); return 3; }
catch (COMException error) when (error.HResult == unchecked((int)0x800B0109))
{
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { info.Active, info.RegisteredLocation, info.CertificateTrusted, RegistrationHResult = $"0x{error.HResult:X8}", TrustRequested = false }));
    return 0;
}
'@)
    & dotnet run --project (Join-Path $negativeProbe 'Negative.csproj') -- $install (Join-Path $testRoot 'negative-registration.json')
    if ($LASTEXITCODE) { throw 'The no-trust registration guard probe failed.' }
    Assert ((Own-Packages).Count -eq 0 -and (Trust-Snapshot) -ceq $trustBefore) 'The negative registration probe changed OS package or trust state.'
    Pass 'Production registration with trust=false rejects untrusted signer as 0x800B0109'
    Write-Json (Join-Path $data 'settings.json') @{ SchemaVersion=1; WorkspaceRoot=$data; AgentExecutable='unused'; AgentProvider='custom'; AgentMode='exec'; AgentArguments=''; TrustedWorkspaceRoots=$data; AutoConnectProvider='none' }
    Write-Json (Join-Path $data 'agent-connections.json') @{ SchemaVersion=1; Profiles=@() }
    Write-Json (Join-Path $data 'onboarding.json') @{ schemaVersion=3; step=4; status='completed' }
    $policy = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw), 'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Write-Json (Join-Path $data 'consent.json') @{ PolicyVersion=$policy; AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-Json (Join-Path $data 'language.json') @{ Language='en' }
    Write-Json (Join-Path $data 'pricing-auto.json') @{ schemaVersion=1; autoUpdateEnabled=$false; catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw) }
    [IO.File]::WriteAllText((Join-Path $data 'history-sentinel.txt'), 'private history remains')
    $settingsHash = (Get-FileHash (Join-Path $data 'settings.json')).Hash
    Write-Json (Join-Path $testRoot 'data-baseline.json') @{ settingsSha256=$settingsHash; historySha256=(Get-FileHash (Join-Path $data 'history-sentinel.txt')).Hash }
    Start-TestApp
    Expand-Identity
    Ui @('wait-for','PortableIdentityConfigure','-a',"$($script:app.Id)",'-p','IsEnabled','--value','True','-t','10000')
    Ui @('invoke','PortableIdentityConfigure','-a',"$($script:app.Id)")
    Ui @('wait-for','PortableIdentityConfigureConfirmation','-a',"$($script:app.Id)",'-t','5000')
    Ui @('wait-for','PortableIdentityTrustConsent','-a',"$($script:app.Id)",'--value','Off','-t','5000')
    Ui @('wait-for','PrimaryButton','-a',"$($script:app.Id)",'-p','IsEnabled','--value','False','-t','5000')
    Ui @('screenshot','-a',"$($script:app.Id)",'-o',(Join-Path $testRoot 'configure-unchecked.png'))
    Ui @('invoke','CloseButton','-a',"$($script:app.Id)")
    Assert ((Own-Packages).Count -eq 0) 'Canceling configure changed package registration.'
    Assert ((Trust-Snapshot) -ceq $trustBefore) 'Canceling configure changed certificate trust.'
    Pass 'Configure cancellation preserves OS state; trust remains unchecked'
    Stop-TestApp
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable-identity/Package.manifest.xml') -Destination (Join-Path $identityLayout 'AppxManifest.xml')
    Add-AppxPackage -Register (Join-Path $identityLayout 'AppxManifest.xml') -ExternalLocation $install -ErrorAction Stop
    $registered = Assert-DebugRegistration
    Pass 'DEBUG ONLY development registration' @{ fullName=$registered.PackageFullName; installLocation=$registered.InstallLocation; externalLocation=$install }
    Start-TestApp
    Verify-Identity 'Debug identity reaches actual portable process; classification remains Portable'
    Expand-Identity
    Ui @('wait-for','PortableIdentityConfigure','-a',"$($script:app.Id)",'-p','IsEnabled','--value','False','-t','10000')
    Ui @('wait-for','PortableIdentityRemove','-a',"$($script:app.Id)",'-p','IsEnabled','--value','True','-t','10000')
    Ui @('invoke','PortableIdentityRemove','-a',"$($script:app.Id)")
    Ui @('wait-for','PortableIdentityRemoveConfirmation','-a',"$($script:app.Id)",'-t','5000')
    Ui @('screenshot','-a',"$($script:app.Id)",'-o',(Join-Path $testRoot 'remove-debug-confirmation.png'))
    Ui @('invoke','CloseButton','-a',"$($script:app.Id)")
    $null = Assert-DebugRegistration
    Assert ((Trust-Snapshot) -ceq $trustBefore) 'Canceling removal changed certificate trust.'
    Ui @('screenshot','-a',"$($script:app.Id)",'-o',(Join-Path $testRoot 'debug-active.png'))
    Pass 'Active configure disabled, removal enabled; cancel removal preserves registration'

    $updateCases = if ($RemovalOnly) { @() } else { @($false,$true) }
    foreach ($rollback in $updateCases) {
        $name = if ($rollback) { 'rollback' } else { 'success' }
        $work = Join-Path $testRoot ($name + '-work')
        $stage = Join-Path $work 'stage'
        New-Item -ItemType Directory -Path $work | Out-Null
        & dotnet run --project (Join-Path $probe 'Probe.csproj') -- $archive $stage (Join-Path $work 'staged-files.json')
        if ($LASTEXITCODE) { throw 'Production archive staging failed.' }
        [IO.File]::WriteAllText((Join-Path $install 'old-managed.txt'), 'previous version')
        $oldManifest = Get-Content (Join-Path $install 'portable-files.json') -Raw | ConvertFrom-Json
        $oldManifest.files = @($oldManifest.files | Where-Object { $_ -ne 'old-managed.txt' }) + 'old-managed.txt'
        Write-Json (Join-Path $install 'portable-files.json') $oldManifest
        if ($rollback) { [IO.File]::WriteAllText((Join-Path $stage 'ChatAgentBridge.Desktop.runtimeconfig.json'), '{}') }
        $rows = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + ' *' + [IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
        })
        [IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), ($rows -join "`n") + "`n")
        $files = @{}
        Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $files[[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
        $release = Get-Content (Join-Path $stage 'portable-release.json') -Raw | ConvertFrom-Json
        $nonce = [guid]::NewGuid().ToString('N')
        $planFile = Join-Path $work 'update-plan.json'
        Write-Json $planFile @{ SchemaVersion=1; InstallDirectory=$install; DataDirectory=$data; StagingDirectory=$stage; WorkDirectory=$work; ParentProcessId=$script:app.Id; ParentStartTimeUtcTicks=$script:app.StartTime.ToUniversalTime().Ticks; Files=$files; ReleaseVersion=$release.releaseVersion; Architecture=$release.architecture; Nonce=$nonce; Language='en' }
        $helperExe = Join-Path $work 'ChatAgentBridge.Updater.exe'
        Copy-Item -LiteralPath (Join-Path $install 'ChatAgentBridge.Updater.exe') -Destination $helperExe
        $start = [Diagnostics.ProcessStartInfo]::new($helperExe)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$work
        foreach ($argument in @('--apply',$planFile,'--nonce',$nonce)) { $start.ArgumentList.Add($argument) }
        $helper = [Diagnostics.Process]::Start($start)
        Wait-Until { (Test-Path (Join-Path $work ('helper-ready-'+$nonce))) -or $helper.HasExited } 25 'Helper preflight timed out.'
        Assert (-not $helper.HasExited) 'Helper rejected the identity-bearing update.'
        Stop-TestApp
        $resultFile = Join-Path $work 'update-result.json'
        Wait-Until { Test-Path -LiteralPath $resultFile } 75 'Updater did not finish.'
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        if (-not $rollback) { Wait-Until { $helper.HasExited } 15 'Owned update helper did not exit.' }
        Assert ($result.Success -eq (-not $rollback) -and $result.RolledBack -eq $rollback) 'Unexpected update/rollback result.'
        Assert ((Get-FileHash (Join-Path $data 'settings.json')).Hash -ceq $settingsHash) 'Settings changed.'
        Assert ((Get-Content (Join-Path $data 'history-sentinel.txt') -Raw) -ceq 'private history remains') 'History changed.'
        Assert ((Test-Path (Join-Path $install 'old-managed.txt')) -eq $rollback) 'Managed files were not applied or restored.'
        $running = @(Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.ExecutablePath -ieq (Join-Path $install 'ChatAgentBridge.Desktop.exe') })
        Assert ($running.Count -eq 1) 'Expected exactly one owned updated/restored process.'
        $script:app = [Diagnostics.Process]::GetProcessById($running[0].ProcessId)
        Ui @('wait-for','NavAbout','-a',"$($script:app.Id)",'-t','20000')
        Verify-Identity "$name restart retains debug identity and Portable classification"
        Assert ((Trust-Snapshot) -ceq $trustBefore) 'The update changed certificate trust.'
        Pass "$name updater preserves custom data and external registration" $result
        if ($rollback) {
            # Verify recovery before dismissing the expected blocking failure MessageBox.
            try { Wait-Until { Dismiss-OwnedHelper $helper; return $helper.HasExited } 10 'Owned rollback failure MessageBox remained open.' }
            catch {
                Assert ($helper.Path -ieq $helperExe) 'Refusing to stop an unrelated rollback helper.'
                Stop-Process -Id $helper.Id -Force -ErrorAction Stop
                Pass 'Verified rollback; stopped only owned helper after its expected blocking error dialog' @{ nativeDialogAutomation=$false }
            }
        }
    }
    # Exercise the real coordinator -> copied helper -> safe parent exit handshake without adding trust.
    Expand-Identity
    Ui @('wait-for','PortableIdentityRemove','-a',"$($script:app.Id)",'-p','IsEnabled','--value','True','-t','10000')
    Ui @('invoke','PortableIdentityRemove','-a',"$($script:app.Id)")
    Ui @('wait-for','PortableIdentityRemoveConfirmation','-a',"$($script:app.Id)",'-t','5000')
    $removalParentId = $script:app.Id
    $removalParentTicks = $script:app.StartTime.ToUniversalTime().Ticks
    Ui @('invoke','PrimaryButton','-a',"$($script:app.Id)")
    $setupRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ChatAgentBridge/notification-setup'
    Wait-Until {
        foreach ($directory in Get-ChildItem -LiteralPath $setupRoot -Directory) {
            $planPath = Join-Path $directory.FullName 'identity-plan.json'
            if (-not (Test-Path -LiteralPath $planPath)) { continue }
            try { $candidate = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json } catch { continue }
            if ($candidate.InstallDirectory -ieq $install -and $candidate.DataDirectory -ieq $data -and
                $candidate.ParentProcessId -eq $removalParentId -and $candidate.ParentStartTimeUtcTicks -eq $removalParentTicks) {
                Assert ($candidate.Operation -ceq 'unregister' -and -not $candidate.TrustCertificate) 'Unexpected identity setup operation or trust request.'
                $script:identityHelperWork = $directory.FullName
                return $true
            }
        }
        return $false
    } 20 'The coordinator did not create the owned removal plan.'
    Wait-Until { $script:app.HasExited } 30 'The coordinator did not safely close its parent process.'
    $removalResultPath = Join-Path $data 'portable-notification-result.json'
    Wait-Until { Test-Path -LiteralPath $removalResultPath } 45 'The identity helper did not report removal completion.'
    $removalResult = Get-Content -LiteralPath $removalResultPath -Raw | ConvertFrom-Json
    Assert ($removalResult.Success -eq $true -and $removalResult.Operation -ceq 'unregister') 'The actual identity helper removal failed.'
    Assert ((Own-Packages).Count -eq 0) 'The actual helper did not remove its package.'
    Wait-Until {
        $script:restarted = @(Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'" | Where-Object { $_.ExecutablePath -ieq (Join-Path $install 'ChatAgentBridge.Desktop.exe') })
        return $script:restarted.Count -eq 1
    } 20 'The identity helper did not restart the same installation.'
    $script:app = [Diagnostics.Process]::GetProcessById($script:restarted[0].ProcessId)
    Ui @('wait-for','NavAbout','-a',"$($script:app.Id)",'-t','20000')
    $afterRemoval = & (Join-Path $PSScriptRoot 'test-support/NotificationNativeProbe/Get-ProbeProcessPackage.ps1') -ProcessId $script:app.Id | ConvertFrom-Json
    Assert (-not $afterRemoval.hasPackageIdentity) 'The restarted process retained removed identity.'
    Verify-PortableClassification
    Assert ((Get-FileHash (Join-Path $data 'settings.json')).Hash -ceq $settingsHash) 'Removal changed settings.'
    Assert ((Get-Content (Join-Path $data 'history-sentinel.txt') -Raw) -ceq 'private history remains') 'Removal changed history.'
    Assert ((Trust-Snapshot) -ceq $trustBefore) 'Removal changed certificate trust.'
    Pass 'Real removal coordinator/helper safely unregisters and restarts same portable app/data' $removalResult
    Stop-TestApp
}
catch {
    $checks.Add(@{ name='smoke'; status='FAIL'; error=$_.Exception.GetType().Name; detail=$_.Exception.Message })
    throw
}
finally {
    Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('ChatAgentBridge.Desktop.exe','ChatAgentBridge.Updater.exe') -and $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($script:identityHelperWork) {
        $ownedIdentityHelper = Join-Path $script:identityHelperWork 'ChatAgentBridge.Updater.exe'
        Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Updater.exe'" | Where-Object { $_.ExecutablePath -ieq $ownedIdentityHelper } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    foreach ($package in (Own-Packages)) {
        Assert ($package.Publisher -ceq 'CN=mmiyaji' -and $package.IsDevelopmentMode -eq $true -and
            [IO.Path]::GetFullPath($package.InstallLocation).TrimEnd('\') -ieq [IO.Path]::GetFullPath($identityLayout).TrimEnd('\')) 'Refusing to remove an identity package not owned by this test.'
        Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
    }
    $trustAfter = Trust-Snapshot
    Assert (-not $trustBefore -or $trustBefore -ceq $trustAfter) 'Certificate trust changed during the test.'
    Assert ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock').AllowDevelopmentWithoutDevLicense -eq $developerMode) 'Developer Mode changed.'
    Assert ((Own-Packages).Count -eq 0) 'The owned debug package was not removed.'
    Write-Json (Join-Path $testRoot 'results.json') @{ debugOnly=$true; productionSignedRegistrationVerified=$false; checks=$checks.ToArray(); trustUnchanged=($trustBefore -ceq $trustAfter); remainingRegistrations=(Own-Packages).Count }
    Write-Output "Artifacts: $testRoot"
}
