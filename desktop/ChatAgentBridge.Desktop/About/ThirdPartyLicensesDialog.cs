using System.Text.Json;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.About;

/// <summary>Reads bundled notices only; viewing licenses never requires a network connection.</summary>
internal static class ThirdPartyLicensesDialog
{
    private sealed record Document(string Name, string File, string Components);

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Licenses");
        var catalog = await File.ReadAllTextAsync(Path.Combine(directory, "catalog.json"));
        var documents = JsonSerializer.Deserialize<Document[]>(catalog,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (documents is not { Length: > 0 } || documents.Any(d =>
                d is null || string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.File) ||
                d.File != Path.GetFileName(d.File) || d.File.IndexOfAny(['/', '\\', ':']) >= 0 ||
                !d.File.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The bundled license catalog is invalid.");

        var selector = new ComboBox
        {
            Header = DesktopText.Get("ThirdPartyLicensesComponent"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = documents.Select(d => d.Name).ToArray()
        };
        AutomationProperties.SetAutomationId(selector, "ThirdPartyLicenseSelector");
        AutomationProperties.SetName(selector, DesktopText.Get("ThirdPartyLicensesComponent"));
        var components = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetAutomationId(components, "ThirdPartyLicenseComponents");
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetAutomationId(body, "ThirdPartyLicenseBody");
        var documentContent = new StackPanel { Spacing = 12 };
        documentContent.Children.Add(components);
        documentContent.Children.Add(body);
        var scroll = new ScrollViewer
        {
            Content = documentContent,
            MaxHeight = Math.Max(100, Math.Min(400, xamlRoot.Size.Height - 320)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetAutomationId(scroll, "ThirdPartyLicenseScroll");
        AutomationProperties.SetName(scroll, DesktopText.Get("ThirdPartyLicensesTitle"));
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(selector);
        panel.Children.Add(scroll);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = DesktopText.Get("ThirdPartyLicensesTitle"),
            Content = panel,
            CloseButtonText = DesktopText.Get("Close"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "ThirdPartyLicensesDialog");

        // Read the first document before displaying the dialog. Later selections are asynchronous;
        // a slower earlier read must never overwrite the currently selected document.
        var selectionVersion = 0;
        async Task LoadAsync(Document document)
        {
            var version = ++selectionVersion;
            components.Text = document.Components ?? string.Empty;
            body.Text = DesktopText.Get("ThirdPartyLicensesLoading");
            try
            {
                var text = await File.ReadAllTextAsync(Path.Combine(directory, document.File));
                if (version != selectionVersion) return;
                body.Text = text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (version != selectionVersion) return;
                body.Text = DesktopText.Get("ThirdPartyLicensesReadFailed");
            }
            scroll.ChangeView(null, 0, null, true);
        }
        selector.SelectedIndex = 0;
        await LoadAsync(documents[0]);
        selector.SelectionChanged += SelectionChanged;
        try { await dialog.ShowAsync(); }
        finally
        {
            ++selectionVersion;
            selector.SelectionChanged -= SelectionChanged;
        }

        async void SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (selector.SelectedIndex is var index && index >= 0 && index < documents.Length)
                await LoadAsync(documents[index]);
        }
    }
}
