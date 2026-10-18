using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        ThemeChoices.SelectedIndex = (_pendingTheme ?? App.Appearance.Preference) switch { "light" => 1, "dark" => 2, _ => 0 };
        _updating = false;
        ThemeStatus.Text = App.Appearance.LoadFailed ? DesktopText.Get("AppearanceReadFailed")
            : App.Appearance.Preference == "system" ? DesktopText.Get("AppearanceFollowsSystem")
            : DesktopText.Get("AppearanceSaved");
    }

    private async void ThemeChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || ThemeChoices.SelectedItem is not RadioButton { Tag: string theme }) return;
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
