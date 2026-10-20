using System.Globalization;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Resources;

namespace ChatAgentBridge_Desktop.Diagrams;

internal static class ArchitectureViewer
{
    private const double DiagramWidth = 1080;
    private const double DiagramHeight = 500;
    private const double MaximumRasterSize = 4096;
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Localization.DesktopStrings", typeof(ArchitectureViewer).Assembly);
    private static bool _isOpen;

    public static bool IsOpen => _isOpen;

    public static async Task ShowAsync(XamlRoot root, Button opener, string? accessibleName)
    {
        if (_isOpen) return;
        _isOpen = true;
        try
        {
            await ShowCoreAsync(root, accessibleName);
        }
        catch (Exception ex)
        {
            // A background approval may have opened another dialog in the meantime.
            // Leave the active dialog and the setup form intact if presentation fails.
            System.Diagnostics.Debug.WriteLine($"Architecture viewer: {ex.GetType().Name}");
        }
        finally
        {
            _isOpen = false;
            if (opener.XamlRoot is not null)
                opener.Focus(FocusState.Programmatic);
        }
    }

    private static async Task ShowCoreAsync(XamlRoot root, string? accessibleName)
    {
        var culture = CultureInfo.CurrentUICulture;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = Text("ArchitectureDialogTitle", culture),
            CloseButtonText = Text("ArchitectureDialogClose", culture),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "ArchitectureViewerDialog");
        dialog.Resources["ContentDialogMaxWidth"] = Math.Max(360, Math.Min(1328, root.Size.Width - 32));

        var zoomOut = MakeButton("−", "ArchitectureZoomOut", "ArchitectureZoomOutName", culture);
        var zoomLevel = new TextBlock
        {
            MinWidth = 56,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = "100%"
        };
        AutomationProperties.SetAutomationId(zoomLevel, "ArchitectureZoomLevel");
        AutomationProperties.SetLiveSetting(zoomLevel, AutomationLiveSetting.Polite);
        var zoomIn = MakeButton("+", "ArchitectureZoomIn", "ArchitectureZoomInName", culture);
        var fit = MakeButton(Text("ArchitectureFit", culture), "ArchitectureFit", "ArchitectureFitName", culture);
        var actualSize = MakeButton(Text("ArchitectureActualSize", culture), "ArchitectureActualSize", "ArchitectureActualSizeName", culture);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        toolbar.Children.Add(zoomOut);
        toolbar.Children.Add(zoomLevel);
        toolbar.Children.Add(zoomIn);
        toolbar.Children.Add(fit);
        toolbar.Children.Add(actualSize);

        var image = new Image
        {
            Width = DiagramWidth,
            Height = DiagramHeight,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
            Source = new SvgImageSource(new Uri(LocalizedAssets.GetOverviewArchitecture(culture)))
        };
        AutomationProperties.SetAutomationId(image, "ArchitectureViewerImage");
        AutomationProperties.SetName(image, accessibleName ?? Text("ArchitectureImageName", culture));
        var viewport = new ScrollViewer
        {
            Name = "ArchitectureViewport",
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f,
            MaxZoomFactor = 4f,
            Content = image
        };
        AutomationProperties.SetAutomationId(viewport, "ArchitectureViewport");
        var error = new TextBlock
        {
            Text = Text("ArchitectureLoadError", culture),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetAutomationId(error, "ArchitectureLoadError");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(toolbar);
        body.Children.Add(viewport);
        body.Children.Add(error);
        dialog.Content = body;

        var fitMode = true;
        var opening = true;
        var closed = false;
        void UpdateDialogSize()
        {
            var width = Math.Max(240, Math.Min(1280, root.Size.Width - 96));
            var height = Math.Max(120, Math.Min(640, root.Size.Height - 260));
            dialog.Resources["ContentDialogMaxWidth"] = width + 48;
            dialog.Resources["ContentDialogMaxHeight"] = Math.Max(240, root.Size.Height - 24);
            viewport.Width = width;
            viewport.Height = height;
        }
        void UpdateZoomLabel()
        {
            zoomLevel.Text = viewport.ZoomFactor.ToString("P0", culture);
            AutomationProperties.SetName(zoomLevel, Text("ArchitectureZoomLevelName", culture, zoomLevel.Text));
        }
        void UpdateRasterization()
        {
            var factor = Math.Max(0.1, viewport.ZoomFactor);
            if (image.Source is SvgImageSource svg)
            {
                svg.RasterizePixelWidth = Math.Min(MaximumRasterSize, Math.Ceiling(DiagramWidth * factor));
                svg.RasterizePixelHeight = Math.Ceiling(svg.RasterizePixelWidth * DiagramHeight / DiagramWidth);
            }
            UpdateZoomLabel();
        }
        void FitToViewport()
        {
            fitMode = true;
            var factor = Math.Min(viewport.ViewportWidth / DiagramWidth, viewport.ViewportHeight / DiagramHeight);
            if (double.IsFinite(factor) && factor > 0)
                viewport.ChangeView(null, null, (float)Math.Clamp(factor, 0.1, 4), true);
        }
        void ZoomBy(double ratio)
        {
            fitMode = false;
            var current = viewport.ZoomFactor;
            var next = Math.Clamp(current * ratio, 0.1, 4);
            var scale = next / current;
            var horizontalCenter = viewport.HorizontalOffset + viewport.ViewportWidth / 2;
            var verticalCenter = viewport.VerticalOffset + viewport.ViewportHeight / 2;
            viewport.ChangeView(
                Math.Max(0, horizontalCenter * scale - viewport.ViewportWidth / 2),
                Math.Max(0, verticalCenter * scale - viewport.ViewportHeight / 2),
                (float)next,
                true);
        }
        void OnViewportViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            UpdateZoomLabel();
            if (args.IsIntermediate) return;
            UpdateRasterization();
            if (opening)
            {
                opening = false;
                FitToViewport();
            }
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            UpdateDialogSize();
            viewport.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closed) return;
                viewport.UpdateLayout();
                if (fitMode) FitToViewport();
            });
        }

        zoomOut.Click += (_, _) => ZoomBy(1 / 1.2);
        zoomIn.Click += (_, _) => ZoomBy(1.2);
        fit.Click += (_, _) => FitToViewport();
        actualSize.Click += (_, _) =>
        {
            fitMode = false;
            viewport.ChangeView(viewport.HorizontalOffset, viewport.VerticalOffset, 1f, true);
        };
        viewport.ViewChanged += OnViewportViewChanged;
        root.Changed += OnRootChanged;
        void OnImageFailed(object sender, ExceptionRoutedEventArgs args) => error.Visibility = Visibility.Visible;
        image.ImageFailed += OnImageFailed;
        UpdateDialogSize();
        UpdateZoomLabel();

        dialog.Opened += (_, _) => viewport.DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            viewport.UpdateLayout();
            FitToViewport();
        });
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            root.Changed -= OnRootChanged;
            viewport.ViewChanged -= OnViewportViewChanged;
            image.ImageFailed -= OnImageFailed;
            closed = true;
        }
    }

    private static Button MakeButton(string label, string automationId, string nameKey, CultureInfo culture)
    {
        var button = new Button { Content = label, MinWidth = 40, Padding = new Thickness(12, 8, 12, 8) };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, Text(nameKey, culture));
        return button;
    }

    private static string Text(string key, CultureInfo culture, params object?[] args)
    {
        var value = Strings.GetString(key, culture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }
}
