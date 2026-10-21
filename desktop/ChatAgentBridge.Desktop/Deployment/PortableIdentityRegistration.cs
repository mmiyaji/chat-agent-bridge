using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ChatAgentBridge.Core.Deployment;
using ChatAgentBridge.Core.Updates;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ChatAgentBridge_Desktop.Deployment;

public sealed record PortableIdentityRegistrationInfo(bool Active, string? RegisteredLocation, bool CertificateTrusted);

/// <summary>Current-user, signed external-location packages. Never changes machine trust or Developer Mode.</summary>
public static class PortableIdentityRegistration
{
    public static Task<PortableIdentityRegistrationInfo> InspectAsync(string installDirectory, PortableIdentityBundle bundle) =>
        Task.Run(() =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new PlatformNotSupportedException("Portable notification identity requires Windows 10 version 2004 or later.");
            var installed = FindOwnPackage();
            string? location = installed is null ? null : ExternalPath(installed);
            bool active = false;
            try
            {
                var current = Package.Current;
                active = IsOwn(current) && SamePath(ExternalPath(current), installDirectory);
            }
            catch (InvalidOperationException) { }
            catch (COMException error) when (error.HResult == unchecked((int)0x80073D54) || error.HResult == unchecked((int)0x80073CF6)) { }
            // Existing registrations remain manageable after the signing certificate expires.
            bool alreadyHere = location is not null && SamePath(location, installDirectory);
            return new PortableIdentityRegistrationInfo(active, location, alreadyHere || IsCertificateTrusted(bundle));
        });

    public static async Task RegisterAsync(string installDirectory, PortableIdentityBundle bundle, bool trustCertificate, bool allowRelocate)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Portable notification identity requires Windows 10 version 2004 or later.");
        using var guard = AcquireRegistrationGuard();
        var verified = PortableIdentityBundle.LoadValidated(installDirectory);
        EnsureUnchanged(bundle, verified);
        var old = FindOwnPackage();
        var oldLocation = old is null ? null : ExternalPath(old);
        if (oldLocation is not null && SamePath(oldLocation, installDirectory)) return;
        if (oldLocation is not null && !allowRelocate)
            throw new InvalidOperationException("The notification identity belongs to another portable folder. Explicit relocation consent is required.");
        if (old is not null) EnsureNotRunning(old);

        using var certificate = X509CertificateLoader.LoadCertificate(verified.PublicCertificateBytes);
        bool addedTrust = false, removedOld = false, deploymentAttempted = false, rollbackIncomplete = false;
        try
        {
            if (!IsCertificateTrusted(verified))
            {
                if (!trustCertificate)
                    throw new COMException("The notification package signer is not trusted for this Windows user.", unchecked((int)0x800B0109));
                if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                    throw new CryptographicException("An expired or not-yet-valid certificate cannot be added to user trust.");
                // Trust only this public, non-CA, code-signing leaf, after explicit UI consent.
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                if (!ContainsCertificate(store, certificate))
                {
                    store.Add(certificate);
                    addedTrust = true;
                }
            }
            var manager = new PackageManager();
            if (old is not null)
            {
                CheckResult(await manager.RemovePackageAsync(old.Id.FullName));
                removedOld = true;
            }
            try
            {
                deploymentAttempted = true;
                CheckResult(await manager.AddPackageByUriAsync(new Uri(verified.PackagePath), new AddPackageOptions
                {
                    ExternalLocationUri = DirectoryUri(installDirectory)
                    // No AllowUnsigned, force shutdown, provisioning, or development registration.
                }));
                var registered = FindOwnPackage();
                if (registered is null || !SamePath(ExternalPath(registered), installDirectory))
                    throw new InvalidDataException("Windows did not retain the requested portable notification location.");
            }
            catch
            {
                // A successful add followed by a failed postcondition must not leave a new registration behind.
                if (deploymentAttempted)
                {
                    try
                    {
                        var added = FindOwnPackage();
                        if (added is not null && SamePath(ExternalPath(added), installDirectory))
                        {
                            EnsureNotRunning(added);
                            CheckResult(await manager.RemovePackageAsync(added.Id.FullName));
                        }
                    }
                    catch (Exception cleanupError)
                    {
                        rollbackIncomplete = true;
                        throw new InvalidOperationException("Notification setup failed and its registration could not be rolled back.", cleanupError);
                    }
                }
                if (removedOld && oldLocation is not null && Directory.Exists(oldLocation))
                {
                    try
                    {
                        PortableUpdatePaths.EnsureNoReparsePoints(oldLocation);
                        CheckResult(await manager.AddPackageByUriAsync(new Uri(verified.PackagePath), new AddPackageOptions
                        {
                            ExternalLocationUri = DirectoryUri(oldLocation)
                        }));
                    }
                    catch (Exception restoreError)
                    {
                        throw new InvalidOperationException("Notification registration failed and the previous location could not be restored.", restoreError);
                    }
                }
                throw;
            }
        }
        catch
        {
            if (addedTrust && !rollbackIncomplete)
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                // Restore the trust state on failure; never remove pre-existing user trust.
                store.Remove(certificate);
            }
            throw;
        }
    }

    public static async Task UnregisterAsync(string installDirectory, PortableIdentityBundle bundle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Portable notification identity requires Windows 10 version 2004 or later.");
        using var guard = AcquireRegistrationGuard();
        EnsureUnchanged(bundle, PortableIdentityBundle.LoadValidated(installDirectory));
        var package = FindOwnPackage();
        if (package is null) return;
        if (!SamePath(ExternalPath(package), installDirectory))
            throw new InvalidOperationException("Refusing to remove the notification registration of another portable folder.");
        EnsureNotRunning(package);
        CheckResult(await new PackageManager().RemovePackageAsync(package.Id.FullName));
        // Certificate trust is retained: it can also be used by another installation or later updates.
    }

    private static void EnsureUnchanged(PortableIdentityBundle expected, PortableIdentityBundle actual)
    {
        if (expected.PackageSha256 != actual.PackageSha256 || expected.CertificateSha256 != actual.CertificateSha256)
            throw new InvalidDataException("The notification package changed after confirmation.");
    }

    private static Package? FindOwnPackage()
    {
        var matches = new PackageManager().FindPackagesForUser("").Where(IsOwn).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Multiple portable notification registrations were found.");
        return matches.SingleOrDefault();
    }

    private static bool IsOwn(Package package) => package.Id.Name == PortableIdentityBundle.PackageName &&
        package.Id.Publisher == PortableIdentityBundle.Publisher;

    [SupportedOSPlatform("windows10.0.19041")]
    private static string ExternalPath(Package package)
    {
        if (package.Id.Version is not { Major: 1, Minor: 0, Build: 0, Revision: 0 })
            throw new InvalidDataException("Unsupported portable notification identity version.");
        var path = package.EffectiveExternalPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The notification registration is not an external-location package.");
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static Uri DirectoryUri(string path) => new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar);

    private static void CheckResult(DeploymentResult result)
    {
        if (result.ExtendedErrorCode is { } error)
            throw new COMException("Windows notification identity registration failed: " + result.ErrorText, error.HResult);
    }

    private static FileStream AcquireRegistrationGuard()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatAgentBridge", "notification-setup");
        PortableUpdatePaths.EnsureNoReparsePoints(folder);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "registration.lock");
        PortableUpdatePaths.EnsureNoReparsePoints(file);
        return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool IsCertificateTrusted(PortableIdentityBundle bundle)
    {
        int signatureTrust = VerifyPackageTrust(bundle.PackagePath);
        if (signatureTrust == 0) return true;
        if (signatureTrust is not (unchecked((int)0x800B0109) or unchecked((int)0x800B010A) or unchecked((int)0x800B0004)))
            throw new COMException("Windows could not verify the notification package signature.", signatureTrust);
        using var certificate = X509CertificateLoader.LoadCertificate(bundle.PublicCertificateBytes);
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.TrustedPeople, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            if (ContainsCertificate(store, certificate)) return true;
        }
        using var chain = new X509Chain();
        // Preliminary UI state only. PackageManager still performs Windows signature/trust enforcement.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
        return chain.Build(certificate);
    }

    private static int VerifyPackageTrust(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        nint filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = filePointer, StateAction = 1,
            ProviderFlags = 0x1000 | 0x10 // Cache-only, no revocation fetch; deployment performs its own final checks.
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try { return WinVerifyTrust(new nint(-1), ref action, ref data); }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint Handle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File;
        public uint StateAction;
        public nint State, UrlReference;
        public uint ProviderFlags, UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    private static bool ContainsCertificate(X509Store store, X509Certificate2 expected)
    {
        bool found = false;
        foreach (var certificate in store.Certificates)
            using (certificate)
                found |= certificate.RawData.AsSpan().SequenceEqual(expected.RawData);
        return found;
    }

    private static void EnsureNotRunning(Package package)
    {
        foreach (var process in Process.GetProcessesByName("ChatAgentBridge.Desktop"))
        {
            using (process)
            {
                if (process.HasExited) continue;
                uint count = 0;
                int result = GetPackageFullName(process.Handle, ref count, null);
                if (result == 15700) continue; // APPMODEL_ERROR_NO_PACKAGE
                if (result != 122) throw new InvalidOperationException("Close all portable app copies before changing notification registration.");
                var name = new StringBuilder(checked((int)count));
                if (GetPackageFullName(process.Handle, ref count, name) != 0)
                    throw new InvalidOperationException("Could not verify a running portable app copy.");
                if (name.ToString().Equals(package.Id.FullName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Close the other running portable app copy before changing notification registration.");
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFullName(nint process, ref uint count, StringBuilder? name);
}
