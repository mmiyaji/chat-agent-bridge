using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private readonly DispatcherTimer _progressPreviewTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _progressPreviewStage = "received";
    private void ProgressPreviewFlyout_Opening(object? sender, object e)
    {
        _progressPreviewTimer.Stop();
        ProgressPreviewLevel.SelectedValue = NormalizePreviewLevel(ViewModel.ChatProgressDetail);
        ProgressPreviewStage("received");
        if (XamlRoot is { } root)
        {
            ProgressPreviewRoot.Width = Math.Max(200, Math.Min(440, root.Size.Width - 64));
            ProgressPreviewScrollViewer.MaxHeight = Math.Max(180, Math.Min(620, root.Size.Height - 120));
        }
    }

    private void ProgressPreviewFlyout_Opened(object? sender, object e)
    {
        if (new UISettings().AnimationsEnabled)
            StartProgressPreview();
    }

    private void ProgressPreviewFlyout_Closed(object? sender, object e) => _progressPreviewTimer.Stop();

    private void CloseProgressPreviewOnNavigation()
    {
        _progressPreviewTimer.Stop();
        if (ProgressPreviewFlyout.IsOpen) ProgressPreviewFlyout.Hide();
    }

    private void ProgressPreviewAnchor_Unloaded(object sender, RoutedEventArgs e)
    {
        _progressPreviewTimer.Stop();
        if (ProgressPreviewFlyout.IsOpen) ProgressPreviewFlyout.Hide();
    }

    private void ProgressPreviewLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _progressPreviewTimer.Stop();
        UpdateProgressPreviewMessage();
    }

    private void ProgressPreviewStage_Click(object sender, RoutedEventArgs e)
    {
        _progressPreviewTimer.Stop();
        if (sender is Button { Tag: string stage }) ProgressPreviewStage(stage);
    }

    private void ProgressPreviewPlay_Click(object sender, RoutedEventArgs e) => StartProgressPreview();

    private void ProgressPreviewClose_Click(object sender, RoutedEventArgs e) => ProgressPreviewFlyout.Hide();

    private void StartProgressPreview()
    {
        _progressPreviewTimer.Stop();
        ProgressPreviewStage("received");
        _progressPreviewTimer.Tick -= ProgressPreviewTimer_Tick;
        _progressPreviewTimer.Tick += ProgressPreviewTimer_Tick;
        _progressPreviewTimer.Start();
    }

    private void ProgressPreviewTimer_Tick(object? sender, object e)
    {
        var next = _progressPreviewStage switch
        {
            "received" => "running",
            "running" => "completed",
            _ => null
        };
        if (next is null)
        {
            _progressPreviewTimer.Stop();
            return;
        }
        ProgressPreviewStage(next);
        if (next == "completed") _progressPreviewTimer.Stop();
    }

    private void ProgressPreviewStage(string stage)
    {
        _progressPreviewStage = stage;
        ProgressPreviewStatus.Text = DesktopText.Get(stage switch
        {
            "running" => "ProgressPreviewRunningStatus",
            "completed" => "ProgressPreviewCompletedStatus",
            _ => "ProgressPreviewReceivedStatus"
        });
        UpdateProgressPreviewMessage();
    }

    private void UpdateProgressPreviewMessage()
    {
        var level = ProgressPreviewLevel.SelectedValue as string ?? "standard";
        var detailed = level == "detailed";
        var minimal = level == "minimal";
        var key = _progressPreviewStage switch
        {
            "running" => detailed ? "ProgressRunning" : "ProgressRunningShort",
            "completed" => detailed ? "ProgressCompleted" : "ProgressCompletedShort",
            _ => detailed ? "ProgressReceived" : "ProgressReceivedShort"
        };
        var headline = DesktopText.Get(key, _progressPreviewStage == "running" ? "00:12" : "00:15");
        if (minimal)
        {
            ProgressPreviewMessage.Text = headline;
            return;
        }
        if (level == "standard")
        {
            ProgressPreviewMessage.Text = _progressPreviewStage switch
            {
                "running" => string.Join(" · ", headline, DesktopText.Get("ProgressActivityCountShort", 3)),
                "completed" => string.Join(" · ", headline, DesktopText.Get("ProgressActivityCountShort", 3), DesktopText.Get("ProgressUsageShort", "1,240", "386")),
                _ => headline
            };
            return;
        }
        var lines = new List<string> { headline };
        if (_progressPreviewStage != "received")
        {
            lines.Add(DesktopText.Get("ProgressDetails", DesktopText.Get("ProgressPreviewAgent"), DesktopText.Get("ProgressPreviewModel"), DesktopText.Get("ProgressPreviewPermission")));
            if (_progressPreviewStage == "running")
            {
                if (ViewModel.ChatResultMode == "auto")
                    lines.Add("• " + DesktopText.Get("ProgressPreviewActivity"));
                else
                    lines.Add(DesktopText.Get("ProgressActivityCount", 3));
                lines.Add(DesktopText.Get("ProgressFollowUpQueueHint"));
            }
            else
            {
                lines.Add(DesktopText.Get("ProgressActivityCount", 3));
                lines.Add(DesktopText.Get("ProgressUsage", "1,240", "386"));
            }
        }
        ProgressPreviewMessage.Text = string.Join(Environment.NewLine, lines);
    }

    private static string NormalizePreviewLevel(string? value) => ChatProgressDetail.Normalize(value);
}
