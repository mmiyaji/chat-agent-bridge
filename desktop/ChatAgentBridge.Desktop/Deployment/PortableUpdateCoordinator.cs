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
    private readonly PortableUpdatePreferencesStore _preferencesStore;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly string _installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private readonly bool _hasLifetimeLease;
    private readonly DateTimeOffset _sessionStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly string _currentVersion;
    private PortableRelease? _available;
    private StagedPortableRelease? _staged;
    private string? _workDirectory;
    private CancellationTokenSource? _operation;
    private bool _includePrereleases;
    private bool _automaticChecksEnabled = true;
    private DateTimeOffset? _lastAttemptUtc;
    private PortableUpdatePreferences _preferences;
    private bool _initialized;
    private int _automaticRunner;
    private string? _automaticAvailableStatus;
    private bool _committing;
    private bool _disposed;

    public bool IsSupported => DeploymentRuntime.IsPortable && _hasLifetimeLease && !_disposed;
    public bool IsBusy { get; private set; }
    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public string Status { get; private set; }
    public string? AvailableVersion => _available?.Version;
    public Uri? AvailableReleasePage => _available?.ReleasePage;
    public bool AutomaticChecksEnabled => _automaticChecksEnabled;
    public bool IncludePrereleases => _includePrereleases;
    public bool IsDownloaded => _staged is not null;
    public event EventHandler? Changed;

    public PortableUpdateCoordinator(string dataDirectory, bool hasLifetimeLease)
    {
        _dataDirectory = dataDirectory;
        _preferencesStore = new PortableUpdatePreferencesStore(dataDirectory);
        _hasLifetimeLease = hasLifetimeLease;
#if CAB_PORTABLE_UPDATE_TEST
        _testHttpClient = PortableUpdateTestFeed.CreateClient();
        _client = new PortableReleaseClient(_testHttpClient);
#else
        _client = new PortableReleaseClient();
#endif
        _currentVersion = ReadCurrentVersion();
        _includePrereleases = _currentVersion.Contains('-');
        _preferences = PortableUpdatePreferences.CreateDefault(_includePrereleases);
        Status = DesktopText.Get(IsSupported ? "PortableUpdateIdle" : "PortableUpdateUnsupported");
    }

    public async Task InitializeAsync()
    {
        await _preferencesGate.WaitAsync();
        try
        {
            if (_initialized) return;
            PortableUpdatePreferences? preferences;
            var loadFailed = false;
            try { preferences = await _preferencesStore.LoadAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
            {
                preferences = null;
                loadFailed = true;
            }
            _preferences = preferences ?? PortableUpdatePreferences.CreateDefault(_includePrereleases);
            _automaticChecksEnabled = _preferences.AutomaticChecksEnabled;
            _includePrereleases = _preferences.IncludePrereleases;
            _lastAttemptUtc = _preferences.LastAttemptUtc;
            if (_lastAttemptUtc is { } lastAttempt && lastAttempt > DateTimeOffset.UtcNow.AddMinutes(5))
                _lastAttemptUtc = null;
            _available = PortableReleaseClient.IsValidCachedCandidate(
                _preferences.AvailableRelease, _currentVersion, Architecture(), _includePrereleases)
                ? _preferences.AvailableRelease
                : null;
            _preferences = _preferences with { LastAttemptUtc = _lastAttemptUtc, AvailableRelease = _available };
            _initialized = true;
            if (preferences is null || _preferences != preferences)
            {
                try { await _preferencesStore.SaveAsync(_preferences); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    loadFailed = true;
                }
            }
            if (loadFailed)
                Status = DesktopText.Get("PortableUpdateFailed", DesktopText.Get("PortableUpdateLocalError"));
            else if (_available is { } cached)
                UpdateAutomaticAvailabilityStatus(cached);
        }
        finally { _preferencesGate.Release(); }
        Notify();
    }

    public async Task SetAutomaticChecksEnabledAsync(bool enabled)
    {
        await InitializeAsync();
        await _preferencesGate.WaitAsync();
        try
        {
            if (_automaticChecksEnabled == enabled) return;
            var updated = _preferences with { AutomaticChecksEnabled = enabled };
            await _preferencesStore.SaveAsync(updated);
            _preferences = updated;
            _automaticChecksEnabled = enabled;
        }
        finally { _preferencesGate.Release(); }
        Notify();
    }

    public async Task SetIncludePrereleasesAsync(bool includePrereleases)
    {
        await InitializeAsync();
        await _preferencesGate.WaitAsync();
        try
        {
            if (IsBusy || _includePrereleases == includePrereleases) return;
            var updated = _preferences with { IncludePrereleases = includePrereleases, LastAttemptUtc = null, AvailableRelease = null };
            await _preferencesStore.SaveAsync(updated);
            _preferences = updated;
            _includePrereleases = includePrereleases;
            _lastAttemptUtc = null;
            _available = null;
            DiscardDownload();
            Status = DesktopText.Get("PortableUpdateIdle");
            _automaticAvailableStatus = null;
        }
        finally { _preferencesGate.Release(); }
        Notify();
    }

    public async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported || _disposed) return;
        if (Interlocked.Exchange(ref _automaticRunner, 1) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var token = linked.Token;
        try
        {
            await InitializeAsync();
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            // A manual check in this process already covers the startup check.
            var checkAtStartup = _lastAttemptUtc is null || _lastAttemptUtc < _sessionStartedAtUtc;
            await RunAutomaticCheckIfDueAsync(token, checkAtStartup);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                await RunAutomaticCheckIfDueAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* Background startup and persistence failures must not escape the app lifetime task. */ }
        finally { Volatile.Write(ref _automaticRunner, 0); }
    }

    private async Task RunAutomaticCheckIfDueAsync(CancellationToken cancellationToken, bool checkAtStartup = false)
    {
        if (!IsSupported || !_automaticChecksEnabled || IsBusy || IsDownloaded ||
            (!checkAtStartup && !PortableUpdateCheckPolicy.IsDue(_lastAttemptUtc, DateTimeOffset.UtcNow)) || !TryBegin()) return;
        Notify();

        try
        {
            using var operationLink = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operation!.Token);
            var operationToken = operationLink.Token;
            var attemptedAt = DateTimeOffset.UtcNow;
            await SaveAttemptTimeAsync(attemptedAt, operationToken);
            var candidate = await _client.FindUpdateAsync(_currentVersion, Architecture(), IncludePrereleases, operationToken);
            if (candidate is not null && !PortableReleaseClient.IsValidCachedCandidate(candidate, _currentVersion, Architecture(), IncludePrereleases))
                candidate = null;
            await SaveAvailableCandidateAsync(candidate, operationToken);
            if (_available != candidate)
            {
                _available = candidate;
                UpdateAutomaticAvailabilityStatus(candidate);
                Notify();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _operation?.IsCancellationRequested == true) { }
        catch (Exception) { /* Background failures stay quiet and preserve the current visible status and candidate. */ }
        finally { EndOperation(); }
    }

    public async Task CheckAsync()
    {
        await InitializeAsync();
        if (!TryBegin()) return;
        DiscardDownload();
        _available = null;
        Status = DesktopText.Get("PortableUpdateChecking");
        Notify();
        try
        {
            await SaveAttemptTimeAsync(DateTimeOffset.UtcNow, _operation!.Token);
            await SaveAvailableCandidateAsync(null, _operation.Token);
            _available = await _client.FindUpdateAsync(_currentVersion, Architecture(), IncludePrereleases, _operation!.Token);
            await SaveAvailableCandidateAsync(_available, _operation.Token);
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
        if (!IsSupported || IsBusy || App.PortableIdentity is { IsBusy: true }) return false;
        IsBusy = true;
        _operation = new();
        return true;
    }

    private async Task SaveAttemptTimeAsync(DateTimeOffset attemptedAtUtc, CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            _lastAttemptUtc = attemptedAtUtc;
            var updated = _preferences with { LastAttemptUtc = attemptedAtUtc };
            await _preferencesStore.SaveAsync(updated, cancellationToken);
            _preferences = updated;
        }
        finally { _preferencesGate.Release(); }
    }

    private static bool IsQuietStatus(string status) =>
        status == DesktopText.Get("PortableUpdateIdle") || status == DesktopText.Get("PortableUpdateCurrent");

    private void UpdateAutomaticAvailabilityStatus(PortableRelease? candidate)
    {
        if (candidate is not null)
        {
            if (IsQuietStatus(Status) || Status == _automaticAvailableStatus)
            {
                Status = DesktopText.Get("PortableUpdateAvailable", candidate.Version);
                _automaticAvailableStatus = Status;
            }
        }
        else if (Status == _automaticAvailableStatus)
        {
            Status = DesktopText.Get("PortableUpdateCurrent");
            _automaticAvailableStatus = null;
        }
    }

    private async Task SaveAvailableCandidateAsync(PortableRelease? candidate, CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            var updated = _preferences with { AvailableRelease = candidate };
            await _preferencesStore.SaveAsync(updated, cancellationToken);
            _preferences = updated;
        }
        finally { _preferencesGate.Release(); }
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
        _lifetimeCancellation.Cancel();
        if (!_committing) _operation?.Cancel();
        _client.Dispose();
#if CAB_PORTABLE_UPDATE_TEST
        _testHttpClient.Dispose();
#endif
    }
}
