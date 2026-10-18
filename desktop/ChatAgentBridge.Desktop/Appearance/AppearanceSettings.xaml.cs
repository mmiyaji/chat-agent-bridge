using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Appearance;

public sealed partial class AppearanceSettings : UserControl
{
    private bool _updating = true;
    private bool _saving;
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
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed) { App.Appearance.Changed -= Refresh; _subscribed = false; }
    }

    private void Refresh()
    {
        _updating = true;
        ThemeChoices.SelectedIndex = App.Appearance.Preference switch { "light" => 1, "dark" => 2, _ => 0 };
        _updating = false;
        ThemeStatus.Text = App.Appearance.LoadFailed ? DesktopText.Get("AppearanceReadFailed")
            : App.Appearance.Preference == "system" ? DesktopText.Get("AppearanceFollowsSystem")
            : DesktopText.Get("AppearanceSaved");
    }

    private async void ThemeChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _saving || ThemeChoices.SelectedItem is not RadioButton { Tag: string theme } || theme == App.Appearance.Preference) return;
        _saving = true;
        ThemeChoices.IsEnabled = false;
        try { await App.Appearance.SaveAsync(theme); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Refresh();
            ThemeStatus.Text = DesktopText.Get("AppearanceSaveFailed");
        }
        finally { _saving = false; ThemeChoices.IsEnabled = true; }
    }
}
