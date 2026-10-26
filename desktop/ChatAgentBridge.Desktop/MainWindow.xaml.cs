using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ChatAgentBridge_Desktop;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinimumWidthDip = 500;
    private const int MinimumHeightDip = 600;
    private const double SettingsSearchPanelMaximumWidth = 480;
    private const double SettingsSearchPanelHorizontalMargin = 64;
    private bool _restoreSearchTriggerFocusOnClose;
    private bool _settingsSearchResultSelected;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        App.Appearance.Attach(this, WindowRoot);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        UpdateMinimumWindowSize();
        AppWindow.Resize(new SizeInt32((int)(1160 * scale), (int)(800 * scale)));
        WindowRoot.Loaded += WindowRoot_Loaded;
        WindowRoot.SizeChanged += WindowRoot_SizeChanged;
        SettingsSearchBox.AddHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(SettingsSearchBox_KeyDown), true);
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        // Construct data-consuming pages only after registered schema migrations succeed.
        WindowRoot.Loaded += StartupRoot_Loaded;
        UpdateSettingsSearchVisibility();
    }

    private void UpdateSettingsSearchVisibility()
    {
        var canSearch = Page?.CanSearchSettings == true;
        if (!canSearch && SettingsSearchFlyout.IsOpen)
        {
            _restoreSearchTriggerFocusOnClose = false;
            SettingsSearchFlyout.Hide();
        }
    }

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)Math.Ceiling(MinimumWidthDip * scale);
        var height = (int)Math.Ceiling(MinimumHeightDip * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs args)
    {
        WindowRoot.Loaded -= WindowRoot_Loaded;
        WindowRoot.XamlRoot.Changed += WindowRootXamlRoot_Changed;
        UpdateMinimumWindowSize();
    }

    private void WindowRootXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateMinimumWindowSize();

    private void WindowRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!SettingsSearchFlyout.IsOpen) return;
        _restoreSearchTriggerFocusOnClose = SettingsSearchBox.FocusState != FocusState.Unfocused;
        SettingsSearchFlyout.Hide();
    }

    private MainPage? Page => RootFrame.Content as MainPage;

    private void SettingsSearchFlyout_Opening(object sender, object args)
    {
        ClearSettingsSearchQuery();
        SettingsSearchPanel.Width = Math.Min(SettingsSearchPanelMaximumWidth,
            Math.Max(220, WindowRoot.ActualWidth - SettingsSearchPanelHorizontalMargin));
        Page?.RefreshSettingsSearchIndex();
    }

    private void SettingsSearchFlyout_Opened(object sender, object args)
    {
        SettingsSearchBox.Focus(FocusState.Programmatic);
    }

    private void SettingsSearchFlyout_Closed(object sender, object args)
    {
        ClearSettingsSearchQuery();
        if (_restoreSearchTriggerFocusOnClose && !_settingsSearchResultSelected && Page?.CanSearchSettings == true)
            Page.FocusSettingsSearchTrigger();
        _restoreSearchTriggerFocusOnClose = false;
        _settingsSearchResultSelected = false;
    }

    private void ClearSettingsSearchQuery()
    {
        SettingsSearchBox.Text = string.Empty;
        SettingsSearchBox.ItemsSource = null;
        SettingsSearchBox.IsSuggestionListOpen = false;
    }

    private void SettingsSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || !SettingsSearchFlyout.IsOpen) return;
        _restoreSearchTriggerFocusOnClose = true;
        SettingsSearchFlyout.Hide();
        args.Handled = true;
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var results = Page?.SearchSettings(sender.Text) ?? [];
        // A typed query with no match shows a note instead of silently closing the list.
        sender.ItemsSource = results.Count == 0 && sender.Text.Trim().Length > 0
            ? new[] { SettingsSearchEntry.NoResults(Localization.DesktopText.Get("SearchNoResults")) }
            : results;
    }

    private void SettingsSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) { }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter without picking a suggestion opens the best match.
        var entry = args.ChosenSuggestion as SettingsSearchEntry ?? Page?.SearchSettings(args.QueryText).FirstOrDefault();
        // The no-results note is not a destination; leave the panel open.
        if (entry is null || entry.IsNoResults || Page is not { } page) return;
        _settingsSearchResultSelected = true;
        _restoreSearchTriggerFocusOnClose = false;
        SettingsSearchFlyout.Hide();
        ClearSettingsSearchQuery();
        page.OpenSettingsSearchResult(entry);
    }

    private void SettingsSearchAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowSettingsSearch();
    }

    private void ShowSettingsSearch()
    {
        if (Page?.CanSearchSettings != true) return;
        if (!SettingsSearchFlyout.IsOpen)
        {
            ClearSettingsSearchQuery();
            SettingsSearchFlyout.ShowAt(AppTitleBar);
        }
        else
        {
            SettingsSearchBox.Focus(FocusState.Keyboard);
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            PasswordRevealControl.Hide(WindowRoot);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        CancelStartupMigration();
        WindowRoot.Loaded -= WindowRoot_Loaded;
        WindowRoot.SizeChanged -= WindowRoot_SizeChanged;
        if (WindowRoot.XamlRoot is { } root) root.Changed -= WindowRootXamlRoot_Changed;
        App.Appearance.Dispose();
        if (RootFrame.Content is MainPage page) page.ViewModel.CancelForWindowClose();
        ChatAgentBridge.Core.AppServer.CodexAppServerRunner.ShutdownSharedProcesses();
    }

    internal async Task ShutdownAsync()
    {
        CancelStartupMigration();
        if (_startupInitializationTask is { } startup) await startup;
        if (RootFrame.Content is MainPage page) await page.ShutdownAsync();
    }

    internal Task<bool> TryResolveUnsavedSettingsAsync() => RootFrame.Content is MainPage page
        ? page.TryResolveUnsavedSettingsAsync() : Task.FromResult(true);

    internal bool CanRestartForUpdate => RootFrame.Content is MainPage page && page.CanRestartForPortableUpdate;
    internal Task ReloadAfterDataMaintenanceAsync() => RootFrame.Content is MainPage page
        ? page.ReloadAfterDataMaintenanceAsync() : Task.CompletedTask;
    internal Task<bool> PrepareForDataMaintenanceAsync() => RootFrame.Content is MainPage page
        ? page.PrepareForDataMaintenanceAsync() : Task.FromResult(false);
    internal void FinishDataMaintenance()
    {
        if (RootFrame.Content is MainPage page) page.FinishDataMaintenance();
    }
    internal Task<bool> PrepareForPortableUpdateAsync() => RootFrame.Content is MainPage page
        ? page.PrepareForPortableUpdateAsync() : Task.FromResult(false);
    internal void CancelPortableUpdatePreparation()
    {
        if (RootFrame.Content is MainPage page) page.CancelPortableUpdatePreparation();
    }
    internal Task CancelPortableUpdatePreparationAsync() => RootFrame.Content is MainPage page
        ? page.CancelPortableUpdatePreparationAsync() : Task.CompletedTask;
}
