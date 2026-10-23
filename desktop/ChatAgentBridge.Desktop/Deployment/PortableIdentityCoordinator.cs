using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;
using ChatAgentBridge.Core.Updates;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>Prepares a confirmed identity change; the existing updater performs it after safe shutdown.</summary>
public sealed class PortableIdentityCoordinator(string dataDirectory, bool hasLifetimeLease)
{
    private readonly string _installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private PortableIdentityBundle? _bundle;
    private int _operation;

    public bool IsSupported => DeploymentRuntime.IsPortable && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    public bool IsBusy => Volatile.Read(ref _operation) != 0;
    public bool CanConfigure => IsSupported && hasLifetimeLease && !IsBusy && _bundle is not null && !IsActive;
    public bool IsActive { get; private set; }
    public bool IsRegistered => RegisteredLocation is not null;
    public bool CanUnregister => IsSupported && hasLifetimeLease && !IsBusy && _bundle is not null && IsRegistered && !HasOtherLocation;
    public bool NeedsCertificateTrust { get; private set; }
    public bool HasOtherLocation => RegisteredLocation is not null && !SamePath(RegisteredLocation, _installDirectory);
    public string Status { get; private set; } = DesktopText.Get("PortableIdentityChecking");
    public string Publisher => PortableIdentityBundle.Publisher;
    public string CertificateFingerprint => _bundle?.CertificateSha256 ?? "";
    public string? RegisteredLocation { get; private set; }
    public event EventHandler? Changed;

    public async Task RefreshAsync()
    {
        if (!Begin()) return;
        try
        {
            IsActive = false;
            RegisteredLocation = null;
            NeedsCertificateTrust = false;
            _bundle = null;
            if (!IsSupported) { Status = DesktopText.Get("PortableIdentityUnsupported"); return; }
            var metadata = Path.Combine(_installDirectory, "Identity", PortableIdentityBundle.MetadataFileName);
            if (!File.Exists(metadata)) { Status = DesktopText.Get("PortableIdentityMissingBundle"); return; }
            _bundle = await Task.Run(() => PortableIdentityBundle.LoadValidated(_installDirectory));
            var info = await PortableIdentityRegistration.InspectAsync(_installDirectory, _bundle);
            IsActive = info.Active;
            RegisteredLocation = info.RegisteredLocation;
            NeedsCertificateTrust = !info.CertificateTrusted;
            Status = DesktopText.Get(IsActive ? "PortableIdentityActive" : HasOtherLocation ? "PortableIdentityOtherLocationStatus" :
                IsRegistered ? "PortableIdentityRegisteredRestart" : NeedsCertificateTrust ? "PortableIdentityTrustRequiredStatus" : "PortableIdentityReady",
                RegisteredLocation ?? "");
            var resultFile = Path.Combine(dataDirectory, "portable-notification-result.json");
            PortableUpdatePaths.EnsureNoReparsePoints(resultFile);
            if (!IsActive && File.Exists(resultFile) && new FileInfo(resultFile).Length <= 4096)
            {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
                if (result.RootElement.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.False &&
                    result.RootElement.TryGetProperty("ErrorHResult", out var error) && error.ValueKind == JsonValueKind.String &&
                    error.GetString() is { Length: 10 } hresult && hresult.StartsWith("0x", StringComparison.Ordinal) && hresult[2..].All(Uri.IsHexDigit))
                    Status = DesktopText.Get("PortableIdentityLastFailed", hresult);
            }
        }
        catch (Exception error) when (IsSetupError(error))
        {
            _bundle = null;
            Status = DesktopText.Get("PortableIdentityInvalidBundle");
        }
        finally { End(); }
    }

    public Task ConfigureAsync(bool trustCertificate, bool relocate) => ChangeAsync("register", trustCertificate, relocate);
    public Task RemoveAsync() => ChangeAsync("unregister", false, false);

    private async Task ChangeAsync(string operation, bool trustCertificate, bool relocate)
    {
        if (!IsSupported || !hasLifetimeLease || _bundle is not { } confirmed || !Begin()) return;
        Process? helper = null;
        MainWindow? window = null;
        bool committing = false;
        try
        {
            if (App.Window is not MainWindow currentWindow || !currentWindow.CanRestartForUpdate || App.PortableUpdates.IsBusy)
            {
                Status = DesktopText.Get("PortableIdentityBusyWork");
                return;
            }
            window = currentWindow;
            var originalHelper = Path.Combine(_installDirectory, "ChatAgentBridge.Updater.exe");
            if (!File.Exists(originalHelper)) { Status = DesktopText.Get("PortableIdentityHelperMissing"); return; }
            var bundle = await Task.Run(() => PortableIdentityBundle.LoadValidated(_installDirectory));
            if (bundle.PackageSha256 != confirmed.PackageSha256 || bundle.CertificateSha256 != confirmed.CertificateSha256)
                throw new InvalidDataException("Notification files changed after confirmation.");
            using var parent = Process.GetCurrentProcess();
            var nonce = Guid.NewGuid().ToString("N");
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatAgentBridge", "notification-setup", nonce);
            var plan = new PortableIdentitySetupPlan(1, _installDirectory, Path.GetFullPath(dataDirectory), work,
                parent.Id, parent.StartTime.ToUniversalTime().Ticks, nonce, operation, bundle.PackageSha256,
                bundle.CertificateSha256, trustCertificate, relocate, CultureInfo.CurrentUICulture.Name);
            plan.Validate();
            Directory.CreateDirectory(work);
            var planFile = Path.Combine(work, PortableIdentitySetupPlan.FileName);
            await File.WriteAllTextAsync(planFile, JsonSerializer.Serialize(plan));
            var helperPath = Path.Combine(work, "ChatAgentBridge.Updater.exe");
            File.Copy(originalHelper, helperPath, overwrite: false);
            var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
            foreach (var argument in new[] { "--configure-identity", planFile, "--nonce", nonce }) start.ArgumentList.Add(argument);
            helper = Process.Start(start) ?? throw new IOException("The notification setup helper did not start.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!File.Exists(plan.ReadyFile))
            {
                if (helper.HasExited || DateTimeOffset.UtcNow >= deadline) throw new IOException("Notification setup did not become ready.");
                await Task.Delay(100);
            }
            if (!await window.PrepareForPortableUpdateAsync())
            {
                Status = DesktopText.Get("PortableIdentityBusyWork");
                return;
            }
            if (helper.HasExited) throw new IOException("Notification setup exited before restart.");
            Status = DesktopText.Get("PortableIdentityStarting");
            Notify();
            await File.WriteAllTextAsync(plan.CommitFile, nonce);
            committing = true;
            await App.Tray.ExitAsync();
        }
        catch (Exception error) when (IsSetupError(error))
        {
            Status = DesktopText.Get("PortableIdentityFailed", $"0x{error.HResult:X8}");
        }
        finally
        {
            if (!committing)
            {
                if (helper is not null)
                {
                    try { if (!helper.HasExited) helper.Kill(); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                if (window is not null) await window.CancelPortableUpdatePreparationAsync();
            }
            helper?.Dispose();
            End();
        }
    }

    private bool Begin()
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return false;
        Notify();
        return true;
    }
    private void End() { Volatile.Write(ref _operation, 0); Notify(); }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static bool IsSetupError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException or
        CryptographicException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException or
        System.ComponentModel.Win32Exception or TimeoutException or PlatformNotSupportedException;
}
