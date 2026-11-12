using ChatAgentBridge.Core.Appearance;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ChatAgentBridge_Desktop.Appearance;

public sealed partial class AppearanceSettings : UserControl
{
    private bool _updating = true;
    private bool _saving;
    private string? _pendingTheme;
    private bool _subscribed;

    public AppearanceSettings()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed) { App.Appearance.Changed += Refresh; _subscribed = true; }
        LanguageSettings.LanguageChanged -= OnLanguageChanged;
        LanguageSettings.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed) { App.Appearance.Changed -= Refresh; _subscribed = false; }
        LanguageSettings.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(System.Globalization.CultureInfo culture) => Refresh();

    private void Refresh()
    {
        _updating = true;
        var selected = (_pendingTheme ?? App.Appearance.Preference) is "light" or "dark" ? _pendingTheme ?? App.Appearance.Preference : "system";
        ThemePicker.SelectedValue = selected;
        _updating = false;
        ThemeStatus.Text = App.Appearance.LoadFailed ? DesktopText.Get("AppearanceReadFailed")
            : App.Appearance.Preference == "system" ? DesktopText.Get("AppearanceFollowsSystem")
            : DesktopText.Get("AppearanceSaved");
        RefreshAccent();
    }

    private const string MoreColorsTag = "__more";
    private bool _accentSaving;

    /// <summary>Windows accent, the basic colors, the current color if it is not basic, then "more colors".</summary>
    private void RefreshAccent()
    {
        _updating = true;
        var current = App.Appearance.Accent;
        AccentPicker.Items.Clear();
        AccentPicker.Items.Add(new ComboBoxItem
        {
            Content = DesktopText.Get("AccentSystemOption"), Tag = AccentPalette.System
        });
        foreach (var color in AccentPalette.Colors.Where(color => color.IsBasic || color.Id == current))
            AccentPicker.Items.Add(new ComboBoxItem { Content = CreateSwatchLabel(color), Tag = color.Id });
        AccentPicker.Items.Add(new ComboBoxItem
        {
            Content = DesktopText.Get("AccentMoreOption", AccentPalette.Colors.Count), Tag = MoreColorsTag
        });
        foreach (var item in AccentPicker.Items.OfType<ComboBoxItem>())
            AutomationProperties.SetAutomationId(item, $"Accent_{item.Tag}");
        AccentPicker.SelectedItem = AccentPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == current)
            ?? AccentPicker.Items[0];
        _updating = false;
        AccentStatus.Text = AccentPalette.Find(current) is { } chosen
            ? DesktopText.Get("AccentSaved", chosen.Label) : DesktopText.Get("AccentFollowsSystem");
    }

    private FrameworkElement CreateSwatchLabel(AccentColor color)
    {
        var rgb = ActualTheme == ElementTheme.Dark ? color.Dark : color.Light;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new Ellipse
        {
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], StrokeThickness = 1
        });
        panel.Children.Add(new TextBlock { Text = color.Label, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private async void AccentPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _accentSaving || AccentPicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var accent = tag == MoreColorsTag ? await ChooseMoreColorAsync() : tag;
        if (accent is null || accent == App.Appearance.Accent) { RefreshAccent(); return; }
        _accentSaving = true;
        try { await App.Appearance.SaveAccentAsync(accent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RefreshAccent();
            AccentStatus.Text = DesktopText.Get("AccentSaveFailed");
            return;
        }
        finally { _accentSaving = false; }
        RefreshAccent();
    }

    /// <summary>All colors in a grid, in HiMemo's hue order. Returns null when cancelled.</summary>
    private async Task<string?> ChooseMoreColorAsync()
    {
        string? chosen = null;
        var grid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 420 };
        AutomationProperties.SetAutomationId(grid, "AccentMoreColors");
        foreach (var color in AccentPalette.Colors)
        {
            var item = new GridViewItem { Content = CreateSwatchLabel(color), Tag = color.Id, MinWidth = 180 };
            AutomationProperties.SetAutomationId(item, $"AccentMore_{color.Id}");
            AutomationProperties.SetName(item, color.Label);
            grid.Items.Add(item);
        }
        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("AccentMoreTitle"),
            Content = grid,
            CloseButtonText = DesktopText.Get("AccentMoreCancel"),
            XamlRoot = XamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme
        };
        grid.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is GridViewItem { Tag: string id }) chosen = id;
            else if (args.ClickedItem is FrameworkElement { Parent: GridViewItem { Tag: string parentId } }) chosen = parentId;
            dialog.Hide();
        };
        await dialog.ShowAsync();
        return chosen;
    }

    private async void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || ThemePicker.SelectedValue is not string theme) return;
        _pendingTheme = theme;
        if (_saving) return;
        _saving = true;
        // Keep the focused radio group alive while WinUI completes its selection
        // event. Serialize saves and retain the latest choice during a pending write.
        try
        {
            while (_pendingTheme is { } requested)
            {
                _pendingTheme = null;
                if (requested != App.Appearance.Preference)
                    await App.Appearance.SaveAsync(requested);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _pendingTheme = null;
            Refresh();
            ThemeStatus.Text = DesktopText.Get("AppearanceSaveFailed");
        }
        finally { _saving = false; }
    }
}
