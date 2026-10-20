using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Pricing;

public sealed partial class PricingSettings : UserControl
{
    public PricingSettingsViewModel ViewModel { get; }

    public PricingSettings()
    {
        ViewModel = new PricingSettingsViewModel(App.Pricing);
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutForWidth();
        Loaded += (_, _) => ViewModel.Subscribe();
        Unloaded += (_, _) => ViewModel.Unsubscribe();
        Loaded += (_, _) => UpdateLayoutForWidth();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateLayoutForWidth()
    {
        var narrow = ActualWidth < 680;
        RatesGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        PlaceRateGroup(InputRateGroup, 0, 0);
        PlaceRateGroup(CachedInputRateGroup, narrow ? 1 : 0, narrow ? 0 : 1);
        PlaceRateGroup(CacheWriteRateGroup, narrow ? 2 : 1, 0);
        PlaceRateGroup(OutputRateGroup, narrow ? 3 : 1, narrow ? 0 : 1);

        ManualButtonsGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetRow(SaveManualButton, 0);
        Grid.SetColumn(SaveManualButton, 0);
        Grid.SetColumnSpan(SaveManualButton, narrow ? 2 : 1);
        Grid.SetRow(RemoveManualButton, narrow ? 1 : 0);
        Grid.SetColumn(RemoveManualButton, narrow ? 0 : 1);
        Grid.SetColumnSpan(RemoveManualButton, narrow ? 2 : 1);
    }

    private static void PlaceRateGroup(FrameworkElement group, int row, int column)
    {
        Grid.SetRow(group, row);
        Grid.SetColumn(group, column);
        Grid.SetColumnSpan(group, 1);
    }

    private async void RemoveManual_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.SelectedManualModelName)) return;
        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("PricingRemoveConfirmTitle"),
            Content = DesktopText.Get("PricingRemoveConfirmMessage", ViewModel.SelectedManualModelName),
            PrimaryButtonText = DesktopText.Get("PricingRemoveConfirmPrimary"),
            CloseButtonText = DesktopText.Get("PricingRemoveConfirmCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.RemoveManualCommand.Execute(null);
    }
}
