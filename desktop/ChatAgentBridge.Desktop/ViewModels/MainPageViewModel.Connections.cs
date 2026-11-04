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
    private AgentConnectionProfile? _registeredAntigravityConnection;
    private AgentConnectionProfile? _registeredKiroConnection;
    private AgentConnectionProfile? _registeredQwenConnection;
    private AgentConnectionProfile? _registeredKimiConnection;
    private AgentConnectionProfile? _registeredMuseConnection;

    [ObservableProperty] public partial string RegisteredConnectionSummary { get; set; } = ViewModelText.Get("NoRegisteredConnections");
    [ObservableProperty] public partial string ConnectionRegistrationStatus { get; set; } = "";

    public bool HasRegisteredCodexConnection => _registeredCodexConnection is not null;
    public bool HasRegisteredClaudeConnection => _registeredClaudeConnection is not null;
    public bool HasRegisteredGeminiConnection => _registeredGeminiConnection is not null;
    public bool HasRegisteredCopilotConnection => _registeredCopilotConnection is not null;
    public bool HasRegisteredAntigravityConnection => _registeredAntigravityConnection is not null;
    public bool HasRegisteredKiroConnection => _registeredKiroConnection is not null;
    public bool HasRegisteredQwenConnection => _registeredQwenConnection is not null;
    public bool HasRegisteredKimiConnection => _registeredKimiConnection is not null;
    public bool HasRegisteredMuseConnection => _registeredMuseConnection is not null;

    private void RefreshConnectionCommands()
    {
        RegisterAgentConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredCodexConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredClaudeConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredGeminiConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredCopilotConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredAntigravityConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredKiroConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredQwenConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredKimiConnectionCommand.NotifyCanExecuteChanged();
        LoadRegisteredMuseConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRegisteredCodexConnection));
        OnPropertyChanged(nameof(HasRegisteredClaudeConnection));
        OnPropertyChanged(nameof(HasRegisteredGeminiConnection));
        OnPropertyChanged(nameof(HasRegisteredCopilotConnection));
        OnPropertyChanged(nameof(HasRegisteredAntigravityConnection));
        OnPropertyChanged(nameof(HasRegisteredKiroConnection));
        OnPropertyChanged(nameof(HasRegisteredQwenConnection));
        OnPropertyChanged(nameof(HasRegisteredKimiConnection));
        OnPropertyChanged(nameof(HasRegisteredMuseConnection));
        OnPropertyChanged(nameof(HasVerifiedAgentEditor));
    }

    private async Task LoadAgentConnectionsAsync()
    {
        try
        {
            var profiles = await Task.Run(() => _agentConnectionStore.ListAsync());
            _registeredCodexConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Codex);
            _registeredClaudeConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Claude);
            _registeredGeminiConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Gemini);
            _registeredCopilotConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.GitHubCopilot);
            _registeredAntigravityConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Antigravity);
            _registeredKiroConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Kiro);
            _registeredQwenConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Qwen);
            _registeredKimiConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Kimi);
            _registeredMuseConnection = profiles.FirstOrDefault(profile => profile.Provider == AgentProfile.Muse);
            var names = profiles.Select(profile => profile.Provider switch
            {
                AgentProfile.Codex => "Codex", AgentProfile.Claude => "Claude Code",
                AgentProfile.Gemini => "Gemini CLI", AgentProfile.GitHubCopilot => "GitHub Copilot CLI",
                AgentProfile.Antigravity => "Antigravity CLI", AgentProfile.Kiro => "Kiro CLI", AgentProfile.Qwen => "Qwen Code", AgentProfile.Kimi => "Kimi Code CLI", AgentProfile.Muse => "Muse Code CLI", _ => profile.Provider
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
            _registeredAntigravityConnection = null;
            _registeredKiroConnection = null;
            _registeredQwenConnection = null;
            _registeredKimiConnection = null;
            _registeredMuseConnection = null;
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
            var profile = CreateCheckedAgentConnectionProfile()
                ?? throw new InvalidOperationException("A current CLI check is required before registration.");
            await _agentConnectionStore.SaveVerifiedAsync(profile);
            await LoadAgentConnectionsAsync();
            ConnectionRegistrationStatus = ViewModelText.Get("ConnectionRegistered", AgentProviderDisplayName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ConnectionRegistrationStatus = ViewModelText.Get("ConnectionRegistrationFailed", ex.Message);
        }
    }

    private AgentConnectionProfile? CreateCheckedAgentConnectionProfile()
    {
        if (!CanRegisterAgentConnection() || _cliCheckedAt is not { } checkedAt) return null;
        var candidate = new AgentConnectionProfile(
            AgentProvider,
            // Preserve npm-backed CLI launchers; node.exe is an internal implementation detail.
            AgentProvider is AgentProfile.Qwen or AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Muse ? AgentExecutable.Trim() : CliExecutablePath,
            AgentProvider == AgentProfile.Codex ? (UseAppServer ? "app-server" : AgentProfile.DefaultCodexArguments)
                : AgentProvider == AgentProfile.Claude ? "" : AgentArguments,
            AgentProvider == AgentProfile.Codex && UseAppServer ? "app-server" : "exec",
            AgentModel,
            AgentReasoningEffort,
            AgentPermissionMode,
            AllowAgentNetwork,
            CliVersion,
            checkedAt);
        try
        {
            // ApplyTo runs the verified-store validator and returns its canonical agent values.
            var normalized = candidate.ApplyTo(new BridgeSettings());
            return candidate with
            {
                Provider = normalized.AgentProvider,
                Executable = normalized.AgentExecutable,
                Arguments = normalized.AgentArguments,
                Mode = normalized.AgentMode,
                Model = normalized.AgentModel,
                ReasoningEffort = normalized.AgentReasoningEffort,
                PermissionMode = normalized.AgentPermissionMode,
                CliVersion = candidate.CliVersion.Trim(),
                CheckedAt = candidate.CheckedAt.ToUniversalTime()
            };
        }
        catch (ArgumentException) { return null; }
    }

    [RelayCommand(CanExecute = nameof(HasRegisteredCodexConnection))]
    private void LoadRegisteredCodexConnection() => ApplyRegisteredConnection(_registeredCodexConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredClaudeConnection))]
    private void LoadRegisteredClaudeConnection() => ApplyRegisteredConnection(_registeredClaudeConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredGeminiConnection))]
    private void LoadRegisteredGeminiConnection() => ApplyRegisteredConnection(_registeredGeminiConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredCopilotConnection))]
    private void LoadRegisteredCopilotConnection() => ApplyRegisteredConnection(_registeredCopilotConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredAntigravityConnection))]
    private void LoadRegisteredAntigravityConnection() => ApplyRegisteredConnection(_registeredAntigravityConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredKiroConnection))]
    private void LoadRegisteredKiroConnection() => ApplyRegisteredConnection(_registeredKiroConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredQwenConnection))]
    private void LoadRegisteredQwenConnection() => ApplyRegisteredConnection(_registeredQwenConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredKimiConnection))]
    private void LoadRegisteredKimiConnection() => ApplyRegisteredConnection(_registeredKimiConnection);

    [RelayCommand(CanExecute = nameof(HasRegisteredMuseConnection))]
    private void LoadRegisteredMuseConnection() => ApplyRegisteredConnection(_registeredMuseConnection);

    private void ApplyRegisteredConnection(AgentConnectionProfile? profile)
    {
        if (profile is null) return;
        var applied = profile.ApplyTo(CreateSettingsSnapshot());
        AgentProvider = applied.AgentProvider;
        AgentExecutable = applied.AgentExecutable;
        AgentArguments = applied.AgentArguments;
        UseAppServer = applied.AgentMode == "app-server";
        AgentModel = applied.AgentModel ?? "";
        AgentReasoningEffort = applied.AgentProvider == AgentProfile.Muse ? null : applied.AgentReasoningEffort;
        AgentPermissionMode = applied.AgentProvider == AgentProfile.Muse ? "inherit" : applied.AgentPermissionMode;
        AllowAgentNetwork = applied.AllowAgentNetwork;
        ConnectionRegistrationStatus = ViewModelText.Get("ConnectionLoaded", AgentProviderDisplayName);
        UpdateSavedAgentSummary();
    }
}
