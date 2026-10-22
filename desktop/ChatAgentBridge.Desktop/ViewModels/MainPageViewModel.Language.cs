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
        RefreshWizardSkipState();
        OnPropertyChanged(nameof(BasicProviderHelp));
        OnPropertyChanged(nameof(AgentProviderDisplayName));
        OnPropertyChanged(nameof(AgentPermissionStatus));
        RefreshOverviewStatus();
        if (_startupState is { } startupState)
            _ = RefreshStartupStatusForLanguageAsync(startupState);
        RefreshSetupStepper();
        UpdateSavedAgentSummary();
        if (IsWizardIntro && !CliCheckSucceeded && !IsCheckingCli)
        {
            CliVersion = ChatAgentBridge_Desktop.Localization.ViewModelText.Get("NotChecked");
            CliCheckSummary = ChatAgentBridge_Desktop.Localization.ViewModelText.Get("CliCheckIntro");
            InvalidateModelCatalog();
        }

        if (IsWizardIntro)
        {
            ConsentStatus = _hasCurrentConsent
                ? ChatAgentBridge_Desktop.Localization.ViewModelText.Get("ConsentSaved", OnboardingConsentStore.CurrentPolicyVersion)
                : ChatAgentBridge_Desktop.Localization.ViewModelText.Get("ConsentInstructionsWizard");
            if (IsOnboardingActive && StatusSeverity == "Warning") IsStatusOpen = false;
        }
    }

    private async Task RefreshStartupStatusForLanguageAsync(Startup.StartupState stateBeforeRefresh)
    {
        try
        {
            // Startup status text comes from an OS query and is cached in the
            // view model. Re-read it so the new culture is used, while leaving
            // an in-progress user change untouched.
            var result = await _startup.GetStateAsync();
            if (_startupState != stateBeforeRefresh) return;
            ApplyStartupResult(result);
        }
        catch
        {
            // Language refresh must not change the current startup state if the
            // read-only OS query is temporarily unavailable.
        }
    }
}
