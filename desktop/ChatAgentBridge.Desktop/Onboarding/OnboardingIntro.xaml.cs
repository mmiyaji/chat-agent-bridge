using ChatAgentBridge_Desktop.ViewModels;
using ChatAgentBridge_Desktop.Localization;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.Onboarding;

public sealed partial class OnboardingIntro : UserControl
{
    public MainPageViewModel ViewModel { get; }

    public OnboardingIntro(MainPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        IntroLanguageSettings.ApplyImmediately = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        TermsConsentCheckBox.Checked += (_, _) => SetMissingHighlight(TermsConsentBorder, false);
        PrivacyConsentCheckBox.Checked += (_, _) => SetMissingHighlight(PrivacyConsentBorder, false);
    }

    private void SetMissingHighlight(Border border, bool missing)
    {
        border.BorderBrush = missing ? (Brush)Resources["MissingConsentBorderBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        border.Background = missing ? (Brush)Resources["MissingConsentBackgroundBrush"] : null;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        LanguageSettings.LanguageChanged -= OnLanguageChanged;
        LanguageSettings.LanguageChanged += OnLanguageChanged;
        SetMissingHighlight(TermsConsentBorder, false);
        SetMissingHighlight(PrivacyConsentBorder, false);
        RefreshLanguage(CultureInfo.CurrentUICulture);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        LanguageSettings.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(CultureInfo culture) => RefreshLanguage(culture);

    public void RefreshLanguage(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        IntroHeading.Text = MrtText.Get("onboardingOnboardingIntroTextBlock001.Text", culture);
        IntroDescription.Text = MrtText.Get("onboardingOnboardingIntroTextBlock002.Text", culture);
        IntroDataNotice.Text = MrtText.Get("onboardingOnboardingIntroTextBlock003.Text", culture);
        IntroDisclaimer.Text = MrtText.Get("onboardingOnboardingIntroTextBlock004.Text", culture);
        TermsButtonText.Text = MrtText.Get("onboardingOnboardingIntroTextBlock005.Text", culture);
        PrivacyButtonText.Text = MrtText.Get("onboardingOnboardingIntroTextBlock006.Text", culture);
        TermsConsentCheckBox.Content = MrtText.Get("WizardAcceptTerms.Content", culture);
        PrivacyConsentText.Text = MrtText.Get("onboardingOnboardingIntroTextBlock007.Text", culture);
        var architectureName = MrtText.Get("WizardArchitecture.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", culture);
        ArchitectureImage.SetAccessibleName(architectureName, culture);
        AutomationProperties.SetName(ArchitectureImage, architectureName);
    }

    public void FocusMissingConsent()
    {
        // Mark every unchecked consent, not just the first one that receives focus.
        SetMissingHighlight(TermsConsentBorder, !ViewModel.HasAcceptedTerms);
        SetMissingHighlight(PrivacyConsentBorder, !ViewModel.HasAcceptedPrivacy);
        var target = !ViewModel.HasAcceptedTerms ? TermsConsentCheckBox : PrivacyConsentCheckBox;
        target.Focus(FocusState.Programmatic);
        // Align above the fold rather than only revealing the checkbox's last
        // pixel; both consent choices and the explanation should be readable.
        target.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0,
            AnimationDesired = false
        });
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
