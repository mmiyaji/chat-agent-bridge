using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly AgentConnectionStore _agentConnectionStore = new();
    private AgentConnectionProfile? _registeredCodexConnection;
    private AgentConnectionProfile? _registeredClaudeConnection;
    private AgentConnectionProfile? _registeredGeminiConnection;
    private AgentConnectionProfile? _registeredCopilotConnection;

    [ObservableProperty] public partial string RegisteredConnectionSummary { get; set; } = ViewModelText.Get("NoRegisteredConnections");
    [ObservableProperty] public partial string ConnectionRegistrationStatus { get; set; } = "";

    public bool HasRegisteredCodexConnection => _registeredCodexConnection is not null;
    public bool HasRegisteredClaudeConnection => _registeredClaudeConnection is not null;
    public bool HasRegisteredGeminiConnection => _registeredGeminiConnection is not null;
    public bool HasRegisteredCopilotConnection => _registeredCopilotConnection is not null;

    private void RefreshConnectionCommands()
    {
        RegisterAgentConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredCodexConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredClaudeConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredGeminiConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredCopilotConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRegisteredCodexConnection));
        OnPropertyChanged(nameof(HasRegisteredClaudeConnection));
        OnPropertyChanged(nameof(HasRegisteredGeminiConnection));
        OnPropertyChanged(nameof(HasRegisteredCopilotConnection));
    }

    private async Task LoadAgentConnectionsAsync()
    {
        try
        {
            var profiles = await _agentConnectionStore.ListAsync();
            _registeredCodexConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Codex);
            _registeredClaudeConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Claude);
            _registeredGeminiConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Gemini);
            _registeredCopilotConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.GitHubCopilot);
            var names = profiles.Select(profile => profile.Provider switch
            {
                AgentProfile.Codex => "Codex", AgentProfile.Claude => "Claude Code",
                AgentProfile.Gemini => "Gemini CLI", AgentProfile.GitHubCopilot => "GitHub Copilot CLI", _ => profile.Provider
            }).ToArray();
            RegisteredConnectionSummary = names.Length == 0
                ? ViewModelText.Get("NoRegisteredConnections")
                : ViewModelText.Get("RegisteredConnections", string.Join(", ", names));
            ConnectionRegistrationStatus = "";
            RefreshConnectionCommands();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _registeredCodexConnection = null;
            _registeredClaudeConnection = null;
            _registeredGeminiConnection = null;
            _registeredCopilotConnection = null;
            RegisteredConnectionSummary = ViewModelText.Get("RegisteredConnectionsUnavailable", ex.Message);
            RefreshConnectionCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegisterAgentConnection))]
    private async Task RegisterAgentConnectionAsync()
    {
        if (!CanRegisterAgentConnection()) return;
        // A checked CLI may still have unconfirmed permission/command edits.
        // Register only a settings projection that passed the same save gate.
        if (!await SavePendingSettingsAsync(retrySensitiveConfirmation: true)) return;
        // Saving can normalize a trusted root and invalidate the CLI signature.
        if (!CanRegisterAgentConnection())
        {
            ConnectionRegistrationStatus = ViewModelText.Get("RecheckCli");
            return;
        }
        try
        {
            var profile = new AgentConnectionProfile(
                AgentProvider,
                CliExecutablePath,
                AgentProvider == AgentProfile.Codex ? (UseAppServer ? "app-server" : AgentProfile.DefaultCodexArguments)
                    : AgentProvider == AgentProfile.Claude ? "" : AgentArguments,
                AgentProvider == AgentProfile.Codex && UseAppServer ? "app-server" : "exec",
                AgentModel,
                AgentReasoningEffort,
                AgentPermissionMode,
                AllowAgentNetwork,
                CliVersion,
                _cliCheckedAt ?? throw new InvalidOperationException("A current CLI check is required before registration."));
            await _agentConnectionStore.SaveVerifiedAsync(profile);
            await LoadAgentConnectionsAsync();
            ConnectionRegistrationStatus = ViewModelText.Get("ConnectionRegistered", AgentProviderDisplayName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ConnectionRegistrationStatus = ViewModelText.Get("ConnectionRegistrationFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRegisteredCodexConnection))]
    private void LoadRegisteredCodexConnection() => ApplyRegisteredConnection(_registeredCodexConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredClaudeConnection))]
    private void LoadRegisteredClaudeConnection() => ApplyRegisteredConnection(_registeredClaudeConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredGeminiConnection))]
    private void LoadRegisteredGeminiConnection() => ApplyRegisteredConnection(_registeredGeminiConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredCopilotConnection))]
    private void LoadRegisteredCopilotConnection() => ApplyRegisteredConnection(_registeredCopilotConnection);

    private void ApplyRegisteredConnection(AgentConnectionProfile? profile)
    {
        if (profile is null) return;
        var applied = profile.ApplyTo(CreateSettingsSnapshot());
        AgentProvider = applied.AgentProvider;
        AgentExecutable = applied.AgentExecutable;
        AgentArguments = applied.AgentArguments;
        UseAppServer = applied.AgentMode == "app-server";
        AgentModel = applied.AgentModel ?? "";
        AgentReasoningEffort = applied.AgentReasoningEffort;
        AgentPermissionMode = applied.AgentPermissionMode;
        AllowAgentNetwork = applied.AllowAgentNetwork;
        ConnectionRegistrationStatus = ViewModelText.Get("ConnectionLoaded", AgentProviderDisplayName);
        UpdateSavedAgentSummary();
    }
}
