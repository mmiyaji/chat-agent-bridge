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
    public Task StartupReady => _startupReady.Task;

    private void StartupRoot_Loaded(object sender, RoutedEventArgs args)
    {
        WindowRoot.Loaded -= StartupRoot_Loaded;
        _startupInitializationTask = InitializeAfterMigrationsAsync();
    }

    private async Task InitializeAfterMigrationsAsync()
    {
        if (_startupBusy || RootFrame.Content is MainPage || _startupLifetime.IsCancellationRequested) return;
        _startupBusy = true;
        StartupMigrationRetry.IsEnabled = false;
        StartupMigrationError.IsOpen = false;
        StartupMigrationRing.IsActive = true;
        StartupMigrationLoading.Visibility = Visibility.Visible;
        StartupMigrationTitle.Visibility = Visibility.Collapsed;
        StartupMigrationActions.Visibility = Visibility.Collapsed;
        StartupMigrationProgress.Text = DesktopText.Get("MigrationChecking");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StartupMigrationRing, StartupMigrationProgress.Text);
        StartupMigrationExit.Content = DesktopText.Get("MigrationExit");
        StartupMigrationRetry.Content = DesktopText.Get("MigrationRetry");
        try
        {
            await ShowStartupSplashFrameAsync();
            var progress = new Progress<MigrationProgress>(value =>
            {
                if (_startupLifetime.IsCancellationRequested || RootFrame.Content is MainPage) return;
                StartupMigrationShell.Visibility = Visibility.Visible;
                StartupMigrationProgress.Text = DesktopText.Get("MigrationTitle");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StartupMigrationRing, StartupMigrationProgress.Text);
            });
            var migration = new DataMigrationCoordinator(new SettingsStore().DirectoryPath, ApplicationDataMigrations.Create());
            var running = migration.RunAsync(progress, _startupLifetime.Token);
            // Fast checks never flash an overlay. Slow preflight/validation still has feedback.
            if (await Task.WhenAny(running, Task.Delay(200, _startupLifetime.Token)) != running &&
                !_startupLifetime.IsCancellationRequested)
                StartupMigrationShell.Visibility = Visibility.Visible;
            await running;
            if (_startupLifetime.IsCancellationRequested) return;
            StartupMigrationShell.Visibility = Visibility.Collapsed;
            StartupMigrationRing.IsActive = false;
            RootFrame.Navigate(typeof(MainPage));
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
            UpdateSettingsSearchVisibility();
            _startupReady.TrySetResult();
        }
        catch (OperationCanceledException) when (_startupLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            HideStartupSplash();
            StartupMigrationShell.Visibility = Visibility.Visible;
            StartupMigrationRing.IsActive = false;
            StartupMigrationLoading.Visibility = Visibility.Collapsed;
            StartupMigrationTitle.Visibility = Visibility.Visible;
            StartupMigrationActions.Visibility = Visibility.Visible;
            StartupMigrationTitle.Text = DesktopText.Get("MigrationFailedTitle");
            StartupMigrationError.Message = DesktopText.Get("MigrationFailed");
            StartupMigrationError.IsOpen = true;
            StartupMigrationRetry.Visibility = Visibility.Visible;
            StartupMigrationRetry.IsEnabled = true;
        }
        finally { _startupBusy = false; }
    }

    private void CancelStartupMigration()
    {
        _startupLifetime.Cancel();
        _startupRingAnimation.Stop();
        _startupReady.TrySetCanceled();
    }

    private void StartupMigrationRetry_Click(object sender, RoutedEventArgs args)
    {
        if (!_startupBusy) _startupInitializationTask = InitializeAfterMigrationsAsync();
    }

    private async void StartupMigrationExit_Click(object sender, RoutedEventArgs args) => await App.Tray.ExitAsync();
}
