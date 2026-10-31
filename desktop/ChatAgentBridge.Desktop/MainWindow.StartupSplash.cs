using System.Diagnostics;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainWindow
{
    // A brief floor avoids a single-frame flash; long loads incur no extra delay.
    private static readonly TimeSpan StartupSplashMinimumDuration = TimeSpan.FromMilliseconds(400);
    private readonly Stopwatch _startupSplashClock = new();
    private readonly Storyboard _startupRingAnimation = new();

    private void InitializeStartupSplash()
    {
        StartupSplashMessage.Text = DesktopText.Get("StartupLoading");
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
        HideStartupSplash();
    }

    private async Task ShowStartupSplashFrameAsync()
    {
        if (_startupSplashClock.IsRunning) return;
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
        StartupSplashShell.Visibility = Visibility.Collapsed;
        RootFrame.Opacity = 1;
        RootFrame.IsHitTestVisible = true;
        RootFrame.IsEnabled = true;
    }
}
