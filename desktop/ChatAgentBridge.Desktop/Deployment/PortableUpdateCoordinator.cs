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
    private readonly string _workRoot;
    private readonly string _cleanupRoot;
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
    private bool _restoredStage;
    private bool _disposed;
    private Task? _cleanupTask;

    public bool IsSupported => DeploymentRuntime.IsPortable && _hasLifetimeLease && !_disposed;
    public bool IsBusy { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsPaused { get; private set; }
    private bool _pauseRequested;
    public double DownloadProgress { get; private set; }
    public string DownloadTransferDetails { get; private set; } = "";
    public string Status { get; private set; }
    public string? AvailableVersion => _available?.Version;
    public Uri? AvailableReleasePage => _available?.ReleasePage;
    public bool AutomaticChecksEnabled => _automaticChecksEnabled;
    public bool IncludePrereleases => _includePrereleases;
    public bool IsDownloaded => _staged is not null;
    public bool WasRestoredStage => _restoredStage;
    public event EventHandler? Changed;

    public bool TryClaimRestoredStagePrompt()
    {
        if (!_restoredStage) return false;
        _restoredStage = false;
        return true;
    }

    public PortableUpdateCoordinator(string dataDirectory, bool hasLifetimeLease)
    {
        _dataDirectory = dataDirectory;
        _workRoot = PortableUpdateStageStore.GetWorkRoot(_installDirectory, dataDirectory);
        _preferencesStore = new PortableUpdatePreferencesStore(dataDirectory);
        _hasLifetimeLease = hasLifetimeLease;
#if CAB_PORTABLE_UPDATE_TEST
        _testHttpClient = PortableUpdateTestFeed.CreateClient();
        _client = new PortableReleaseClient(_testHttpClient);
        // UI update fixtures must never clean the user's real shared update cache.
        _cleanupRoot = Path.Combine(Environment.GetEnvironmentVariable("CAB_PORTABLE_UPDATE_TEST_DIRECTORY")!, "cleanup-cache");
#else
        _client = new PortableReleaseClient();
        _cleanupRoot = Path.GetDirectoryName(_workRoot)!;
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
            await RestoreStageAsync();
            if (_staged is null) await RestorePartialDownloadAsync();
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
            else if (_staged is not null)
                Status = DesktopText.Get("PortableUpdateReady", _available?.Version ?? string.Empty);
            else if (_available is { } cached)
                UpdateAutomaticAvailabilityStatus(cached);
        }
        finally { _preferencesGate.Release(); }
        Notify();
        if (IsSupported) _cleanupTask = CleanCacheSafelyAsync();
    }

    private async Task CleanCacheSafelyAsync()
    {
        try
        {
            await PortableUpdateCacheCleanup.RunAsync(_cleanupRoot, DateTimeOffset.UtcNow, _workDirectory, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { Debug.WriteLine($"Update cache cleanup: {error.GetType().Name}"); }
    }

    public Task<long?> GetCacheBytesAsync() => PortableUpdateCacheCleanup.GetCacheBytesAsync(_cleanupRoot, _lifetimeCancellation.Token);

    public async Task<PortableUpdateCacheCleanupResult> CleanCacheAsync()
    {
        await InitializeAsync();
        if (!TryBegin()) return new(0, 0, 0);
        var cleanupToken = _operation!.Token;
        Notify();
        try
        {
            if (_cleanupTask is { } startupCleanup) await startupCleanup;
            return await PortableUpdateCacheCleanup.RunAsync(_cleanupRoot, DateTimeOffset.UtcNow,
                _workDirectory, cleanupToken);
        }
        finally { EndOperation(); }
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
        catch (Exception error) { ShowError(error, "check"); }
        finally { EndOperation(); }
    }

    public async Task DownloadAsync()
    {
        if (_available is not { } release || !TryBegin()) return;
        var failureStage = "download";
        var resuming = IsPaused;
        if (!resuming) DiscardDownload();
        IsPaused = false;
        _pauseRequested = false;
        _workDirectory ??= Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        IsDownloading = true;
        DownloadTransferDetails = DesktopText.Get("PortableUpdateTransferCalculating");
        if (!resuming) DownloadProgress = 0;
        var totalMiB = release.Size / (1024d * 1024d);
        Status = DesktopText.Get("PortableUpdateDownloading", release.Version, DownloadProgress / 100 * totalMiB, totalMiB);
        Notify();
        try
        {
            PortableUpdatePaths.EnsureNoReparsePoints(_workDirectory);
            Directory.CreateDirectory(_workRoot);
            PortableUpdatePaths.EnsureNoReparsePoints(_workRoot);
            Directory.CreateDirectory(_workDirectory);
            using var workLease = PortableUpdateCacheCleanup.AcquireWorkLease(_workDirectory);
            var checkpoint = new DownloadCheckpoint(_installDirectory, _dataDirectory, release.Sha256, release.Size);
            await File.WriteAllTextAsync(Path.Combine(_workDirectory, "download.json"),
                JsonSerializer.Serialize(checkpoint), _operation!.Token);
            var zip = Path.Combine(_workDirectory, "update.zip");
            var downloadToken = _operation!.Token;
            var lastReceived = Stopwatch.GetTimestamp();
            var retryAttempt = 0;
            var waiting = false;
            var reportedPercent = DownloadProgress;
            double? sampledPercent = null;
            var sampledAt = Stopwatch.GetTimestamp();
            double smoothedBytesPerSecond = 0;
            using var feedbackCancellation = new CancellationTokenSource();
            var progress = new Progress<double>(value =>
            {
                if (!IsDownloading || _operation?.Token != downloadToken) return;
                var percent = Math.Clamp(value * 100, 0, 100);
                lastReceived = Stopwatch.GetTimestamp();
                if (sampledPercent is null || percent < DownloadProgress)
                {
                    sampledPercent = percent;
                    sampledAt = Stopwatch.GetTimestamp();
                    smoothedBytesPerSecond = 0;
                }
                DownloadProgress = percent;
                var wasWaiting = waiting;
                waiting = false;
                var wasRetrying = retryAttempt > 0;
                retryAttempt = 0;
                if (!wasWaiting && !wasRetrying && percent - reportedPercent < 1 && percent < 100) return;
                reportedPercent = percent;
                Status = DesktopText.Get("PortableUpdateDownloading", release.Version, percent / 100 * totalMiB, totalMiB);
                Notify();
            });
            var retries = new Progress<int>(attempt =>
            {
                if (!IsDownloading || _operation?.Token != downloadToken) return;
                retryAttempt = attempt;
                Status = DesktopText.Get("PortableUpdateRetrying", attempt, DownloadProgress / 100 * totalMiB, totalMiB);
                Notify();
            });
            async Task ShowWaitingAsync()
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(1000, feedbackCancellation.Token);
                        if (!IsDownloading || _operation?.Token != downloadToken) return;
                        var seconds = (int)Stopwatch.GetElapsedTime(lastReceived).TotalSeconds;
                        var sampleSeconds = Stopwatch.GetElapsedTime(sampledAt).TotalSeconds;
                        if (seconds >= 5 || retryAttempt > 0)
                        {
                            DownloadTransferDetails = DesktopText.Get("PortableUpdateTransferCalculating");
                            sampledPercent = DownloadProgress;
                            sampledAt = Stopwatch.GetTimestamp();
                            smoothedBytesPerSecond = 0;
                        }
                        else if (sampledPercent is { } previousPercent && sampleSeconds >= 2)
                        {
                            var bytesPerSecond = Math.Max(0, DownloadProgress - previousPercent) / 100 * release.Size / sampleSeconds;
                            smoothedBytesPerSecond = smoothedBytesPerSecond == 0 ? bytesPerSecond
                                : 0.25 * bytesPerSecond + 0.75 * smoothedBytesPerSecond;
                            sampledPercent = DownloadProgress;
                            sampledAt = Stopwatch.GetTimestamp();
                            DownloadTransferDetails = smoothedBytesPerSecond > 0 && DownloadProgress < 100
                                ? DesktopText.Get("PortableUpdateTransferEstimate", smoothedBytesPerSecond / 1024,
                                    Math.Max(1, Math.Ceiling((1 - DownloadProgress / 100) * release.Size / smoothedBytesPerSecond / 60)))
                                : DesktopText.Get("PortableUpdateTransferCalculating");
                        }
                        Notify();
                        if (seconds < 5 || retryAttempt > 0) continue;
                        waiting = true;
                        Status = DesktopText.Get("PortableUpdateWaiting", seconds, DownloadProgress / 100 * totalMiB, totalMiB);
                        Notify();
                    }
                }
                catch (OperationCanceledException) when (feedbackCancellation.IsCancellationRequested) { }
            }
            var feedback = ShowWaitingAsync();
            try
            {
                await _client.DownloadAsync(release, zip, progress, downloadToken,
                    preservePartialOnCancellation: true, retryProgress: retries);
            }
            finally
            {
                feedbackCancellation.Cancel();
                await feedback;
            }
            IsDownloading = false;
            Status = DesktopText.Get("PortableUpdateVerifying");
            Notify();
            failureStage = "extract";
            _staged = await PortableArchiveStager.ExtractAsync(zip, Path.Combine(_workDirectory, "stage"), release, _operation.Token);
            Status = DesktopText.Get("PortableUpdatePreparing");
            Notify();
            failureStage = "save";
            await PortableUpdateStageStore.SaveAsync(_workDirectory, _installDirectory, _dataDirectory, Architecture(), release, _staged, _operation.Token);
            Status = DesktopText.Get("PortableUpdateReady", release.Version);
        }
        catch (OperationCanceledException) when (_pauseRequested)
        {
            IsPaused = true;
            Status = DesktopText.Get("PortableUpdatePaused", DownloadProgress / 100 * release.Size / (1024d * 1024d), release.Size / (1024d * 1024d));
        }
        catch (HttpRequestException error)
        {
            IsPaused = true;
            ShowError(error, failureStage);
        }
        catch (OperationCanceledException) when (_operation?.IsCancellationRequested != true)
        {
            IsPaused = true;
            ShowCancellationOrTimeout();
        }
        catch (OperationCanceledException) { DiscardDownload(); ShowCancellationOrTimeout(); }
        catch (Exception error) { DiscardDownload(); ShowError(error, failureStage); }
        finally { IsDownloading = false; EndOperation(); }
    }

    public async Task ApplyAsync()
    {
        if (_available is not { } release || _staged is not { } staged || _workDirectory is null || !TryBegin()) return;
        Process? helper = null;
        var loadingWindow = App.Window as MainWindow;
        try
        {
            loadingWindow?.SetPortableUpdateLoading(true);
#if CAB_PORTABLE_UPDATE_TEST
            var loadingGate = Environment.GetEnvironmentVariable("CAB_PORTABLE_UPDATE_LOADING_TEST_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(loadingGate))
            {
                if (!Path.IsPathFullyQualified(loadingGate)) throw new InvalidDataException("Test gate directory must be absolute.");
                var continueFile = Path.Combine(loadingGate, "continue");
                var loadingDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
                while (!File.Exists(continueFile))
                {
                    _operation!.Token.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= loadingDeadline) throw new TimeoutException("Update loading test gate timed out.");
                    await Task.Delay(100, _operation.Token);
                }
            }
#endif
            var restored = await PortableUpdateStageStore.LoadAsync(_workDirectory, _installDirectory, _dataDirectory,
                _currentVersion, Architecture(), IncludePrereleases, release, _operation!.Token)
                ?? throw new InvalidDataException("The staged update is no longer available.");
            _staged = staged = restored.Staged;
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
            try { await App.Tray.ExitForPortableUpdateAsync(); }
            catch
            {
                _committing = false;
                throw;
            }
        }
        catch (OperationCanceledException) { ShowCancellationOrTimeout(); }
        catch (Exception error) { ShowError(error, "apply"); }
        finally
        {
            try
            {
                if (!_committing && helper is { HasExited: false })
                {
                    try { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                    catch (InvalidOperationException) { }
                }
                if (!_committing && App.Window is MainWindow window) await window.CancelPortableUpdatePreparationAsync();
            }
            finally
            {
                try { helper?.Dispose(); }
                finally
                {
                    try { EndOperation(); }
                    finally
                    {
                        if (!_committing) loadingWindow?.SetPortableUpdateLoading(false);
                    }
                }
            }
        }
    }

    public void Pause()
    {
        if (!IsDownloading || _operation is null) return;
        _pauseRequested = true;
        _operation.Cancel();
    }

    public void Cancel()
    {
        if (_committing) return;
        _pauseRequested = false;
        if (IsPaused) { DiscardDownload(); Status = DesktopText.Get("PortableUpdateCancelled"); Notify(); }
        else _operation?.Cancel();
    }

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

    private void ShowError(Exception error, string stage)
    {
        // Report useful categories without echoing tokens, signed redirect URLs or HTTP response bodies.
        var reason = error switch
        {
            HttpRequestException => DesktopText.Get("PortableUpdateNetworkError"),
            UnauthorizedAccessException => DesktopText.Get("PortableUpdateAccessError", GetUpdateStage(stage), FormatErrorCode(error)),
            InvalidDataException or JsonException => DesktopText.Get("PortableUpdateIntegrityError"),
            IOException io when GetWindowsErrorCode(io) is 39 or 112 => DesktopText.Get("PortableUpdateDiskFullError", GetUpdateStage(stage), FormatErrorCode(io)),
            IOException io when GetWindowsErrorCode(io) is 32 or 33 => DesktopText.Get("PortableUpdateFileBusyError", GetUpdateStage(stage), FormatErrorCode(io)),
            IOException io => DesktopText.Get("PortableUpdateIoError", GetUpdateStage(stage), FormatErrorCode(io)),
            _ => DesktopText.Get("PortableUpdateStageError", GetUpdateStage(stage), FormatErrorCode(error))
        };
        Status = DesktopText.Get("PortableUpdateFailed", reason);
    }

    private static int GetWindowsErrorCode(Exception error) =>
        (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? error.HResult & 0xFFFF
            : 0;

    private static string FormatErrorCode(Exception error) => $"0x{error.HResult:X8}";

    private static string GetUpdateStage(string stage) => DesktopText.Get(stage switch
    {
        "check" => "PortableUpdateStageCheck",
        "extract" => "PortableUpdateStageExtract",
        "save" => "PortableUpdateStageSave",
        "apply" => "PortableUpdateStageApply",
        "download" => "PortableUpdateStageDownload",
        _ => "PortableUpdateStageGeneral"
    });

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
        IsPaused = false;
        _staged = null;
        if (_workDirectory is not null && Directory.Exists(_workDirectory))
        {
            try
            {
                PortableUpdatePaths.EnsureNoReparsePoints(_workDirectory);
                var retired = _workDirectory;
                _ = Task.Run(() =>
                {
                    try { PortableUpdateCacheCleanup.DiscardOwnedPayload(_installDirectory, _dataDirectory, retired); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
        }
        _workDirectory = null;
    }

    private sealed record DownloadCheckpoint(string InstallDirectory, string DataDirectory, string Sha256, long Size);

    private async Task RestorePartialDownloadAsync()
    {
        if (_available is not { } release || !Directory.Exists(_workRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(_workRoot).Take(256))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                PortableUpdatePaths.EnsureNoReparsePoints(directory);
                var path = Path.Combine(directory, "download.json");
                PortableUpdatePaths.EnsureNoReparsePoints(path);
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) continue;
                var checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(await File.ReadAllTextAsync(path));
                if (checkpoint is null || checkpoint.Sha256 != release.Sha256 || checkpoint.Size != release.Size ||
                    !string.Equals(checkpoint.InstallDirectory, _installDirectory, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(checkpoint.DataDirectory, _dataDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                var partial = Path.Combine(directory, ".update.zip.download-" + release.Sha256[..32].ToLowerInvariant());
                PortableUpdatePaths.EnsureNoReparsePoints(partial);
                if (!File.Exists(partial) || new FileInfo(partial).Length > release.Size) continue;
                _workDirectory = directory;
                DownloadProgress = 100d * new FileInfo(partial).Length / release.Size;
                IsPaused = true;
                Status = DesktopText.Get("PortableUpdatePaused", DownloadProgress / 100 * release.Size / (1024d * 1024d), release.Size / (1024d * 1024d));
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
    }

    private async Task RestoreStageAsync()
    {
        if (!Directory.Exists(_workRoot)) return;
        try { PortableUpdatePaths.EnsureNoReparsePoints(_workRoot); }
        catch (InvalidDataException) { return; }

        foreach (var directory in Directory.EnumerateDirectories(_workRoot))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            try
            {
                PortableUpdatePaths.EnsureNoReparsePoints(directory);
                if (PortableUpdateStageStore.IsCompleted(directory))
                {
                    // Keep logs/result records; background cleanup reclaims only payload files.
                    continue;
                }
                if (_available is null) continue;
                var restored = await PortableUpdateStageStore.LoadAsync(directory, _installDirectory, _dataDirectory,
                    _currentVersion, Architecture(), IncludePrereleases, _available, _lifetimeCancellation.Token);
                if (restored is null) continue;
                _staged = restored.Staged;
                _workDirectory = restored.WorkDirectory;
                _available = restored.Release;
                _restoredStage = true;
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or NotSupportedException or OperationCanceledException or FormatException or InvalidOperationException)
            {
                // Unknown recovery states must not be destroyed just because restoration
                // validation failed. The conservative background cleaner handles old payloads.
                Debug.WriteLine($"Update stage restore: {error.GetType().Name}");
            }
        }
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
