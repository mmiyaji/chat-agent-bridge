using System.ComponentModel;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private DispatcherQueueTimer? _settingsAutoSaveTimer;
    private bool _autoSaveAttemptRunning;
    private bool _autoSaveDeferredForBusy;
    private bool _sensitiveSettingsDialogOpen;

    private void InitializeSettingsAutoSave()
    {
        _settingsAutoSaveTimer = DispatcherQueue.CreateTimer();
        _settingsAutoSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _settingsAutoSaveTimer.IsRepeating = false;
        _settingsAutoSaveTimer.Tick += SettingsAutoSaveTimer_Tick;
        ViewModel.ConfirmSensitiveSettingsAsync = ConfirmSensitiveSettingsAsync;
        ViewModel.SettingsDraftEdited += ScheduleSettingsAutoSave;
        ViewModel.PropertyChanged += SettingsAutoSaveStateChanged;
        ViewModel.ModelCatalog.PropertyChanged += SettingsAutoSaveStateChanged;
        LostFocus += SettingsInput_LostFocus;
        Unloaded += (_, _) =>
        {
            _settingsAutoSaveTimer.Stop();
            ViewModel.SettingsDraftEdited -= ScheduleSettingsAutoSave;
            ViewModel.PropertyChanged -= SettingsAutoSaveStateChanged;
            ViewModel.ModelCatalog.PropertyChanged -= SettingsAutoSaveStateChanged;
            LostFocus -= SettingsInput_LostFocus;
        };
    }

    public static bool SettingsAutoSaveNeedsRetry(bool dirty, bool confirmationPending, bool statusOpen, string severity) =>
        dirty && (confirmationPending || statusOpen && severity is "Error");

    public static Visibility SettingsAutoSaveRetryVisibility(bool dirty, bool confirmationPending, bool statusOpen, string severity) =>
        ToVisibility(SettingsAutoSaveNeedsRetry(dirty, confirmationPending, statusOpen, severity));

    public static string SettingsAutoSaveStatus(bool dirty, bool loading, bool confirmationPending, bool statusOpen, string severity) =>
        confirmationPending ? DesktopText.Get("SettingsAutoSaveConfirmation")
            : dirty && statusOpen && severity is "Error" ? DesktopText.Get("SettingsAutoSaveFailed")
            : string.Empty;

    public static Visibility NonEmptyTextVisibility(string? text) => ToVisibility(!string.IsNullOrEmpty(text));

    private void SettingsAutoSaveStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_autoSaveDeferredForBusy && args.PropertyName is "IsLoading" or "IsRunning" or "IsCheckingCli" or "IsOnboardingActive")
            ScheduleSettingsAutoSave();
    }

    private void SettingsInput_LostFocus(object sender, RoutedEventArgs args) => ScheduleSettingsAutoSave();

    private bool IsSettingsInputFocused()
    {
        if (XamlRoot is null) return false;
        var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox or PasswordBox or NumberBox) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool SettingsAutoSaveIsBusy => ViewModel.IsLoading || ViewModel.IsRunning || ViewModel.IsCheckingCli ||
        ViewModel.ModelCatalog.IsLoading || ViewModel.SaveSettingsCommand.IsRunning;

    private void ScheduleSettingsAutoSave()
    {
        if (_settingsAutoSaveTimer is null || _autoSaveAttemptRunning || _settingsPromptOpen || _sensitiveSettingsDialogOpen) return;
        _settingsAutoSaveTimer.Stop();
        if (!ViewModel.HasUnsavedSettings && ViewModel.StatusTitle == ViewModelText.Get("SettingsSaveFailed"))
            ViewModel.IsStatusOpen = false;
        if (!ViewModel.HasUnsavedSettings || !ViewModel.IsSettingsLoaded || ViewModel.IsOnboardingActive ||
            ViewModel.IsSensitiveConfirmationPending) return;
        _autoSaveDeferredForBusy = SettingsAutoSaveIsBusy;
        if (_autoSaveDeferredForBusy) return;
        _settingsAutoSaveTimer.Start();
    }

    private async void SettingsAutoSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_autoSaveAttemptRunning || _settingsPromptOpen || _sensitiveSettingsDialogOpen ||
            !ViewModel.HasUnsavedSettings || ViewModel.IsOnboardingActive || ViewModel.IsSensitiveConfirmationPending) return;
        if (SettingsAutoSaveIsBusy) { _autoSaveDeferredForBusy = true; return; }
        // Text and credentials are committed on focus loss, never on every keystroke.
        if (IsSettingsInputFocused()) return;
        _autoSaveAttemptRunning = true;
        try
        {
            if (await ViewModel.SavePendingSettingsAsync()) ViewModel.IsStatusOpen = false;
        }
        finally { _autoSaveAttemptRunning = false; }
    }

    private async void RetrySettingsAutoSave_Click(object sender, RoutedEventArgs args)
    {
        if (_autoSaveAttemptRunning || _settingsPromptOpen || _sensitiveSettingsDialogOpen) return;
        CommitSettingsNumberEdit();
        _settingsAutoSaveTimer?.Stop();
        _autoSaveAttemptRunning = true;
        try
        {
            if (await ViewModel.SavePendingSettingsAsync(retrySensitiveConfirmation: true)) ViewModel.IsStatusOpen = false;
        }
        finally { _autoSaveAttemptRunning = false; }
    }

    private async Task<bool> ConfirmSensitiveSettingsAsync(BridgeSettings saved, BridgeSettings draft)
    {
        if (_sensitiveSettingsDialogOpen || XamlRoot is null) return false;
        _sensitiveSettingsDialogOpen = true;
        try
        {
            var changes = SettingsSensitiveChanges.Compare(saved, draft);
            var groups = new List<string>();
            if (changes.CliCommand) groups.Add(DesktopText.Get("SettingsSensitiveCli"));
            if (changes.AgentPermissions) groups.Add(DesktopText.Get("SettingsSensitivePermissions"));
            if (changes.TrustedWorkspaces) groups.Add(DesktopText.Get("SettingsSensitiveFolders"));
            if (changes.ChatAccess) groups.Add(DesktopText.Get("SettingsSensitiveAccess"));
            if (changes.ChatSharing) groups.Add(DesktopText.Get("SettingsSensitiveSharing"));
            if (changes.FileAttachments) groups.Add(DesktopText.Get("SettingsSensitiveAttachments"));
            if (changes.ConnectionIdentity) groups.Add(DesktopText.Get("SettingsSensitiveConnection"));
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = DesktopText.Get("SettingsSensitiveIntro"), TextWrapping = TextWrapping.Wrap });
            foreach (var group in groups)
                content.Children.Add(new TextBlock { Text = group, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            foreach (var detail in SettingsReviewDetails(saved, draft))
                content.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(new TextBlock { Text = DesktopText.Get("SettingsSensitiveApplyHelp"), TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("SettingsSensitiveTitle"),
                Content = new ScrollViewer { MaxHeight = 360, Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = DesktopText.Get("SettingsSensitiveAccept"),
                CloseButtonText = DesktopText.Get("SettingsDraftCancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "SensitiveSettingsConfirmation");
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings confirmation: {ex.GetType().Name}");
            return false;
        }
        finally { _sensitiveSettingsDialogOpen = false; }
    }
}
