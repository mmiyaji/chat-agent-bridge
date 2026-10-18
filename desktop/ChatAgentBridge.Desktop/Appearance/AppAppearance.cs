using ChatAgentBridge.Core.Appearance;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace ChatAgentBridge_Desktop.Appearance;

/// <summary>Applies the saved app theme without changing execution or connection settings.</summary>
public sealed class AppAppearance(AppearancePreferencesStore store) : IDisposable
{
    private readonly UISettings _systemSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private MainWindow? _window;
    private FrameworkElement? _root;
    private bool _closed;
    public string Preference { get; private set; } = "system";
    public bool LoadFailed { get; private set; }
    public event Action? Changed;

    public ElementTheme RequestedTheme => Preference switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => SystemTheme
    };

    private ElementTheme SystemTheme
    {
        get
        {
            // Windows exposes theme-aware foreground colors. A light foreground
            // indicates dark app surfaces; do not change any Windows preferences.
            var color = _systemSettings.GetColorValue(UIColorType.Foreground);
            return 5 * color.G + 2 * color.R + color.B > 8 * 128 ? ElementTheme.Dark : ElementTheme.Light;
        }
    }

    public async Task InitializeAsync()
    {
        try { Preference = (await store.LoadAsync()).Theme; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadFailed = true;
        }
    }

    public void Attach(MainWindow window, FrameworkElement root)
    {
        _window = window;
        _root = root;
        root.ActualThemeChanged += RootThemeChanged;
        _systemSettings.ColorValuesChanged += SystemColorsChanged;
        Apply();
    }

    public async Task SaveAsync(string theme)
    {
        await store.SaveAsync(new AppearancePreferences(theme));
        Preference = theme;
        LoadFailed = false;
        Apply();
    }

    private void SystemColorsChanged(UISettings sender, object args)
    {
        App.DispatcherQueue.TryEnqueue(() => { if (!_closed) Apply(); });
    }

    private void RootThemeChanged(FrameworkElement sender, object args) => UpdateTitleBar();

    private void Apply()
    {
        if (_root is not null) _root.RequestedTheme = RequestedTheme;
        UpdateTitleBar();
        Changed?.Invoke();
    }

    private void UpdateTitleBar()
    {
        if (_window is null || _root is null) return;
        _window.AppWindow.TitleBar.PreferredTheme = _accessibility.HighContrast
            ? TitleBarTheme.UseDefaultAppMode
            : _root.ActualTheme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
    }

    public void Dispose()
    {
        _closed = true;
        _systemSettings.ColorValuesChanged -= SystemColorsChanged;
        if (_root is not null) _root.ActualThemeChanged -= RootThemeChanged;
        _root = null;
        _window = null;
    }
}
