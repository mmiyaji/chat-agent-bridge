using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Migrations;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _startupLifetime = new();
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _startupInitializationTask;
    private bool _startupBusy;
    private CancellationTokenSource? _startupAttemptLifetime;
    private bool _startupAttemptFailed;
    private bool _startupRestartRequired;
    private bool _startupRestarting;
    private CancellationToken StartupAttemptToken => _startupAttemptLifetime?.Token ?? _startupLifetime.Token;
    public Task StartupReady => _startupReady.Task;
    internal bool CanStartChatAfterStartup => _startupReady.Task.IsCompletedSuccessfully && !_startupAttemptFailed;

    private void StartupRoot_Loaded(object sender, RoutedEventArgs args)
    {
        WindowRoot.Loaded -= StartupRoot_Loaded;
        _startupInitializationTask = InitializeAfterMigrationsAsync();
    }

    private async Task InitializeAfterMigrationsAsync()
    {
        if (_startupBusy || RootFrame.Content is MainPage || _startupLifetime.IsCancellationRequested) return;
        _startupBusy = true;
        _startupAttemptFailed = false;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_startupLifetime.Token);
        _startupAttemptLifetime = attempt;
        var timeout = TimeSpan.FromSeconds(30);
#if CAB_STARTUP_FIXTURE
        timeout = Startup.StartupFailureFixture.Timeout;
#endif
        attempt.CancelAfter(timeout);
        var token = attempt.Token;
        var creatingPage = false;
        var migrationDone = false;
        StartupMigrationRetry.IsEnabled = false;
        StartupMigrationError.IsOpen = false;
        StartupMigrationTitle.Visibility = Visibility.Collapsed;
        StartupMigrationActions.Visibility = Visibility.Collapsed;
        StartupSplashMessage.Text = DesktopText.Get("StartupLoading");
        StartupMigrationExit.Content = DesktopText.Get("MigrationExit");
        StartupMigrationRetry.Content = DesktopText.Get("MigrationRetry");
        try
        {
            PrepareStartupSplashAttempt();
            await ShowStartupSplashFrameAsync();
            var progress = new Progress<MigrationProgress>(value =>
            {
                // A slow migration is fine while it keeps reporting; only silence counts as a stall.
                if (!migrationDone)
                {
                    try { attempt.CancelAfter(timeout); }
                    catch (ObjectDisposedException) { }
                }
                if (token.IsCancellationRequested || _startupAttemptFailed || RootFrame.Content is MainPage) return;
                StartupSplashMessage.Text = DesktopText.Get("MigrationTitle");
            });
            var migration = new DataMigrationCoordinator(new SettingsStore().DirectoryPath, ApplicationDataMigrations.Create());
#if CAB_STARTUP_FIXTURE
            await Startup.StartupFailureFixture.BeforeMigrationAsync(progress).WaitAsync(token);
#endif
            var running = migration.RunAsync(progress, token);
            await running.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            // Building the page gets its own fixed budget, independent of how long the migration took.
            migrationDone = true;
            attempt.CancelAfter(timeout);
            StartupSplashMessage.Text = DesktopText.Get("StartupLoading");
            creatingPage = true;
            if (!RootFrame.Navigate(typeof(MainPage))) throw new InvalidOperationException("Main page navigation failed.");
            if (Page is { } page)
            {
                page.SettingsSearchRequested += (_, _) => ShowSettingsSearch();
                page.ViewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(page.ViewModel.IsOnboardingActive) or nameof(page.ViewModel.IsSettingsLoaded))
                        UpdateSettingsSearchVisibility();
                };
                await CompleteStartupSplashAsync(page);
            }
            token.ThrowIfCancellationRequested();
            attempt.CancelAfter(Timeout.InfiniteTimeSpan);
            UpdateSettingsSearchVisibility();
            _startupReady.TrySetResult();
        }
        catch (OperationCanceledException) when (_startupLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _startupRestartRequired = true;
            ShowStartupFailure("StartupTimeoutTitle", "StartupTimeout");
        }
        catch (Exception)
        {
            _startupRestartRequired = creatingPage;
            ShowStartupFailure(creatingPage ? "StartupFailedTitle" : "MigrationFailedTitle",
                creatingPage ? "StartupFailed" : "MigrationFailed");
        }
        finally
        {
            _startupAttemptLifetime = null;
            _startupBusy = false;
        }
    }

    private void ShowStartupFailure(string title, string message)
    {
        _startupAttemptFailed = true;
        if (SettingsSearchFlyout.IsOpen) SettingsSearchFlyout.Hide();
        HideStartupSplash();
        // Do not expose a partially initialized page or allow commands behind the error.
        RootFrame.Visibility = Visibility.Collapsed;
        RootFrame.IsEnabled = false;
        StartupMigrationShell.Visibility = Visibility.Visible;
        StartupMigrationTitle.Visibility = Visibility.Visible;
        StartupMigrationActions.Visibility = Visibility.Visible;
        StartupMigrationTitle.Text = DesktopText.Get(title);
        StartupMigrationError.Message = DesktopText.Get(message);
        StartupMigrationError.IsOpen = true;
        StartupMigrationRetry.Visibility = Visibility.Visible;
        StartupMigrationRetry.IsEnabled = true;
    }

    private void CancelStartupMigration()
    {
        _startupLifetime.Cancel();
        _startupRingAnimation.Stop();
        _startupFadeAnimation.Stop();
        _startupReady.TrySetCanceled();
    }

    private void StartupMigrationRetry_Click(object sender, RoutedEventArgs args)
    {
        if (_startupBusy || _startupRestarting) return;
        if (_startupRestartRequired)
        {
            _startupRestarting = true;
            StartupMigrationRetry.IsEnabled = false;
            try
            {
                var directory = new SettingsStore().DirectoryPath.TrimEnd('\\', '/');
                if (directory.EndsWith(':')) directory += "\\.";
                Deployment.SingleInstanceGate.Release();
                _ = Microsoft.Windows.AppLifecycle.AppInstance.Restart($"--data-dir \"{directory}\"");
                Deployment.SingleInstanceGate.Reclaim();
                StartupMigrationError.Message = DesktopText.Get("StartupRestartFailed");
            }
            catch (Exception)
            {
                Deployment.SingleInstanceGate.Reclaim();
                StartupMigrationError.Message = DesktopText.Get("StartupRestartFailed");
            }
            finally { _startupRestarting = false; StartupMigrationRetry.IsEnabled = true; }
            return;
        }
        RootFrame.Visibility = Visibility.Visible;
        _startupInitializationTask = InitializeAfterMigrationsAsync();
    }

    private async void StartupMigrationExit_Click(object sender, RoutedEventArgs args) => await App.Tray.ExitAsync();
}
