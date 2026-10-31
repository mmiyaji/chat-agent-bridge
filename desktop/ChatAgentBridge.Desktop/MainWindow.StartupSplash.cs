using System.Diagnostics;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainWindow
{
    // A brief floor avoids a single-frame flash; long loads incur no extra delay.
    private static readonly TimeSpan StartupSplashMinimumDuration = TimeSpan.FromMilliseconds(400);
    private readonly Stopwatch _startupSplashClock = new();
    private readonly Storyboard _startupRingAnimation = new();
    private readonly Storyboard _startupFadeAnimation = new();
    private Task? _startupIconTask;

    private void InitializeStartupSplash()
    {
        StartupSplashMessage.Text = DesktopText.Get("StartupLoading");
        _startupIconTask = LoadStartupSplashIconAsync();
        // Keep a static outline when Windows animations are disabled.
        if (new UISettings().AnimationsEnabled)
        {
            var rotation = new DoubleAnimation
            {
                From = 0, To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(1600)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(rotation, StartupSplashRotation);
            Storyboard.SetTargetProperty(rotation, "Angle");
            _startupRingAnimation.Children.Add(rotation);
            _startupRingAnimation.Begin();
        }
    }

    private async Task CompleteStartupSplashAsync(MainPage page)
    {
        // Readiness excludes network connections and other background services.
        await page.InitialContentReady.WaitAsync(_startupLifetime.Token);
        var remaining = StartupSplashMinimumDuration - _startupSplashClock.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, _startupLifetime.Token);
        _startupLifetime.Token.ThrowIfCancellationRequested();
        await FadeOutStartupSplashAsync();
        HideStartupSplash();
    }

    private async Task ShowStartupSplashFrameAsync()
    {
        if (_startupSplashClock.IsRunning) return;
        if (_startupIconTask is { } iconTask)
            await iconTask.WaitAsync(_startupLifetime.Token);
        // Reveal the entire group only after decoding the bundled icon, avoiding
        // a ring/title followed by a separate, late image pop-in.
        StartupSplashContent.Opacity = 1;
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => rendered.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        try
        {
            // Loaded precedes the first paint. Yield until the splash can actually
            // be presented before constructing the much larger main-page tree.
            await rendered.Task.WaitAsync(_startupLifetime.Token);
            _startupSplashClock.Restart();
            await Task.Yield();
        }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private void HideStartupSplash()
    {
        _startupRingAnimation.Stop();
        _startupFadeAnimation.Stop();
        StartupSplashShell.Visibility = Visibility.Collapsed;
        RootFrame.Opacity = 1;
        RootFrame.IsHitTestVisible = true;
        RootFrame.IsEnabled = true;
    }

    private async Task LoadStartupSplashIconAsync()
    {
        try
        {
            // An absolute bundled path works for both unpackaged portable and Store builds.
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(
                Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png"));
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!_startupLifetime.IsCancellationRequested) StartupSplashIcon.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // A missing/damaged decorative asset must not prevent access to the app.
        }
    }

    private async Task FadeOutStartupSplashAsync()
    {
        if (!new UISettings().AnimationsEnabled) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, object args) => completed.TrySetResult();
        _startupFadeAnimation.Children.Clear();
        foreach (var (target, from, to) in new[]
                 { ((DependencyObject)RootFrame, 0d, 1d), ((DependencyObject)StartupSplashShell, 1d, 0d) })
        {
            var opacity = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
            Storyboard.SetTarget(opacity, target);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            _startupFadeAnimation.Children.Add(opacity);
        }
        RootFrame.IsEnabled = true;
        _startupFadeAnimation.Completed += OnCompleted;
        try
        {
            _startupFadeAnimation.Begin();
            await completed.Task.WaitAsync(_startupLifetime.Token);
        }
        finally
        {
            _startupFadeAnimation.Completed -= OnCompleted;
            _startupFadeAnimation.Stop();
        }
    }
}
