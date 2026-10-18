using System.Globalization;
using System.Resources;
using ChatAgentBridge.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Localization;

public sealed partial class LanguageSettings : UserControl
{
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Localization.LanguageUi", typeof(LanguageSettings).Assembly);
    private static event Action<string>? PreferenceSaved;
    private readonly LanguagePreferencesStore _store = new(new ChatAgentBridge.Core.SettingsStore());
    private bool _loading = true;
    private bool _saving;
    private bool _subscribed;
    private string _savedLanguage = "system";

    public LanguageSettings()
    {
        InitializeComponent();
        SystemLanguageOption.Content = Text("SystemDefault");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string Text(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed) { PreferenceSaved += OnPreferenceSaved; _subscribed = true; }
        _loading = true;
        try
        {
            var preference = await _store.LoadAsync();
            _savedLanguage = preference.Language;
            LanguagePicker.SelectedValue = preference.Language;
            LanguageStatus.Text = AppLanguage.Select(preference.Language, App.SystemLanguageTag).Name == CultureInfo.CurrentUICulture.Name
                ? string.Format(CultureInfo.CurrentCulture, Text("CurrentLanguage"), AppLanguage.IsJapanese ? "日本語" : "English")
                : Text("RestartRequired");
            LanguagePicker.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            LanguageStatus.Text = Text("ReadFailed");
            LanguagePicker.IsEnabled = false;
        }
        finally { _loading = false; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed) { PreferenceSaved -= OnPreferenceSaved; _subscribed = false; }
    }

    private void OnPreferenceSaved(string language)
    {
        _loading = true;
        _savedLanguage = language;
        LanguagePicker.SelectedValue = language;
        LanguageStatus.Text = Text("RestartRequired");
        _loading = false;
    }

    private async void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _saving || LanguagePicker.SelectedValue is not string language || language == _savedLanguage) return;
        _saving = true;
        LanguagePicker.IsEnabled = false;
        try
        {
            await _store.SaveAsync(new LanguagePreferences(language));
            _savedLanguage = language;
            PreferenceSaved?.Invoke(language);
            LanguageStatus.Text = Text("RestartRequired");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _loading = true;
            LanguagePicker.SelectedValue = _savedLanguage;
            _loading = false;
            LanguageStatus.Text = Text("SaveFailed");
        }
        finally { _saving = false; LanguagePicker.IsEnabled = true; }
    }
}
