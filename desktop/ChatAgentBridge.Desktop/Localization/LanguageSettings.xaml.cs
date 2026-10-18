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
    public static event Action<CultureInfo>? LanguageChanged;
    private readonly LanguagePreferencesStore _store = new(new ChatAgentBridge.Core.SettingsStore());
    private bool _loading = true;
    private bool _saving;
    private bool _applyingProgrammaticSelection;
    private long _selectionGeneration;
    private bool _subscribed;
    private long _preferenceGeneration;
    private string _savedLanguage = "system";

    /// <summary>When true, selecting a language updates this process immediately. Used by the initial consent step.</summary>
    public bool ApplyImmediately { get; set; }

    public LanguageSettings()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string Text(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed) { PreferenceSaved += OnPreferenceSaved; _subscribed = true; }
        LanguageChanged -= OnLanguageChanged;
        LanguageChanged += OnLanguageChanged;
        _loading = true;
        LanguagePicker.IsEnabled = false;
        try
        {
            var generationBeforeLoad = _preferenceGeneration;
            var preference = await _store.LoadAsync();
            var language = generationBeforeLoad == _preferenceGeneration ? preference.Language : _savedLanguage;
            _savedLanguage = language;
            SetSelectedLanguage(language);
            if (ApplyImmediately && AppLanguage.Select(language, App.SystemLanguageTag).Name == CultureInfo.CurrentUICulture.Name)
                LanguageStatus.Text = string.Format(CultureInfo.CurrentCulture, Text("AppliedImmediately"), AppLanguage.IsJapanese ? "日本語" : "English");
            else
                LanguageStatus.Text = AppLanguage.Select(language, App.SystemLanguageTag).Name == CultureInfo.CurrentUICulture.Name
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
        LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(CultureInfo culture)
    {
        RefreshLanguage(culture);
    }

    public void RefreshLanguage(CultureInfo culture)
    {
        var wasLoading = _loading;
        var wasApplyingProgrammaticSelection = _applyingProgrammaticSelection;
        _loading = true;
        _applyingProgrammaticSelection = true;
        try
        {
            LanguageStatus.Text = string.Format(culture, Text("AppliedImmediately"), AppLanguage.IsJapanese ? "日本語" : "English");
            SetSelectedLanguage(_savedLanguage);
        }
        finally
        {
            _loading = wasLoading;
            _applyingProgrammaticSelection = wasApplyingProgrammaticSelection;
        }
    }

    private void OnPreferenceSaved(string language)
    {
        _loading = true;
        try
        {
            _preferenceGeneration++;
            _savedLanguage = language;
            SetSelectedLanguage(language);
            LanguageStatus.Text = Text("RestartRequired");
        }
        finally { _loading = false; }
    }

    private void SetSelectedLanguage(string language)
    {
        var generation = ++_selectionGeneration;
        _applyingProgrammaticSelection = true;
        LanguagePicker.SelectedValue = language;
        // WinUI can queue SelectionChanged after a load or synchronization update.
        // Keep that update suppressed until the queued selection has settled.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation == _selectionGeneration) _applyingProgrammaticSelection = false;
        });
    }

    private async void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _saving || _applyingProgrammaticSelection ||
            LanguagePicker.SelectedValue is not string language || language == _savedLanguage) return;
        _saving = true;
        LanguagePicker.IsEnabled = false;
        try
        {
            await _store.SaveAsync(new LanguagePreferences(language));
            _savedLanguage = language;
            PreferenceSaved?.Invoke(language);
            if (ApplyImmediately)
            {
                var culture = App.ApplyLanguagePreference(language);
                LanguageChanged?.Invoke(culture);
                RefreshLanguage(culture);
            }
            else
            {
                LanguageStatus.Text = Text("RestartRequired");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _loading = true;
            SetSelectedLanguage(_savedLanguage);
            _loading = false;
            LanguageStatus.Text = Text("SaveFailed");
        }
        finally { _saving = false; LanguagePicker.IsEnabled = true; }
    }
}
