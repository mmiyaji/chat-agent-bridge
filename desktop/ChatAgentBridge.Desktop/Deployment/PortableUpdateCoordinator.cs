using ChatAgentBridge.Core.Updates;
using ChatAgentBridge_Desktop.Localization;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>Manual, explicitly confirmed portable updates. Network and staging never stop chat work.</summary>
public sealed class PortableUpdateCoordinator : IDisposable
{
    private readonly PortableReleaseClient _client;
#if CAB_PORTABLE_UPDATE_TEST
    private readonly HttpClient _testHttpClient;
#endif
    private readonly string _dataDirectory;
    private readonly string _installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private readonly bool _hasLifetimeLease;
    private readonly string _currentVersion;
    private PortableRelease? _available;
    private StagedPortableRelease? _staged;
    private string? _workDirectory;
    private CancellationTokenSource? _operation;
    private bool _includePrereleases;
    private bool _committing;
    private bool _disposed;

    public bool IsSupported => DeploymentRuntime.IsPortable && _hasLifetimeLease && !_disposed;
    public bool IsBusy { get; private set; }
    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public string Status { get; private set; }
    public string? AvailableVersion => _available?.Version;
    public bool IsDownloaded => _staged is not null;
    public event EventHandler? Changed;

    public bool IncludePrereleases
    {
        get => _includePrereleases;
        set
        {
            if (IsBusy || value == _includePrereleases) return;
            _includePrereleases = value;
            _available = null;
            DiscardDownload();
            Status = DesktopText.Get("PortableUpdateIdle");
            Notify();
        }
    }

    public PortableUpdateCoordinator(string dataDirectory, bool hasLifetimeLease)
    {
        _dataDirectory = dataDirectory;
        _hasLifetimeLease = hasLifetimeLease;
#if CAB_PORTABLE_UPDATE_TEST
        _testHttpClient = PortableUpdateTestFeed.CreateClient();
        _client = new PortableReleaseClient(_testHttpClient);
#else
        _client = new PortableReleaseClient();
#endif
        _currentVersion = ReadCurrentVersion();
        _includePrereleases = _currentVersion.Contains('-');
        Status = DesktopText.Get(IsSupported ? "PortableUpdateIdle" : "PortableUpdateUnsupported");
    }

    public async Task CheckAsync()
    {
        if (!TryBegin()) return;
        DiscardDownload();
        _available = null;
        Status = DesktopText.Get("PortableUpdateChecking");
        Notify();
        try
        {
            _available = await _client.FindUpdateAsync(_currentVersion, Architecture(), IncludePrereleases, _operation!.Token);
            Status = _available is null ? DesktopText.Get("PortableUpdateCurrent")
                : DesktopText.Get("PortableUpdateAvailable", _available.Version);
        }
        catch (OperationCanceledException) { ShowCancellationOrTimeout(); }
        catch (Exception error) { ShowError(error); }
        finally { EndOperation(); }
    }

    public async Task DownloadAsync()
    {
        if (_available is not { } release || !TryBegin()) return;
        DiscardDownload();
        _workDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatAgentBridge", "Updates", Guid.NewGuid().ToString("N"));
        IsDownloading = true;
        DownloadProgress = 0;
        Status = DesktopText.Get("PortableUpdateDownloading", release.Version);
        Notify();
        try
        {
            PortableUpdatePaths.EnsureNoReparsePoints(_workDirectory);
            Directory.CreateDirectory(_workDirectory);
            var zip = Path.Combine(_workDirectory, "update.zip");
            var progress = new Progress<double>(value =>
            {
                var percent = Math.Clamp(value * 100, 0, 100);
                if (percent - DownloadProgress < 1 && percent < 100) return;
                DownloadProgress = percent;
                Notify();
            });
            await _client.DownloadAsync(release, zip, progress, _operation!.Token);
            IsDownloading = false;
            Status = DesktopText.Get("PortableUpdateVerifying");
            Notify();
            _staged = await PortableArchiveStager.ExtractAsync(zip, Path.Combine(_workDirectory, "stage"), release, _operation.Token);
            Status = DesktopText.Get("PortableUpdateReady", release.Version);
        }
        catch (OperationCanceledException) { DiscardDownload(); ShowCancellationOrTimeout(); }
        catch (Exception error) { DiscardDownload(); ShowError(error); }
        finally { IsDownloading = false; EndOperation(); }
    }

    public async Task ApplyAsync()
    {
        if (_available is not { } release || _staged is not { } staged || _workDirectory is null || !TryBegin()) return;
        Process? helper = null;
        try
        {
            if (App.Window is not MainWindow window || !window.CanRestartForUpdate)
            {
                Status = DesktopText.Get("PortableUpdateBusyWork");
                return;
            }
            var originalHelper = Path.Combine(_installDirectory, "ChatAgentBridge.Updater.exe");
            if (!File.Exists(originalHelper)) { Status = DesktopText.Get("PortableUpdateHelperMissing"); return; }
            using var parent = Process.GetCurrentProcess();
            var plan = new PortableUpdatePlan(1, _installDirectory, _dataDirectory, staged.DirectoryPath,
                _workDirectory, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
                staged.Files.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                release.Version, Architecture(), Guid.NewGuid().ToString("N"), CultureInfo.CurrentUICulture.Name);
            await PortableUpdateApplier.ValidateStagingAsync(plan, _operation!.Token);
            var planFile = Path.Combine(_workDirectory, PortableUpdatePlan.FileName);
            await File.WriteAllTextAsync(planFile, JsonSerializer.Serialize(plan), _operation.Token);
            var helperPath = Path.Combine(_workDirectory, "ChatAgentBridge.Updater.exe");
            File.Copy(originalHelper, helperPath, overwrite: true);
            var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workDirectory };
            foreach (var argument in new[] { "--apply", planFile, "--nonce", plan.Nonce }) start.ArgumentList.Add(argument);
            helper = Process.Start(start) ?? throw new IOException("Update helper did not start.");
            Status = DesktopText.Get("PortableUpdateStarting");
            Notify();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!File.Exists(plan.ReadyFile))
            {
                _operation.Token.ThrowIfCancellationRequested();
                if (helper.HasExited || DateTimeOffset.UtcNow >= deadline) throw new IOException("Update helper did not become ready.");
                await Task.Delay(100, _operation.Token);
            }
            _operation.Token.ThrowIfCancellationRequested();
            // Stop receiving first, then recheck work/drafts before committing to restart.
            if (!await window.PrepareForPortableUpdateAsync())
            {
                Status = DesktopText.Get("PortableUpdateBusyWork");
                return;
            }
            _operation.Token.ThrowIfCancellationRequested();
            if (helper.HasExited) throw new IOException("Update helper exited before restart.");
            _committing = true;
            await App.Tray.ExitAsync();
        }
        catch (OperationCanceledException) { ShowCancellationOrTimeout(); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            if (!_committing && helper is { HasExited: false })
            {
                try { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            if (!_committing && App.Window is MainWindow window) window.CancelPortableUpdatePreparation();
            helper?.Dispose();
            EndOperation();
        }
    }

    public void Cancel() { if (!_committing) _operation?.Cancel(); }

    private bool TryBegin()
    {
        if (!IsSupported || IsBusy) return false;
        IsBusy = true;
        _operation = new();
        return true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        _operation?.Dispose();
        _operation = null;
        Notify();
    }

    private void ShowError(Exception error)
    {
        // Report useful categories without echoing tokens, signed redirect URLs or HTTP response bodies.
        var reason = error switch
        {
            HttpRequestException => DesktopText.Get("PortableUpdateNetworkError"),
            UnauthorizedAccessException => DesktopText.Get("PortableUpdateAccessError"),
            InvalidDataException or JsonException => DesktopText.Get("PortableUpdateIntegrityError"),
            _ => DesktopText.Get("PortableUpdateLocalError")
        };
        Status = DesktopText.Get("PortableUpdateFailed", reason);
    }

    private void ShowCancellationOrTimeout() => Status = _operation?.IsCancellationRequested == true
        ? DesktopText.Get("PortableUpdateCancelled")
        : DesktopText.Get("PortableUpdateFailed", DesktopText.Get("PortableUpdateNetworkError"));

    private string ReadCurrentVersion()
    {
        try
        {
            var path = Path.Combine(_installDirectory, "portable-release.json");
            if (new FileInfo(path).Length <= 65536)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var version = json.RootElement.GetProperty("releaseVersion").GetString();
                if (ReleaseVersion.TryParse(version, out var parsed)) return parsed.ToString();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        var assemblyVersion = typeof(DeploymentRuntime).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    private static string Architecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException()
    };

    private void Notify()
    {
        if (App.DispatcherQueue.HasThreadAccess) Changed?.Invoke(this, EventArgs.Empty);
        else App.DispatcherQueue.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private void DiscardDownload()
    {
        _staged = null;
        if (_workDirectory is not null && Directory.Exists(_workDirectory))
        {
            try
            {
                PortableUpdatePaths.EnsureNoReparsePoints(_workDirectory);
                var retired = _workDirectory;
                _ = Task.Run(() =>
                {
                    try { PortableUpdatePaths.EnsureNoReparsePoints(retired); Directory.Delete(retired, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
        }
        _workDirectory = null;
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_committing) _operation?.Cancel();
        _client.Dispose();
#if CAB_PORTABLE_UPDATE_TEST
        _testHttpClient.Dispose();
#endif
    }
}
