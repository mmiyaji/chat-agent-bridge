[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$PackageName = 'mmiyaji.ChatAgentBridge',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Publisher,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublisherDisplayName,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion,
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/store'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$expectedPackageName = 'mmiyaji.ChatAgentBridge'
if ($PackageName -cne $expectedPackageName) {
    throw "PackageName must be '$expectedPackageName'. Confirm this name is assigned to this app in Partner Center before building."
}
if ($Publisher -notmatch '^CN=.+$' -or $Publisher -eq 'CN=ChatAgentBridgeDevelopment' -or
    $Publisher -match '[<>\r\n]' -or $Publisher -match '(?i)placeholder|replace.?me|example') {
    throw 'Publisher must be the formal Store Publisher value from this app in Partner Center, not the development publisher.'
}
try { [void][System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher) }
catch { throw 'Publisher must be a valid X.500 distinguished name copied from Partner Center.' }
if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'PublisherDisplayName must be the formal Store publisher display name from Partner Center.'
}
$versionParts = @($PackageVersion.Split('.') | ForEach-Object { [uint32]::Parse($_) })
if ($PackageVersion -cne ($versionParts -join '.') -or $versionParts.Count -ne 4 -or $versionParts[0] -eq 0 -or
    $versionParts[0] -gt 65535 -or $versionParts[1] -gt 65535 -or
    $versionParts[2] -gt 65535 -or $versionParts[3] -ne 0) {
    throw 'Store PackageVersion must be major.minor.build.0, with a nonzero major and each of the first three parts at most 65535.'
}

$project = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop\ChatAgentBridge.Desktop.csproj'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$output = Join-Path $outputRoot (Join-Path $PackageVersion (Join-Path $Architecture ([guid]::NewGuid().ToString('N'))))
$manifestDirectory = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop\obj\CABStore-manifests'
$manifest = Join-Path $manifestDirectory ("$([guid]::NewGuid().ToString('N')).appxmanifest")

New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
try {
    [xml]$document = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop\Package.appxmanifest') -Raw
    $identity = $document.Package.Identity
    if ($null -eq $identity) { throw 'Development package manifest has no Package/Identity element.' }
    $identity.SetAttribute('Name', $PackageName)
    $identity.SetAttribute('Publisher', $Publisher)
    $identity.SetAttribute('Version', $PackageVersion)
    $document.Package.Properties.DisplayName = 'Chat Agent Bridge'
    $document.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $document.Save($manifest)

    [xml]$generated = Get-Content -LiteralPath $manifest -Raw
    if ($generated.Package.Identity.Name -cne $expectedPackageName -or
        $generated.Package.Identity.Publisher -cne $Publisher -or
        $generated.Package.Identity.Version -cne $PackageVersion -or
        $generated.Package.Properties.DisplayName -cne 'Chat Agent Bridge' -or
        $generated.Package.Properties.PublisherDisplayName -cne $PublisherDisplayName) {
        throw 'Generated Store manifest identity validation failed.'
    }

    if ($ValidateOnly) {
        Write-Output "Store package configuration validated: $expectedPackageName $PackageVersion ($Architecture)."
        return
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $runtimeIdentifier = if ($Architecture -eq 'x64') { 'win-x64' } else { 'win-arm64' }
    $arguments = @(
        'publish', $project,
        '--configuration', 'Release',
        ('-p:Platform=' + $Architecture),
        ('-p:RuntimeIdentifier=' + $runtimeIdentifier),
        '-p:CABStore=true',
        ('-p:CABStoreManifest=' + $manifest),
        ('-p:CABStorePackageName=' + $PackageName),
        ('-p:CABStorePublisher=' + $Publisher),
        ('-p:CABStorePublisherDisplayName=' + $PublisherDisplayName),
        ('-p:CABStorePackageVersion=' + $PackageVersion),
        '-p:CABPortable=false',
        '-p:CABPortableIdentity=false',
        '-p:CABPortableUpdateTest=false',
        '-p:CABDiscordFixture=false',
        ('-p:AppxPackageDir=' + ($output.TrimEnd('\') + '\')),
        '-p:AppxPackageSigningEnabled=false',
        '-p:AppxSymbolPackageEnabled=false',
        '-p:AppxBundle=Never',
        '-p:GenerateAppxPackageOnBuild=true',
        '-p:UapAppxPackageBuildMode=StoreUpload'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $packages = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Extension -in '.msixupload', '.appxupload' })
    if ($packages.Count -eq 0) {
        throw "Build completed, but no Store upload package was found under '$output'."
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($upload in $packages | Where-Object { $_.Extension -in '.msixupload', '.appxupload' }) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($upload.FullName)
        try {
            $innerPackage = $archive.Entries | Where-Object { $_.FullName -match '\.(msix|appx)$' } | Select-Object -First 1
            if ($null -eq $innerPackage) { throw "Store upload archive '$($upload.Name)' has no MSIX payload." }
            $innerStream = $innerPackage.Open()
            try {
                $payload = [System.IO.Compression.ZipArchive]::new($innerStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
                try {
                    $manifestEntry = $payload.GetEntry('AppxManifest.xml')
                    if ($null -eq $manifestEntry) { throw "MSIX payload in '$($upload.Name)' has no AppxManifest.xml." }
                    $reader = [IO.StreamReader]::new($manifestEntry.Open())
                    try { [xml]$packagedManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($packagedManifest.Package.Identity.Name -cne $PackageName -or
                        $packagedManifest.Package.Identity.Publisher -cne $Publisher -or
                        $packagedManifest.Package.Identity.Version -cne $PackageVersion -or
                        $packagedManifest.Package.Identity.ProcessorArchitecture -cne $Architecture.ToLowerInvariant() -or
                        $packagedManifest.Package.Properties.DisplayName -cne 'Chat Agent Bridge' -or
                        $packagedManifest.Package.Properties.PublisherDisplayName -cne $PublisherDisplayName) {
                        throw "Packaged MSIX identity does not match supplied Store identity/version: '$($upload.Name)'."
                    }
                    $capabilities = @($packagedManifest.Package.Capabilities.ChildNodes | ForEach-Object { $_.Name })
                    if ($capabilities.Count -ne 1 -or $capabilities[0] -cne 'runFullTrust') {
                        throw "Store MSIX has unexpected capabilities: $($capabilities -join ', ')."
                    }
                    $frameworkDependencies = @($packagedManifest.SelectNodes("//*[local-name()='PackageDependency']") | Where-Object {
                        $_.GetAttribute('Name') -match '^Microsoft\.WindowsAppRuntime\.'
                    })
                    if ($frameworkDependencies.Count -ne 1) {
                        throw "Store MSIX must declare exactly one Microsoft.WindowsAppRuntime framework PackageDependency: '$($upload.Name)'."
                    }
                    $frameworkDependency = $frameworkDependencies[0]
                    $minimumRuntimeVersion = [version]::new()
                    if ($frameworkDependency.GetAttribute('Publisher') -cne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
                        -not [version]::TryParse($frameworkDependency.GetAttribute('MinVersion'), [ref]$minimumRuntimeVersion)) {
                        throw "Store MSIX Windows App Runtime dependency has an unexpected Publisher or invalid MinVersion: '$($upload.Name)'."
                    }
                    $forbiddenEntries = @($payload.Entries | Where-Object {
                        $_.FullName -match '(^|/)(ChatAgentBridge\.(Updater|TestAgent)(\.|/)|Data/|test-support/|fixture/)'
                    })
                    if ($forbiddenEntries.Count -gt 0) {
                        throw "Store MSIX contains portable/test-only payload: $($forbiddenEntries.FullName -join ', ')."
                    }
                    if ($null -eq $payload.GetEntry('ChatAgentBridge.Desktop.exe') -or
                        $null -eq $payload.GetEntry('hostfxr.dll') -or
                        $null -eq $payload.GetEntry('coreclr.dll') -or
                        $null -eq $payload.GetEntry('hostpolicy.dll')) {
                        throw "Store MSIX is missing the desktop executable or required self-contained .NET runtime: '$($upload.Name)'."
                    }
                    if ($null -ne ($payload.Entries | Where-Object { $_.FullName -match '(^|/)Microsoft\.ui\.xaml\.dll$' } | Select-Object -First 1)) {
                        throw "Store MSIX contains Microsoft.ui.xaml.dll; Windows App SDK must use the shared framework: '$($upload.Name)'."
                    }
                }
                finally { $payload.Dispose() }
            }
            finally { $innerStream.Dispose() }
        }
        finally { $archive.Dispose() }
    }
    Write-Output "Store upload package created: $($packages.FullName -join ', ')"
}
finally {
    Remove-Item -LiteralPath $manifest -Force -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $manifestDirectory) -and
        -not (Get-ChildItem -LiteralPath $manifestDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $manifestDirectory -Force -ErrorAction SilentlyContinue
    }
}
