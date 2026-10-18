using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Onboarding;

public sealed partial class OnboardingIntro : UserControl
{
    public MainPageViewModel ViewModel { get; }

    public OnboardingIntro(MainPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    private async void ShowTerms_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not null)
            await PolicyDialog.ShowAsync(XamlRoot, privacy: false);
    }

    private async void ShowPrivacy_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not null)
            await PolicyDialog.ShowAsync(XamlRoot, privacy: true);
    }
}
