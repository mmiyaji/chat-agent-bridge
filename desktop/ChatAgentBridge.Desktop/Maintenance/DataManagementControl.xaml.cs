using System.Globalization;
using System.Resources;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Maintenance;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ChatAgentBridge_Desktop.Maintenance;

public sealed partial class DataManagementControl : UserControl
{
    private static readonly ResourceManager Strings = new("ChatAgentBridge_Desktop.Maintenance.MaintenanceStrings", typeof(DataManagementControl).Assembly);
    private bool _busy;
    private static string Text(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public DataManagementControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LanguageSettings.LanguageChanged -= LanguageChanged;
            LanguageSettings.LanguageChanged += LanguageChanged;
            Localize(); _ = RefreshAsync();
        };
        Unloaded += (_, _) => LanguageSettings.LanguageChanged -= LanguageChanged;
    }

    private void LanguageChanged(CultureInfo culture) { Localize(); _ = RefreshAsync(); }

    private void Localize()
    {
        Group.Header = Text("Heading");
        Group.Description = Text("Description");
        LocationCard.Header = Text("Location");
        ExportCard.Header = Text("ExportHeading"); ExportCard.Description = Text("ExportDescription");
        ImportCard.Header = Text("ImportHeading"); ImportCard.Description = Text("ImportDescription");
        ClearCard.Header = Text("ClearHeading"); ClearCard.Description = Text("ClearDescription");
        OpenButton.Content = Text("Open"); ExportButton.Content = Text("Export"); ImportButton.Content = Text("Import");
        ClearButton.Content = Text("Clear"); RefreshButton.Content = Text("Refresh");
        Location.Text = new SettingsStore().DirectoryPath;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ExportButton.IsEnabled = ImportButton.IsEnabled = ClearButton.IsEnabled = RefreshButton.IsEnabled = !busy;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var sizes = await Task.Run(() => new LocalDataManager(new SettingsStore().DirectoryPath).Inspect());
            Sizes.Text = string.Join(Environment.NewLine, sizes.Select(item =>
                $"{Text(item.Kind.ToString())}: {item.Bytes / 1024d:N1} KiB ({item.Files})"));
        }
        catch (Exception) { Status.Text = Text("Failed"); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(new SettingsStore().DirectoryPath);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) Status.Text = Text("Failed");
        }
        catch (Exception) { Status.Text = Text("Failed"); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var backup = SettingsBackup.Create(await new SettingsStore().LoadAsync());
            var picker = new FileSavePicker { SuggestedFileName = "chat-agent-bridge-settings" };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllBytesAsync(file.Path, backup.Serialize());
            Status.Text = Text("Exported");
        }
        catch (Exception) { Status.Text = Text("Failed"); }
        finally { SetBusy(false); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        var prepared = false;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            // Read at most the declared format limit, even if a selected file grows while being read.
            var buffer = new byte[65537]; var length = 0;
            await using (var stream = File.OpenRead(file.Path))
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length));
                    if (read == 0) break;
                    length += read;
                }
            var backup = SettingsBackup.Parse(buffer.AsSpan(0, length));
            var trust = new CheckBox { Content = Text("TrustWorkspace"), IsChecked = false };
            AutomationProperties.SetAutomationId(trust, "BackupRestoreTrustWorkspace");
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = Text("RestoreNotice"), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"{backup.Provider}\n{backup.Executable}\n{backup.Workspace}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(trust);
            if (await ConfirmAsync("ImportHeading", "Import", content, "BackupRestoreDialog") != ContentDialogResult.Primary) return;
            if (App.Window is not MainWindow window || !await window.PrepareForDataMaintenanceAsync()) { Status.Text = Text("Busy"); return; }
            prepared = true;
            var store = new SettingsStore();
            new LocalDataManager(store.DirectoryPath).CheckSettingsPaths();
            // Preserve the original even when it cannot be parsed. This private, same-PC copy
            // retains DPAPI-protected credentials and must never be included in support exports.
            var original = Path.Combine(store.DirectoryPath, "settings.json");
            if (File.Exists(original))
                File.Copy(original, Path.Combine(store.DirectoryPath, "settings-original-before-restore.json"), overwrite: true);
            await store.SaveAsync(backup.ToSettings(trust.IsChecked == true));
            await window.ReloadAfterDataMaintenanceAsync();
            Status.Text = Text("Restored");
        }
        catch (Exception) { Status.Text = Text("Failed"); }
        finally
        {
            if (prepared && App.Window is MainWindow window) window.FinishDataMaintenance();
            SetBusy(false);
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        var prepared = false;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = Text("ClearNotice"), TextWrapping = TextWrapping.Wrap });
            var choices = Enum.GetValues<LocalDataKind>().Select(kind => (kind, box: new CheckBox { Content = Text(kind.ToString()) })).ToArray();
            foreach (var item in choices)
            {
                AutomationProperties.SetAutomationId(item.box, "DataClear" + item.kind);
                content.Children.Add(item.box);
            }
            if (await ConfirmAsync("ClearHeading", "Clear", content, "DataClearDialog",
                () => choices.Any(choice => choice.box.IsChecked == true)) != ContentDialogResult.Primary) return;
            var selected = choices.Where(c => c.box.IsChecked == true).Select(c => c.kind).ToArray();
            if (selected.Length == 0) return;
            if (App.Window is not MainWindow window || !await window.PrepareForDataMaintenanceAsync()) { Status.Text = Text("Busy"); return; }
            prepared = true;
            await new LocalDataManager(new SettingsStore().DirectoryPath).ClearAsync(selected);
            await window.ReloadAfterDataMaintenanceAsync();
            await RefreshAsync();
            Status.Text = Text("Cleared");
        }
        catch (Exception) { Status.Text = Text("Failed"); }
        finally
        {
            if (prepared && App.Window is MainWindow window) window.FinishDataMaintenance();
            SetBusy(false);
        }
    }

    private async Task<ContentDialogResult> ConfirmAsync(string title, string primary, UIElement content, string id, Func<bool>? canConfirm = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = App.Appearance.RequestedTheme,
            Title = Text(title), Content = new ScrollViewer { Content = content,
                MaxHeight = Math.Max(120, Math.Min(400, XamlRoot.Size.Height - 260)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = Text(primary), CloseButtonText = Text("Cancel"), DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = canConfirm?.Invoke() ?? true
        };
        if (canConfirm is not null && content is StackPanel panel)
            foreach (var checkBox in panel.Children.OfType<CheckBox>())
            {
                checkBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = canConfirm();
                checkBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = canConfirm();
            }
        AutomationProperties.SetAutomationId(dialog, id);
        return await dialog.ShowAsync();
    }
}
