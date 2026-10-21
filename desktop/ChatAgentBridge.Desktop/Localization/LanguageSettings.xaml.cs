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

    public Func<bool>? CanRestartNow { get; set; }
    public event Action? RestartRequested;

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
            if (ApplyImmediately)
                SetStatus(string.Empty, visible: false);
            else
                SetStatus(AppLanguage.Select(language, App.SystemLanguageTag).Name == CultureInfo.CurrentUICulture.Name
                        ? string.Format(CultureInfo.CurrentCulture, Text("CurrentLanguage"), AppLanguage.IsJapanese ? "日本語" : "English")
                        : Text("RestartRequired"));
            LanguagePicker.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(Text("ReadFailed"));
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
            SetStatus(string.Empty, visible: false);
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
            SetStatus(ApplyImmediately ? string.Empty : Text("RestartRequired"), visible: !ApplyImmediately);
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

    private void SetStatus(string message, bool visible = true)
    {
        LanguageStatus.Text = message;
        LanguageStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowRestartDialogAsync()
    {
        var canRestart = CanRestartNow?.Invoke() == true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = Text("RestartDialogTitle"),
            Content = canRestart
                ? Text("RestartDialogMessage")
                : $"{Text("RestartDialogMessage")}\n\n{Text("RestartDialogBusy")}",
            PrimaryButtonText = Text("RestartDialogRestartNow"),
            CloseButtonText = Text("RestartDialogLater"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = canRestart
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "LanguageRestartDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (CanRestartNow?.Invoke() == true) return;
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.Content = $"{Text("RestartDialogMessage")}\n\n{Text("RestartDialogBusy")}";
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            RestartRequested?.Invoke();
    }

    private async void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _saving || _applyingProgrammaticSelection ||
            LanguagePicker.SelectedValue is not string language || language == _savedLanguage) return;
        _saving = true;
        // Keep an open popup enabled until WinUI commits its selection on close.
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
                SetStatus(Text("RestartRequired"));
                await ShowRestartDialogAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _loading = true;
            SetSelectedLanguage(_savedLanguage);
            _loading = false;
            SetStatus(Text("SaveFailed"));
        }
        finally { _saving = false; }
    }
}
