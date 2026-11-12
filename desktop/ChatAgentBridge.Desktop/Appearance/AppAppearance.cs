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
    /// <summary>"system" for the Windows accent, or an <see cref="AccentPalette"/> id.</summary>
    public string Accent { get; private set; } = AccentPalette.System;
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
        try
        {
            var stored = await store.LoadAsync();
            Preference = stored.Theme;
            Accent = stored.Accent;
            // Before any page is built, so the first ThemeResource lookups already see the chosen accent.
            ApplyAccentResources();
        }
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
        await store.SaveAsync(new AppearancePreferences(theme, Accent));
        Preference = theme;
        LoadFailed = false;
        Apply();
    }

    public async Task SaveAccentAsync(string accent)
    {
        await store.SaveAsync(new AppearancePreferences(Preference, accent));
        Accent = accent;
        LoadFailed = false;
        ApplyAccentResources();
        RefreshThemeResources();
        QueueAccentReapply();
        Changed?.Invoke();
    }

    private static readonly string[] AccentKeys =
    [
        "SystemAccentColorDark3", "SystemAccentColorDark2", "SystemAccentColorDark1", "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"
    ];

    /// <summary>
    /// Overrides the accent shades in the app's Light and Dark theme dictionaries; the Windows accent is used when
    /// none is chosen. High contrast keeps the system colors.
    /// </summary>
    private void ApplyAccentResources()
    {
        var dictionaries = Application.Current.Resources.ThemeDictionaries;
        var color = AccentPalette.Find(Accent);
        foreach (var (key, dark) in new[] { ("Light", false), ("Dark", true) })
        {
            if (!dictionaries.TryGetValue(key, out var value) || value is not ResourceDictionary dictionary) continue;
            if (color is null)
            {
                foreach (var name in AccentKeys) dictionary.Remove(name);
                continue;
            }
            var ramp = AccentPalette.CreateRamp(color, dark);
            uint[] shades = [ramp.Dark3, ramp.Dark2, ramp.Dark1, ramp.Accent, ramp.Light1, ramp.Light2, ramp.Light3];
            for (var index = 0; index < AccentKeys.Length; index++)
                dictionary[AccentKeys[index]] = ToColor(shades[index]);
        }
        RecolorControlBrushes(color);

        static Windows.UI.Color ToColor(uint rgb, byte alpha = 0xFF) =>
            Windows.UI.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private readonly Dictionary<(string Owner, object Theme, string Key), Windows.UI.Color> _originalBrushColors = [];

    /// <summary>
    /// WinUI's accent brushes live in XamlControlsResources and are bound to the system colors there, so overriding
    /// colors or adding brushes at app level does not reach control templates at runtime. Recolor those shared brush
    /// objects in place instead; every control using them updates at once. The originals are restored for "system".
    /// Mirrors the Fluent mapping: light theme fills use Dark1, dark theme fills use Light2.
    /// </summary>
    private void RecolorControlBrushes(AccentColor? color)
    {
        // Nothing to restore while the Windows accent has never been replaced.
        if (color is null && _originalBrushColors.Count == 0) return;
        var controls = Application.Current.Resources.MergedDictionaries
            .OfType<Microsoft.UI.Xaml.Controls.XamlControlsResources>().FirstOrDefault();
        if (controls is not null) Recolor("controls", controls.ThemeDictionaries, BrushMapping);
        // App-level brushes (update badge) likewise.
        Recolor("app", Application.Current.Resources.ThemeDictionaries, AppBrushMapping);

        void Recolor(string owner, IDictionary<object, object> themes,
            Func<AccentRamp?, bool, IEnumerable<(string Key, uint? Rgb, byte Alpha)>> mapping)
        {
            foreach (var (themeKey, themeValue) in themes)
            {
                if (themeValue is not ResourceDictionary theme || themeKey is "HighContrast") continue;
                var dark = themeKey is "Default" or "Dark";
                AccentRamp? ramp = color is null ? null : AccentPalette.CreateRamp(color, dark);
                foreach (var (brushKey, rgb, alpha) in mapping(ramp, dark))
                {
                    if (!theme.TryGetValue(brushKey, out var value) || value is not Microsoft.UI.Xaml.Media.SolidColorBrush brush) continue;
                    var id = (owner, themeKey, brushKey);
                    if (!_originalBrushColors.TryGetValue(id, out var original))
                        _originalBrushColors[id] = original = brush.Color;
                    brush.Color = rgb is { } shade
                        ? Windows.UI.Color.FromArgb(alpha, (byte)(shade >> 16), (byte)(shade >> 8), (byte)shade)
                        : original;
                }
            }
        }
    }

    /// <summary>
    /// The update badge uses the accent fill with WinUI's text color for it (white in light, black in dark).
    /// </summary>
    private static IEnumerable<(string Key, uint? Rgb, byte Alpha)> AppBrushMapping(AccentRamp? ramp, bool dark)
    {
        uint? fill = ramp is null ? null : dark ? ramp.Light2 : ramp.Dark1;
        uint? onFill = ramp is null ? null : dark ? 0x000000u : 0xFFFFFFu;
        return
        [
            ("UpdateBadgeBackgroundBrush", fill, 0xFF),
            ("UpdateBadgeForegroundBrush", onFill, 0xFF),
        ];
    }

    private static IEnumerable<(string Key, uint? Rgb, byte Alpha)> BrushMapping(AccentRamp? ramp, bool dark)
    {
        uint? fill = ramp is null ? null : dark ? ramp.Light2 : ramp.Dark1;
        uint? text = ramp is null ? null : dark ? ramp.Light3 : ramp.Dark2;
        uint? textSecondary = ramp is null ? null : dark ? ramp.Light3 : ramp.Dark3;
        uint? accent = ramp?.Accent;
        return
        [
            ("AccentFillColorDefaultBrush", fill, 0xFF),
            ("AccentFillColorSecondaryBrush", fill, 0xE6),
            ("AccentFillColorTertiaryBrush", fill, 0xCC),
            ("AccentTextFillColorPrimaryBrush", text, 0xFF),
            ("AccentTextFillColorSecondaryBrush", textSecondary, 0xFF),
            ("AccentTextFillColorTertiaryBrush", fill, 0xFF),
            ("AccentFillColorSelectedTextBackgroundBrush", accent, 0xFF),
            ("SystemControlHighlightAccentBrush", accent, 0xFF),
            ("SystemControlForegroundAccentBrush", accent, 0xFF),
            ("SystemControlBackgroundAccentBrush", accent, 0xFF),
        ];
    }


    /// <summary>ThemeResource references re-resolve only on a theme change, so switch away and back once.</summary>
    private void RefreshThemeResources()
    {
        if (_root is null) return;
        var target = RequestedTheme;
        _root.RequestedTheme = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        _root.RequestedTheme = target;
    }

    private void SystemColorsChanged(UISettings sender, object args)
    {
        App.DispatcherQueue.TryEnqueue(() => { if (!_closed) Apply(); });
    }

    private void RootThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBar();
        QueueAccentReapply();
    }

    /// <summary>A theme switch re-evaluates the brushes' ThemeResource colors; recolor them again afterwards.</summary>
    private void QueueAccentReapply()
    {
        if (AccentPalette.Find(Accent) is null) return;
        App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => { if (!_closed) RecolorControlBrushes(AccentPalette.Find(Accent)); });
    }

    private void Apply()
    {
        if (_root is not null) _root.RequestedTheme = RequestedTheme;
        UpdateTitleBar();
        QueueAccentReapply();
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
