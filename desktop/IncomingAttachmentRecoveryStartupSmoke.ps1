#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $PortableAppDirectory = (Join-Path $PSScriptRoot 'artifacts/attachment-recovery-verified'),
    [switch] $SkipBuild,
    [int] $StartupTimeoutSeconds = 30
)

# Cross-process startup smoke for incoming attachment recovery. All fixtures,
# logs, and generated binaries live below one GUID directory in ignored artifacts.
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profileRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot ([guid]::NewGuid().ToString('D'))))
$resultsPath = Join-Path $profileRoot 'results.json'
$checks = [Collections.Generic.List[object]]::new()
$ownedProcesses = [Collections.Generic.List[object]]::new()
$script:helperPath = $null
$script:root = $profileRoot

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Pass([string] $Name, $Details = $null) {
    $checks.Add(@{ name = $Name; status = 'PASS'; details = $Details })
    Write-Output "PASS $Name"
}

function Save-Results([bool] $Passed, [string] $ErrorMessage = '') {
    $payload = @{
        passed = $Passed
        error = $ErrorMessage
        checks = $checks.ToArray()
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        profileRoot = $profileRoot
        fixture = 'isolated Core attachment helper and portable WinUI startup; no external chat, AI service, or credentials'
    }
    [IO.File]::WriteAllText($resultsPath, ($payload | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $Message) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $until)
    throw $Message
}

function Quote-WindowsArgument([string] $Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Start-TrackedProcess([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory, [string] $Stdout, [string] $Stderr) {
    $quotedArguments = @($Arguments | ForEach-Object { Quote-WindowsArgument $_ })
    $process = Start-Process -FilePath $FilePath -ArgumentList $quotedArguments -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden -RedirectStandardOutput $Stdout -RedirectStandardError $Stderr -PassThru
    $ownedProcesses.Add(@{ process = $process; startTime = $process.StartTime.ToUniversalTime().Ticks; label = $FilePath })
    return $process
}

function Stop-OwnedProcess($Process, [long] $ExpectedStartTimeTicks, [string] $Description) {
    if ($null -eq $Process) { return }
    try {
        $current = Get-Process -Id $Process.Id -ErrorAction Stop
        if ($current.StartTime.ToUniversalTime().Ticks -ne $ExpectedStartTimeTicks) {
            throw "Refusing to stop $Description because its process ID was reused."
        }
        if (-not $current.HasExited) {
            $current.Kill($true)
            if (-not $current.WaitForExit(5000)) { throw "Timed out stopping owned $Description." }
        }
    }
    catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
    catch [InvalidOperationException] { }
}

function Start-PortableApp([string] $DataDirectory, [string] $Label) {
    $exe = Join-Path $PortableAppDirectory 'ChatAgentBridge.Desktop.exe'
    Assert (Test-Path -LiteralPath $exe -PathType Leaf) "Portable executable missing: $exe"
    $args = @('--data-dir', $DataDirectory)
    $stdout = Join-Path $profileRoot "$Label.stdout.log"
    $stderr = Join-Path $profileRoot "$Label.stderr.log"
    $app = Start-TrackedProcess $exe $args $PortableAppDirectory $stdout $stderr
    return $app
}

function Capture-AppScreenshot($App, [string] $Name) {
    $path = Join-Path $profileRoot "$Name.png"
    & winapp ui screenshot -a $App.Id -o $path 2>&1 | Out-File -LiteralPath (Join-Path $profileRoot "$Name-screenshot.log") -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Could not capture startup screenshot for app PID $($App.Id). See $Name-screenshot.log."
    }
    return $path
}

function Assert-OwnHelperProcess([int] $ProcessId, [long] $StartTimeTicks) {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    Assert ($process.StartTime.ToUniversalTime().Ticks -eq $StartTimeTicks) 'Synthetic worker process identity changed; refusing to terminate it.'
    Assert (-not $process.HasExited) 'Synthetic worker did not survive its owner process crash.'
    $command = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    Assert ($command.CommandLine -and $command.CommandLine.Contains($script:helperPath, [StringComparison]::OrdinalIgnoreCase)) `
        'Worker command line does not point to this smoke helper; refusing to terminate it.'
    return $process
}

function Invoke-Helper([string[]] $Arguments, [string] $Name) {
    $out = Join-Path $profileRoot "$Name.stdout.log"
    $err = Join-Path $profileRoot "$Name.stderr.log"
    Start-TrackedProcess 'dotnet' (@($script:helperPath) + $Arguments) $profileRoot $out $err
}

function Wait-Ready([string] $Path, $Process, [string] $Description) {
    Wait-Until {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return $true }
        try { $Process.Refresh(); return $Process.HasExited } catch { return $true }
    } 20 "$Description did not become ready within 20 seconds."
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $logs = @(Get-ChildItem -LiteralPath $profileRoot -Filter '*.log' -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        throw "$Description exited before readiness. Logs: $logs"
    }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Kill-OwnerAbruptly($Owner, [string] $Description) {
    $ownerInfo = Get-Process -Id $Owner.Id -ErrorAction Stop
    Assert ($ownerInfo.StartTime.ToUniversalTime().Ticks -eq $Owner.StartTime.ToUniversalTime().Ticks) "Unexpected process identity for $Description."
    $ownerInfo.Kill()
    Assert ($ownerInfo.WaitForExit(5000)) "Timed out hard-killing $Description."
}

try {
    Assert ($StartupTimeoutSeconds -ge 5 -and $StartupTimeoutSeconds -le 120) 'StartupTimeoutSeconds must be between 5 and 120.'
    Assert ($PortableAppDirectory -and [IO.Path]::IsPathFullyQualified($PortableAppDirectory)) 'PortableAppDirectory must be absolute.'
    $PortableAppDirectory = [IO.Path]::GetFullPath($PortableAppDirectory)
    Assert ($PortableAppDirectory.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) `
        'PortableAppDirectory must be under desktop/artifacts.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $PortableAppDirectory 'AppxManifest.xml'))) 'Use the unpackaged portable publish.'
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null

    if (-not $SkipBuild) {
        New-Item -ItemType Directory -Force -Path $PortableAppDirectory | Out-Null
        $desktopProject = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj'
        & dotnet publish $desktopProject -c Release -p:Platform=x64 -r win-x64 --self-contained true `
            -p:WindowsAppSDKSelfContained=true -p:CABPortable=true -o $PortableAppDirectory
        if ($LASTEXITCODE -ne 0) { throw "Portable desktop publish failed with exit code $LASTEXITCODE." }
    }

    $helperDirectory = Join-Path $profileRoot 'helper'
    New-Item -ItemType Directory -Force -Path $helperDirectory | Out-Null
    $helperProject = Join-Path $helperDirectory 'RecoverySmokeHelper.csproj'
    $coreProject = [IO.Path]::GetRelativePath($helperDirectory, (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/ChatAgentBridge.Core.csproj'))
    $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$coreProject" /></ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($helperProject, $projectXml, [Text.UTF8Encoding]::new($false))
    $helperSource = @'
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core;

internal static class Program
{
    private static Task WriteJson(string path, object value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value), new UTF8Encoding(false));

    private static Task WaitForever() => Task.Delay(Timeout.InfiniteTimeSpan);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1) return 2;
        if (args[0] == "--worker")
        {
            await WaitForever();
            return 0;
        }

        if (args.Length != 4 || (args[0] != "--owner" && args[0] != "--orphan")) return 2;
        var mode = args[0];
        var workspace = Path.GetFullPath(args[1]);
        var data = Path.GetFullPath(args[2]);
        var readyPath = Path.GetFullPath(args[3]);
        var descriptors = mode == "--owner"
            ? new[] {
                new IncomingAttachmentDescriptor("known-delete", "original.txt", 29, "text/plain"),
                new IncomingAttachmentDescriptor("known-modified", "modified.txt", 31, "text/plain")
              }
            : new[] { new IncomingAttachmentDescriptor("canary", "canary.txt", 23, "text/plain") };
        var registry = Path.Combine(data, "incoming-attachments");
        var existingManifestIds = Directory.Exists(registry)
            ? Directory.GetFiles(registry, "*.json", SearchOption.TopDirectoryOnly).Select(path => Path.GetFileNameWithoutExtension(path)!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lease = await IncomingAttachmentStager.PrepareAsync(workspace, descriptors,
            async (descriptor, stream, maxBytes, token) => {
                var bytes = Encoding.UTF8.GetBytes($"fixture:{descriptor.Id}:payload\n");
                if (bytes.Length > maxBytes) throw new InvalidDataException("Fixture exceeded its declared size.");
                await stream.WriteAsync(bytes, token);
            }, recoveryDataDirectory: data);
        using (lease)
        {
            var paths = lease.Attachments.Select(item => item.Path).ToArray();
            Process? worker = null;
            var workerStartTimeUtcTicks = 0L;
            if (mode == "--owner")
            {
                using (lease.BeginExecutionScope())
                {
                    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
                    start.ArgumentList.Add("--worker");
                    worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic worker.");
                    workerStartTimeUtcTicks = worker.StartTime.ToUniversalTime().Ticks;
                    IncomingAttachmentRecovery.TrackProcess(worker);
                }
            }
            var manifests = Directory.GetFiles(registry, "*.json", SearchOption.TopDirectoryOnly);
            var manifestId = manifests.Select(path => Path.GetFileNameWithoutExtension(path)!)
                .FirstOrDefault(name => !existingManifestIds.Contains(name) && Guid.TryParseExact(name, "N", out _));
            await WriteJson(readyPath, new {
                mode,
                workspace,
                dataDirectory = data,
                stagingDirectory = Path.GetDirectoryName(paths[0]),
                files = paths,
                manifestId,
                workerProcessId = worker?.Id,
                workerStartTimeUtcTicks
            });
            if (worker is not null) await worker.WaitForExitAsync(); else await WaitForever();
        }
        return 0;
    }
}
'@
    [IO.File]::WriteAllText((Join-Path $helperDirectory 'Program.cs'), $helperSource, [Text.UTF8Encoding]::new($false))
    $helperOutputDirectory = Join-Path $helperDirectory 'output'
    & dotnet build $helperProject -c Release -o $helperOutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Recovery smoke helper build failed with exit code $LASTEXITCODE." }
    $script:helperPath = Join-Path $helperOutputDirectory 'RecoverySmokeHelper.dll'
    Assert (Test-Path -LiteralPath $script:helperPath -PathType Leaf) 'Built recovery helper DLL is missing.'
    Pass 'Built isolated .NET helper referencing Core' $script:helperPath

    $dataDirectory = Join-Path $profileRoot 'data'
    $workspace = Join-Path $profileRoot 'workspace'
    New-Item -ItemType Directory -Force -Path $dataDirectory, $workspace | Out-Null
    $recoveryRegistry = Join-Path $dataDirectory 'incoming-attachments'
    $outsideSentinel = Join-Path $profileRoot 'outside-staging-preserve.txt'
    [IO.File]::WriteAllText($outsideSentinel, 'outside registered staging')
    $lookalike = Join-Path $workspace ('.cab-incoming-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $lookalike | Out-Null
    $lookalikeSentinel = Join-Path $lookalike 'unregistered.txt'
    [IO.File]::WriteAllText($lookalikeSentinel, 'unregistered staging-like directory')

    $ownerReady = Join-Path $profileRoot 'owner.ready.json'
    $owner = Invoke-Helper @('--owner', $workspace, $dataDirectory, $ownerReady) 'owner'
    $ownerReadyInfo = Wait-Ready $ownerReady $owner 'Owner helper'
    $originalPath = [string]$ownerReadyInfo.files[0]
    $modifiedPath = [string]$ownerReadyInfo.files[1]
    Assert (Test-Path -LiteralPath $originalPath -PathType Leaf) 'Owner did not stage its first registered file.'
    $originalHash = (Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash
    $manifestId = [string]$ownerReadyInfo.manifestId
    Assert ($manifestId -match '^[0-9a-f]{32}$') 'Owner did not report its registration ID.'
    Wait-Until { @(Get-ChildItem -LiteralPath $recoveryRegistry -Filter "$manifestId.json" -ErrorAction SilentlyContinue).Count -eq 1 } 10 'Owner registration manifest was not persisted.'
    Pass 'Owner staged registered files and tracked a live worker' @{ manifestId = $manifestId; sha256 = $originalHash }

    $orphanReady = Join-Path $profileRoot 'orphan.ready.json'
    $orphan = Invoke-Helper @('--orphan', $workspace, $dataDirectory, $orphanReady) 'orphan'
    $orphanInfo = Wait-Ready $orphanReady $orphan 'Orphan helper'
    Kill-OwnerAbruptly $orphan 'unowned-registration helper'
    $orphanCanary = [string]$orphanInfo.files[0]
    Assert (Test-Path -LiteralPath $orphanCanary -PathType Leaf) 'The orphan canary was not staged before owner termination.'
    Pass 'Created an orphan registration by hard-killing its owner' @{ file = $orphanCanary; manifestId = $orphanInfo.manifestId }

    # Model an application-owned unknown file and a changed registered file.
    $unknownInStaging = Join-Path ([string]$ownerReadyInfo.stagingDirectory) 'unknown-extra.txt'
    [IO.File]::WriteAllText($unknownInStaging, 'must remain untouched')
    [IO.File]::WriteAllText($modifiedPath, 'changed after registration; preserve')
    $modifiedHash = (Get-FileHash -LiteralPath $modifiedPath -Algorithm SHA256).Hash
    Kill-OwnerAbruptly $owner 'active-worker registration helper'
    $workerProcess = Assert-OwnHelperProcess ([int]$ownerReadyInfo.workerProcessId) ([long]$ownerReadyInfo.workerStartTimeUtcTicks)

    $appOne = Start-PortableApp $dataDirectory 'startup-one'
    $appOneStartTicks = $appOne.StartTime.ToUniversalTime().Ticks
    $ownedProcesses.Add(@{ process = $appOne; startTime = $appOneStartTicks; label = 'portable app startup one' })
    Wait-Until {
        try { $appOne.Refresh(); return (-not $appOne.HasExited) -and (Test-Path -LiteralPath $originalPath -PathType Leaf) } catch { return $false }
    } 20 'Portable app did not remain running with the active worker file present.'
    Wait-Until { -not (Test-Path -LiteralPath $orphanCanary) } $StartupTimeoutSeconds 'Startup recovery did not remove the abandoned canary file.'
    Assert (Test-Path -LiteralPath $originalPath -PathType Leaf) 'Startup recovery deleted a file owned by a live worker.'
    Assert ((Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash -ceq $originalHash) 'The active-worker file changed during startup recovery.'
    $firstScreenshot = Capture-AppScreenshot $appOne 'startup-one'
    Pass 'Real portable WinUI startup recovered an orphan and preserved a live worker lease' @{ appPid = $appOne.Id; orphanCanaryRemoved = $true; activeFileSha256 = $originalHash; screenshot = $firstScreenshot }

    if (-not $appOne.CloseMainWindow() -or -not $appOne.WaitForExit(5000)) {
        Stop-OwnedProcess $appOne $appOneStartTicks 'portable app startup one'
    }
    $workerProcess.Kill($true)
    Assert ($workerProcess.WaitForExit(5000)) 'Timed out stopping the synthetic worker.'
    Pass 'Stopped only the synthetic worker created by this profile' @{ pid = $workerProcess.Id; startTimeUtcTicks = $ownerReadyInfo.workerStartTimeUtcTicks }

    $appTwo = Start-PortableApp $dataDirectory 'startup-two'
    $appTwoStartTicks = $appTwo.StartTime.ToUniversalTime().Ticks
    $ownedProcesses.Add(@{ process = $appTwo; startTime = $appTwoStartTicks; label = 'portable app startup two' })
    Wait-Until {
        try { $appTwo.Refresh(); return -not $appTwo.HasExited } catch { return $false }
    } 20 'Second portable app startup exited before recovery could run.'
    Wait-Until { -not (Test-Path -LiteralPath $originalPath) } $StartupTimeoutSeconds 'Recovery did not remove the abandoned original file after its worker stopped.'
    Assert (Test-Path -LiteralPath $unknownInStaging -PathType Leaf) 'Recovery removed an unknown file in a registered staging directory.'
    Assert ((Get-Content -LiteralPath $unknownInStaging -Raw) -ceq 'must remain untouched') 'Unknown staging file contents changed.'
    Assert (Test-Path -LiteralPath $modifiedPath -PathType Leaf) 'Recovery removed a registered file whose contents changed.'
    Assert ((Get-FileHash -LiteralPath $modifiedPath -Algorithm SHA256).Hash -ceq $modifiedHash) 'Changed registered file contents changed after recovery.'
    Assert (Test-Path -LiteralPath $lookalikeSentinel -PathType Leaf) 'Recovery touched an unregistered staging-like directory.'
    Assert ((Get-Content -LiteralPath $lookalikeSentinel -Raw) -ceq 'unregistered staging-like directory') 'Lookalike directory contents changed.'
    Assert (Test-Path -LiteralPath $outsideSentinel -PathType Leaf) 'Recovery touched an unrelated file outside staging.'
    Assert ((Get-Content -LiteralPath $outsideSentinel -Raw) -ceq 'outside registered staging') 'Unrelated outside file contents changed.'
    $secondScreenshot = Capture-AppScreenshot $appTwo 'startup-two'
    Pass 'Recovery removed only the unchanged registered file and preserved all canaries' @{
        unknownExtra = $unknownInStaging; changedRegisteredFile = $modifiedPath; unregisteredLookalike = $lookalikeSentinel; outsideFile = $outsideSentinel; screenshot = $secondScreenshot
    }

    Save-Results $true
    Write-Output "Results: $resultsPath"
}
catch {
    $message = $_.Exception.ToString()
    $checks.Add(@{ name = 'Startup recovery smoke'; status = 'FAIL'; details = $message })
    Save-Results $false $message
    Write-Error $message
    throw
}
finally {
    foreach ($owned in $ownedProcesses) {
        $process = $owned.process
        $ticks = [long]$owned.startTime
        if ($process.Id -ne $PID) { Stop-OwnedProcess $process $ticks $owned.label }
    }
}
