using System.Globalization;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Resources;

namespace ChatAgentBridge_Desktop.Diagrams;

public sealed partial class ArchitecturePreview : UserControl
{
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Localization.DesktopStrings", typeof(ArchitecturePreview).Assembly);
    private string? _accessibleName;
    private bool _subscribed;
    private string? _asset;

    public static readonly DependencyProperty MaxPreviewHeightProperty = DependencyProperty.Register(
        nameof(MaxPreviewHeight), typeof(double), typeof(ArchitecturePreview), new PropertyMetadata(260d));

    public double MaxPreviewHeight
    {
        get => (double)GetValue(MaxPreviewHeightProperty);
        set => SetValue(MaxPreviewHeightProperty, value);
    }

    public ArchitecturePreview()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed)
        {
            LanguageSettings.LanguageChanged += OnLanguageChanged;
            _subscribed = true;
        }
        RefreshLanguage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed)
        {
            LanguageSettings.LanguageChanged -= OnLanguageChanged;
            _subscribed = false;
        }
    }

    private void OnLanguageChanged(CultureInfo culture) => RefreshLanguage(culture);

    public void RefreshLanguage(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var asset = LocalizedAssets.GetOverviewArchitecture(culture);
        if (_asset != asset)
        {
            DiagramImage.Source = new SvgImageSource(new Uri(asset));
            _asset = asset;
        }
        OpenHint.Text = Text("ArchitectureOpenHint", culture);
        AutomationProperties.SetName(OpenButton,
            _accessibleName is null ? Text("ArchitectureOpenAccessibleName", culture) :
                Text("ArchitectureOpenNamedAccessibleName", culture, _accessibleName));
        AutomationProperties.SetHelpText(OpenButton, Text("ArchitectureOpenHelp", culture));
    }

    public void SetAccessibleName(string name, CultureInfo? culture = null)
    {
        _accessibleName = name;
        RefreshLanguage(culture);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs args)
    {
        if (XamlRoot is null || ArchitectureViewer.IsOpen) return;
        await ArchitectureViewer.ShowAsync(XamlRoot, OpenButton, _accessibleName);
    }

    private static string Text(string key, CultureInfo culture, params object?[] args)
    {
        var value = Strings.GetString(key, culture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }
}
