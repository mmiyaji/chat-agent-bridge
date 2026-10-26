using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Diagnostics;
using ChatAgentBridge_Desktop.Deployment;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ChatAgentBridge_Desktop.Diagnostics;

public sealed partial class DiagnosticsControl : UserControl
{
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Diagnostics.DiagnosticsStrings", typeof(DiagnosticsControl).Assembly);
    private string _savedProvider = "Unknown";
    private string? _verifiedCliVersion;
    private string _savedChatProviders = "";
    private bool _diagnosticsUnavailable;
    private bool _busy;
    private bool _exporting;

    public DiagnosticsControl()
    {
        InitializeComponent();
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
    }

    private static string Text(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        App.PendingRequestNotifications.StatusChanged += Notifications_StatusChanged;
        App.PortableUpdates.Changed += PortableUpdates_Changed;
        LanguageSettings.LanguageChanged -= Language_Changed;
        LanguageSettings.LanguageChanged += Language_Changed;
        SetLocalizedLabels();
        _ = RefreshAsync();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        App.PendingRequestNotifications.StatusChanged -= Notifications_StatusChanged;
        App.PortableUpdates.Changed -= PortableUpdates_Changed;
        LanguageSettings.LanguageChanged -= Language_Changed;
    }

    private void Language_Changed(CultureInfo culture) => DispatcherQueue.TryEnqueue(() =>
    {
        SetLocalizedLabels();
        UpdateSummary();
    });

    private void SetLocalizedLabels()
    {
        ReleaseAndSystemHeader.Text = Text("ReleaseAndSystemHeader");
        ApplicationVersionLabel.Text = Text("ApplicationVersionLabel");
        BuildMetadataLabel.Text = Text("BuildMetadataLabel");
        WindowsRuntimeLabel.Text = Text("WindowsRuntimeLabel");
        ConfigurationStatusHeader.Text = Text("ConfigurationStatusHeader");
        SavedCliProviderLabel.Text = Text("SavedCliProviderLabel");
        CliVerificationLabel.Text = Text("CliVerificationLabel");
        ChatConnectionLabel.Text = Text("ChatConnectionLabel");
        UpdaterLabel.Text = Text("UpdaterLabel");
        NotificationsLabel.Text = Text("NotificationsLabel");
        RefreshButton.Content = Text("RefreshButton");
        TestNotificationButton.Content = Text("TestNotificationButton");
        ExportButton.Content = Text("ExportButton");
    }

    private void Notifications_StatusChanged() => DispatcherQueue.TryEnqueue(UpdateSummary);
    private void PortableUpdates_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateSummary);

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var store = new SettingsStore();
            var settings = await store.LoadAsync();
            _savedProvider = SafeProvider(settings.AgentProvider);
            _savedChatProviders = GetSavedChatProviders(settings);
            var verification = await Task.Run(() => new CliVerificationStore(store.DirectoryPath).LoadAsync(settings));
            _verifiedCliVersion = verification is null ? null : SafeCliVersion(verification.Probe.Version);
            _diagnosticsUnavailable = false;
            SummaryStatus.Text = Text("DiagnosticsReady");
        }
        catch (Exception)
        {
            _savedProvider = "Unknown";
            _verifiedCliVersion = null;
            _savedChatProviders = "";
            _diagnosticsUnavailable = true;
            SummaryStatus.Text = Text("DiagnosticsUnavailable");
        }
        finally
        {
            UpdateSummary();
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void UpdateSummary()
    {
        ProductVersion.Text = GetVersion();
        BuildValue.Text = GetBuild();
        WindowsValue.Text = $"{RuntimeInformation.OSDescription} · Windows {RuntimeInformation.OSArchitecture} · app {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}";
        ProviderValue.Text = _diagnosticsUnavailable ? Text("Unavailable") : _savedProvider;
        CliValue.Text = _verifiedCliVersion is null ? Text("CliNotVerified") : string.Format(CultureInfo.CurrentCulture, Text("CliVerified"), _verifiedCliVersion);
        ChatValue.Text = _diagnosticsUnavailable ? Text("Unavailable") :
            !string.IsNullOrEmpty(_savedChatProviders) ? string.Format(CultureInfo.CurrentCulture, Text("ChatSavedConfigured"), _savedChatProviders) : Text("ChatSavedNotConfigured");
        UpdaterValue.Text = GetUpdaterState();
        NotificationsValue.Text = App.PendingRequestNotifications.IsRegistered
            ? (App.PendingRequestNotifications.IsAvailable ? Text("NotificationsRegistered") : Text("NotificationsRegisteredUnavailable"))
            : Text("NotificationsNotRegistered");
    }

    private static string GetUpdaterState()
    {
        if (!DeploymentRuntime.IsPortable) return Text("UpdaterManagedByPackage");
        var updates = App.PortableUpdates;
        if (!updates.IsSupported) return Text("UpdaterUnavailable");
        if (updates.IsDownloading) return Text("UpdaterDownloading");
        if (updates.IsBusy) return Text("UpdaterWorking");
        if (updates.IsDownloaded) return Text("UpdaterReadyToApply");
        if (updates.AvailableVersion is { } version && SafeVersion(version) != "unknown")
            return string.Format(CultureInfo.CurrentCulture, Text("UpdaterVersionAvailable"), SafeVersion(version));
        return Text("UpdaterAvailable");
    }

    private static string GetVersion()
    {
        try
        {
            var package = Package.Current.Id.Version;
            return $"{package.Major}.{package.Minor}.{package.Build}.{package.Revision}";
        }
        catch (InvalidOperationException) { return DeploymentRuntime.GetAssemblyVersion(); }
    }

    private static string GetBuild()
    {
        var value = typeof(DiagnosticsControl).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticsControl).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return SafeVersion(value);
    }

    private static string SafeProvider(string value) => value switch
    {
        AgentProfile.Codex => "Codex", AgentProfile.Claude => "Claude", AgentProfile.Gemini => "Gemini",
        AgentProfile.GitHubCopilot => "GitHub Copilot", AgentProfile.Antigravity => "Antigravity", AgentProfile.Custom => "Custom", _ => "Unknown"
    };

    private static string GetSavedChatProviders(BridgeSettings settings)
    {
        var providers = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(settings.SlackBotToken) &&
            !string.IsNullOrWhiteSpace(settings.SlackAppToken) &&
            !string.IsNullOrWhiteSpace(settings.SlackAllowedUserIds))
            providers.Add("Slack");
        if (!string.IsNullOrWhiteSpace(settings.MattermostServerUrl) &&
            !string.IsNullOrWhiteSpace(settings.MattermostChannelId) &&
            !string.IsNullOrWhiteSpace(settings.MattermostToken) &&
            !string.IsNullOrWhiteSpace(settings.MattermostAllowedUserIds))
            providers.Add("Mattermost");
        if (!string.IsNullOrWhiteSpace(settings.DiscordBotToken) &&
            !string.IsNullOrWhiteSpace(settings.DiscordAllowedUserIds) &&
            (settings.DiscordAllowDirectMessages || !string.IsNullOrWhiteSpace(settings.DiscordAllowedGuildIds)))
            providers.Add("Discord");
        return string.Join(", ", providers);
    }

    private static string SafeVersion(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && char.IsAsciiDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-') ? value : "unknown";

    private static string SafeCliVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<![A-Za-z0-9])v?([0-9]+(?:\.[0-9]+){1,3})(?![0-9])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enqueued = App.PendingRequestNotifications.TryShowTestNotification(Text("TestNotificationTitle"), Text("TestNotificationBody"));
            SummaryStatus.Text = Text(enqueued ? "TestNotificationEnqueued" : "TestNotificationUnavailable");
        }
        catch (Exception)
        {
            SummaryStatus.Text = Text("TestNotificationUnavailable");
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        _exporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            var report = CreateReport();
            var json = report.ToJson();
            // Configure multiline behavior before assigning JSON: TextBox can
            // otherwise truncate the value at its first newline.
            var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, MaxHeight = 360 };
            preview.Text = json;
            AutomationProperties.SetAutomationId(preview, "DiagnosticsExportPreviewText");
            var dialog = new ContentDialog
            {
                Title = Text("ExportPreviewTitle"), Content = preview,
                RequestedTheme = App.Appearance.RequestedTheme,
                PrimaryButtonText = Text("ExportSave"), CloseButtonText = Text("Cancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "DiagnosticsExportPreview");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var picker = new FileSavePicker { SuggestedFileName = "chat-agent-bridge-support.json" };
            picker.FileTypeChoices.Add(Text("JsonFileType"), new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, json);
            SummaryStatus.Text = Text("ExportSaved");
        }
        catch (Exception)
        {
            SummaryStatus.Text = Text("ExportFailed");
        }
        finally
        {
            _exporting = false;
            ExportButton.IsEnabled = true;
        }
    }

    private SupportReport CreateReport()
    {
        var statuses = new List<string>
        {
            _verifiedCliVersion is null ? "cli_unverified" : "cli_verified",
            !string.IsNullOrEmpty(_savedChatProviders) ? "chat_configured" : "chat_not_configured",
            App.PendingRequestNotifications.IsRegistered ? "notifications_registered" : "notifications_unavailable",
            DeploymentRuntime.IsPortable && App.PortableUpdates.IsSupported ? "updater_supported" : "updater_unavailable"
        };
        var build = SafeVersion($"{GetVersion()}-{GetBuild()}");
        // SupportReport has a fixed allowlist and serializes no source settings, paths, tokens, or raw errors.
        return SupportReport.Create(build, Environment.OSVersion.Version.ToString(), Environment.Version.ToString(),
            RuntimeInformation.OSArchitecture.ToString(), _savedProvider,
            _verifiedCliVersion is null ? "unknown" : SafeCliVersion(_verifiedCliVersion), statuses, _diagnosticsUnavailable ? "diagnostic_unavailable" : "none",
            storageOperations: StorageOperationDiagnostics.Snapshot(), settingsNavigation: SettingsNavigationDiagnostics.Snapshot());
    }
}
