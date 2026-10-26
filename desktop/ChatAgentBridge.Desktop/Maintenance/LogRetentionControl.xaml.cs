using System.Globalization;
using System.Resources;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Maintenance;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Maintenance;

public sealed partial class LogRetentionControl : UserControl
{
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Maintenance.LogRetentionStrings", typeof(LogRetentionControl).Assembly);
    private LogRetentionPreferences _loadedPreferences = new();
    private bool _busy;
    private bool _loadFailed;

    /// <summary>Called by the maintenance owner to persist preferences and apply retention.</summary>
    public Func<LogRetentionPreferences, Task>? ApplyRequested { get; set; }

    public LogRetentionControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string Text(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LanguageSettings.LanguageChanged -= LanguageChanged;
        LanguageSettings.LanguageChanged += LanguageChanged;
        Localize();
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => LanguageSettings.LanguageChanged -= LanguageChanged;

    private void LanguageChanged(CultureInfo culture) => DispatcherQueue.TryEnqueue(Localize);

    private void Localize()
    {
        AdvancedExpander.Header = Text("AdvancedHeader");
        AdvancedExpander.Description = Text("AdvancedDescription");
        StatisticsHeading.Text = Text("StatisticsHeading");
        StatisticsDaysLabel.Text = Text("StatisticsDays");
        AuditHeading.Text = Text("AuditHeading");
        AuditDaysLabel.Text = Text("AuditDays");
        AuditSegmentSizeLabel.Text = Text("AuditSegmentSize");
        AuditMaxSegmentsLabel.Text = Text("AuditMaxSegments");
        HistoryHeading.Text = Text("HistoryHeading");
        HistoryDaysLabel.Text = Text("HistoryDays");
        HistoryMaxRecordsLabel.Text = Text("HistoryMaxRecords");
        ConversationDaysLabel.Text = Text("ConversationDays");
        DiagnosticsHeading.Text = Text("DiagnosticsHeading");
        DiagnosticsDaysLabel.Text = Text("DiagnosticsDays");
        DiagnosticsFileSizeLabel.Text = Text("DiagnosticsFileSize");
        DiagnosticsMaxFilesLabel.Text = Text("DiagnosticsMaxFiles");
        Explanation.Text = Text("Explanation");
        DefaultsButton.Content = Text("DefaultsButton");
        ApplyButton.Content = Text("ApplyButton");
        AutomationProperties.SetName(StatisticsDays, Text("StatisticsDays"));
        AutomationProperties.SetName(AuditDays, Text("AuditDays"));
        AutomationProperties.SetName(AuditSegmentSize, Text("AuditSegmentSize"));
        AutomationProperties.SetName(AuditMaxSegments, Text("AuditMaxSegments"));
        AutomationProperties.SetName(HistoryDays, Text("HistoryDays"));
        AutomationProperties.SetName(HistoryMaxRecords, Text("HistoryMaxRecords"));
        AutomationProperties.SetName(ConversationDays, Text("ConversationDays"));
        AutomationProperties.SetName(DiagnosticsDays, Text("DiagnosticsDays"));
        AutomationProperties.SetName(DiagnosticsFileSize, Text("DiagnosticsFileSize"));
        AutomationProperties.SetName(DiagnosticsMaxFiles, Text("DiagnosticsMaxFiles"));
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var preferences = await new LogRetentionPreferencesStore().LoadAsync();
            _loadedPreferences = preferences;
            _loadFailed = false;
            SetValues(preferences);
            ShowStatus(null);
        }
        catch (Exception)
        {
            _loadFailed = true;
            SetValues(new LogRetentionPreferences());
            ShowStatus(Text("LoadFailed"), InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private void SetValues(LogRetentionPreferences value)
    {
        StatisticsDays.Value = value.StatisticsRetentionDays;
        AuditDays.Value = value.AuditRetentionDays;
        AuditSegmentSize.Value = value.AuditSegmentSizeMb;
        AuditMaxSegments.Value = value.AuditMaxSegments;
        HistoryDays.Value = value.HistoryRetentionDays;
        HistoryMaxRecords.Value = value.HistoryMaxRecords;
        ConversationDays.Value = value.ConversationRetentionDays;
        DiagnosticsDays.Value = value.DiagnosticsRetentionDays;
        DiagnosticsFileSize.Value = value.DiagnosticsFileKiB;
        DiagnosticsMaxFiles.Value = value.DiagnosticsMaxFiles;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var box in NumberBoxes()) box.IsEnabled = !busy;
        DefaultsButton.IsEnabled = ApplyButton.IsEnabled = !busy;
    }

    private NumberBox[] NumberBoxes() => [StatisticsDays, AuditDays, AuditSegmentSize, AuditMaxSegments,
        HistoryDays, HistoryMaxRecords, ConversationDays, DiagnosticsDays, DiagnosticsFileSize, DiagnosticsMaxFiles];

    private void ShowStatus(string? message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBar.Message = message ?? string.Empty;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = message is not null;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        SetValues(new LogRetentionPreferences());
        ShowStatus(Text("DefaultsNotice"));
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var values = NumberBoxes();
        if (values.Any(value => !double.IsFinite(value.Value) || value.Value != Math.Truncate(value.Value)))
        {
            ShowStatus(Text("WholeNumberError"), InfoBarSeverity.Warning);
            return;
        }

        LogRetentionPreferences preferences;
        try
        {
            preferences = new LogRetentionPreferences
            {
                StatisticsRetentionDays = (int)StatisticsDays.Value,
                AuditRetentionDays = (int)AuditDays.Value,
                AuditSegmentSizeMb = (int)AuditSegmentSize.Value,
                AuditMaxSegments = (int)AuditMaxSegments.Value,
                HistoryRetentionDays = (int)HistoryDays.Value,
                HistoryMaxRecords = (int)HistoryMaxRecords.Value,
                ConversationRetentionDays = (int)ConversationDays.Value,
                DiagnosticsRetentionDays = (int)DiagnosticsDays.Value,
                DiagnosticsFileKiB = (int)DiagnosticsFileSize.Value,
                DiagnosticsMaxFiles = (int)DiagnosticsMaxFiles.Value
            };
            preferences.Validate();
        }
        catch (ArgumentException)
        {
            ShowStatus(Text("RangeError"), InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            if (_loadFailed || HasReductions(preferences))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = App.Appearance.RequestedTheme,
                    Title = Text("ConfirmTitle"),
                    Content = new TextBlock { Text = Text("ConfirmMessage"), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = Text("ConfirmApply"),
                    CloseButtonText = Text("Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                AutomationProperties.SetAutomationId(dialog, "LogRetentionReductionDialog");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            if (ApplyRequested is not { } apply)
            {
                ShowStatus(Text("ApplyUnavailable"), InfoBarSeverity.Error);
                return;
            }
            await apply(preferences);
            _loadedPreferences = preferences;
            _loadFailed = false;
            ShowStatus(Text("Applied"), InfoBarSeverity.Success);
        }
        catch (LogRetentionApplyException)
        {
            _loadedPreferences = preferences;
            _loadFailed = false;
            ShowStatus(Text("SavedCleanupPending"), InfoBarSeverity.Warning);
        }
        catch (Exception)
        {
            ShowStatus(Text("ApplyFailed"), InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private bool HasReductions(LogRetentionPreferences value) =>
        value.StatisticsRetentionDays < _loadedPreferences.StatisticsRetentionDays ||
        value.AuditRetentionDays < _loadedPreferences.AuditRetentionDays ||
        value.AuditSegmentSizeMb < _loadedPreferences.AuditSegmentSizeMb ||
        value.AuditMaxSegments < _loadedPreferences.AuditMaxSegments ||
        value.HistoryRetentionDays < _loadedPreferences.HistoryRetentionDays ||
        value.HistoryMaxRecords < _loadedPreferences.HistoryMaxRecords ||
        value.ConversationRetentionDays < _loadedPreferences.ConversationRetentionDays ||
        value.DiagnosticsRetentionDays < _loadedPreferences.DiagnosticsRetentionDays ||
        value.DiagnosticsFileKiB < _loadedPreferences.DiagnosticsFileKiB ||
        value.DiagnosticsMaxFiles < _loadedPreferences.DiagnosticsMaxFiles;
}
