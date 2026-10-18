using ChatAgentBridge.Core;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    /// <summary>Refreshes computed onboarding text after the active UI language changes.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingNextLabel));
        OnPropertyChanged(nameof(WizardSummary));
        OnPropertyChanged(nameof(BasicProviderHelp));
        OnPropertyChanged(nameof(AgentProviderDisplayName));
        OnPropertyChanged(nameof(AgentPermissionStatus));
        RefreshSetupStepper();
        UpdateSavedAgentSummary();
        if (IsWizardIntro && !CliCheckSucceeded && !IsCheckingCli)
        {
            CliVersion = ChatAgentBridge_Desktop.Localization.ViewModelText.Get("NotChecked");
            CliCheckSummary = ChatAgentBridge_Desktop.Localization.ViewModelText.Get("CliCheckIntro");
            ModelCatalog.Invalidate(IsBasicProvider ? ChatAgentBridge_Desktop.Localization.ViewModelText.Get("BasicModelManagedByCli") : null);
        }

        if (IsWizardIntro)
        {
            ConsentStatus = _hasCurrentConsent
                ? ChatAgentBridge_Desktop.Localization.ViewModelText.Get("ConsentSaved", OnboardingConsentStore.CurrentPolicyVersion)
                : ChatAgentBridge_Desktop.Localization.ViewModelText.Get("ConsentInstructionsWizard");
            if (IsOnboardingActive && StatusSeverity == "Warning") IsStatusOpen = false;
        }
    }
}
