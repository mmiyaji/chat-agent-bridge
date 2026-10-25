using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private AgentSettingsDraftStore? _agentSettingsDraftStore;
    private BridgeSettings? _agentEditorSharedSettings;
    private string? _agentEditorProvider;

    /// <summary>Loads one provider into this isolated editor while retaining shared workspace and trust values.</summary>
    public async Task LoadAgentEditorAsync(BridgeSettings shared, string provider)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (!_isAgentProfileEditor) throw new InvalidOperationException("This view model is not an agent profile editor.");
        _agentEditorSharedSettings = shared;
        _agentEditorProvider = NormalizeAgentEditorProvider(provider);
        await LoadAgentEditorCoreAsync(shared, _agentEditorProvider);
    }

    private async Task LoadAgentEditorCoreAsync(BridgeSettings shared, string provider)
    {
        _restoringSettingsDraft = true;
        IsLoading = true;
        IsSettingsLoaded = false;
        StatusTitle = ViewModelText.Get("LoadingSettings");
        StatusMessage = ViewModelText.Get("CheckingSavedSettings");
        StatusSeverity = "Informational";
        IsStatusOpen = true;
        try
        {
            _agentSettingsDraftStore ??= new AgentSettingsDraftStore(_settingsStore.DirectoryPath);
            var draft = (await _agentSettingsDraftStore.ListAsync()).FirstOrDefault(item => item.Provider == provider);
            // Custom CLIs have editable drafts but are intentionally excluded
            // from the verified profiles used for chat-side agent switching.
            var registered = provider == AgentProfile.Custom
                ? null
                : await _agentConnectionStore.FindAsync(provider);
            BridgeSettings editorBaseline;
            if (draft is not null)
                editorBaseline = draft.ApplyTo(shared);
            else if (AgentProfile.ResolveProvider(shared) == provider)
                editorBaseline = shared;
            else if (registered is not null)
                editorBaseline = registered.ApplyTo(shared);
            else
                editorBaseline = CreateAgentEditorDefaults(shared, provider);

            _settings = editorBaseline;
            OnPropertyChanged(nameof(PersistedSettings));
            var settingsPath = Path.Combine(_settingsStore.DirectoryPath, "settings.json");
            (_hasSavedSettings, _savedWorkspaceExists) = await Task.Run(() =>
                (File.Exists(settingsPath), Directory.Exists(shared.WorkspaceRoot)));
            RestoreAgentEditorFields(editorBaseline);
            CancelCliCheck();
            CliCheckSummary = ViewModelText.Get("CliCheckIntro");
            SettingsLocation = _settingsStore.DirectoryPath;
            UpdateSavedAgentSummary();
            RefreshSetupStepper();
            StatusTitle = ViewModelText.Get("SettingsLoaded");
            StatusMessage = ViewModelText.Get("SettingsLoadedDetails");
            StatusSeverity = "Success";
            IsStatusOpen = false;
            IsSettingsLoaded = true;

            await LoadAgentConnectionsAsync();
            await RestoreCliVerificationAsync();
            PopulateGlobalEfforts();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _settings = null;
            OnPropertyChanged(nameof(PersistedSettings));
            IsSettingsLoaded = false;
            _hasSavedSettings = false;
            StatusTitle = ViewModelText.Get("SettingsLoadFailed");
            StatusMessage = ViewModelText.Get("SettingsLoadFailedDetails", ex.Message);
            StatusSeverity = "Error";
            IsStatusOpen = true;
        }
        finally
        {
            _initializingSettings = false;
            _restoringSettingsDraft = false;
            RefreshSettingsDraftChanges(notifyEdited: true);
            IsLoading = false;
        }
        if (IsSettingsLoaded) SettingsSaved?.Invoke();
    }

    private void RestoreAgentEditorFields(BridgeSettings settings)
    {
        WorkspaceRoot = settings.WorkspaceRoot;
        AgentExecutable = settings.AgentExecutable;
        AgentArguments = settings.AgentArguments;
        AgentProvider = settings.AgentProvider;
        _initializingSettings = true;
        AgentModel = settings.AgentModel ?? "";
        AgentReasoningEffort = settings.AgentReasoningEffort;
        _effortModel = NormalizeModel(AgentModel);
        _unsupportedSavedEffort = false;
        _hasCatalogForCurrentModel = false;
        SelectedReasoningEffort = null;
        ModelEffortStatus = settings.AgentReasoningEffort is { Length: > 0 } savedEffort
            ? ViewModelText.Get("SavedEffortPending", savedEffort)
            : ViewModelText.Get("UseDefaultCliValue");
        _initializingSettings = false;

        AutoConnectProvider = settings.AutoConnectProvider;
        UseAppServer = settings.AgentMode == "app-server";
        MattermostServerUrl = settings.MattermostServerUrl;
        MattermostBypassProxy = settings.MattermostBypassProxy;
        MattermostChannelId = settings.MattermostChannelId;
        PostConnectionNotice = settings.PostConnectionNotice;
        MattermostToken = settings.MattermostToken;
        SlackBotToken = settings.SlackBotToken;
        SlackAppToken = settings.SlackAppToken;
        MattermostAllowedUserIds = settings.MattermostAllowedUserIds;
        SlackAllowedUserIds = settings.SlackAllowedUserIds;
        ShareApprovalsInChat = settings.ShareApprovalsInChat;
        ShareAgentQuestionsInChat = settings.ShareAgentQuestionsInChat;
        RequireApproval = settings.RequireApproval;
        AgentPermissionMode = settings.AgentPermissionMode;
        AllowAgentNetwork = settings.AllowAgentNetwork;
        TrustedWorkspaceRoots = settings.TrustedWorkspaceRoots;
        ChatApprovalMode = settings.ChatApprovalMode;
        AutoApproveSwitchCommands = settings.AutoApproveSwitchCommands;
        ChatResultMode = settings.ChatResultMode;
        ChatFollowUpMode = settings.ChatFollowUpMode;
        FileAttachmentApprovalMode = settings.FileAttachmentApprovalMode;
        FileAttachmentMaxSizeMb = settings.FileAttachmentMaxSizeMb;
        FileAttachmentMaxCount = settings.FileAttachmentMaxCount;
        FileAttachmentMaxTotalSizeMb = settings.FileAttachmentMaxTotalSizeMb;
        SlackAllowedChannelIds = settings.SlackAllowedChannelIds;
        SlackAllowedThreadIds = settings.SlackAllowedThreadIds;
        MattermostAllowedThreadIds = settings.MattermostAllowedThreadIds;
        InvalidateMattermostUsernameLookup();
        InvalidateMattermostThreadLookup();
    }

    private static BridgeSettings CreateAgentEditorDefaults(BridgeSettings shared, string provider) => shared with
    {
        AgentProvider = provider,
        AgentExecutable = provider switch
        {
            AgentProfile.Codex => "codex",
            AgentProfile.Claude => "claude",
            AgentProfile.Gemini => "gemini",
            AgentProfile.GitHubCopilot => "copilot",
            AgentProfile.Antigravity => "agy",
            _ => ""
        },
        AgentArguments = provider switch
        {
            AgentProfile.Codex => AgentProfile.DefaultCodexArguments,
            AgentProfile.Claude => "--print --output-format stream-json --verbose",
            AgentProfile.Gemini => AgentProfile.DefaultGeminiArguments,
            AgentProfile.GitHubCopilot => AgentProfile.DefaultGitHubCopilotArguments,
            AgentProfile.Antigravity => AgentProfile.DefaultAntigravityArguments,
            _ => ""
        },
        AgentMode = "exec",
        AgentModel = null,
        AgentReasoningEffort = null,
        AgentPermissionMode = "inherit",
        AllowAgentNetwork = false
    };

    private static string NormalizeAgentEditorProvider(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Antigravity or AgentProfile.Custom => normalized,
            _ => throw new ArgumentException("Only supported agent providers can be edited.", nameof(provider))
        };
    }

    /// <summary>Saves an editor draft and registers it only after the current CLI check has passed.</summary>
    public async Task<bool> CommitAgentEditorCheckAsync()
    {
        if (!_isAgentProfileEditor || !await SavePendingSettingsAsync(retrySensitiveConfirmation: true)) return false;
        if (AgentProvider == AgentProfile.Custom) return HasVerifiedAgentEditor;
        if (!CanRegisterAgentConnection()) return false;
        await RegisterAgentConnectionCommand.ExecuteAsync(null);
        return GetVerifiedAgentEditorProfile() is not null;
    }

    public bool HasVerifiedAgentEditor => GetVerifiedAgentEditorProfile() is not null ||
        (_isAgentProfileEditor && AgentProvider == AgentProfile.Custom && !IsLoading && !IsCheckingCli &&
         !HasUnsavedSettings && HasCurrentCliCheck() && _checkedConnectionSignature == CurrentConnectionSignature());

    /// <summary>Returns the checked profile currently represented by this editor.</summary>
    public AgentConnectionProfile? GetVerifiedAgentEditorProfile()
    {
        if (!_isAgentProfileEditor) return null;
        var checkedProfile = CreateCheckedAgentConnectionProfile();
        if (checkedProfile is null) return null;
        var profile = AgentProvider switch
        {
            AgentProfile.Codex => _registeredCodexConnection,
            AgentProfile.Claude => _registeredClaudeConnection,
            AgentProfile.Gemini => _registeredGeminiConnection,
            AgentProfile.GitHubCopilot => _registeredCopilotConnection,
            AgentProfile.Antigravity => _registeredAntigravityConnection,
            _ => null
        };
        return profile == checkedProfile ? profile : null;
    }

    /// <summary>Activates a registered profile only if it was checked against the current shared workspace context.</summary>
    public async Task<bool> ActivateAgentEditorAsync(AgentConnectionProfile profile, MainPageViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(editor);
        var checkedContext = editor.PersistedSettings;
        if (!editor._isAgentProfileEditor || editor.AgentProvider == AgentProfile.Custom ||
            editor.IsLoading || editor.IsCheckingCli || editor.HasUnsavedSettings ||
            editor.GetVerifiedAgentEditorProfile() != profile) return false;
        if (_isAgentProfileEditor || _closing || IsRunning || !await SavePendingSettingsAsync(retrySensitiveConfirmation: true)) return false;
        AgentConnectionProfile? registered;
        try { registered = await _agentConnectionStore.FindAsync(profile.Provider); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        if (registered is null || registered != profile) return false;

        // ApplyTo validates the profile again and preserves the root workspace/trust/chat state.
        var active = CreateSettingsSnapshot();
        if (checkedContext is null || editor.GetVerifiedAgentEditorProfile() != profile || editor.HasUnsavedSettings ||
            editor.IsLoading || editor.IsCheckingCli || !SameSharedWorkspaceContext(active, checkedContext)) return false;
        var applied = profile.ApplyTo(active);
        if (!await ApplyAgentFieldsAndSaveAsync(applied)) return false;
        await SaveActivatedCliVerificationAsync(editor, applied);
        return true;
    }

    /// <summary>Explicitly activates a checked custom executable without registering it as a conversation-switch profile.</summary>
    public async Task<bool> ActivateCheckedCustomAgentEditorAsync(MainPageViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var checkedContext = editor.PersistedSettings;
        if (_isAgentProfileEditor || _closing || IsRunning || !editor._isAgentProfileEditor || editor.AgentProvider != AgentProfile.Custom ||
            editor.IsLoading || editor.IsCheckingCli || editor.HasUnsavedSettings || !editor.HasVerifiedAgentEditor ||
            !await SavePendingSettingsAsync(retrySensitiveConfirmation: true)) return false;
        var active = CreateSettingsSnapshot();
        if (checkedContext is null || !editor.HasVerifiedAgentEditor || editor.HasUnsavedSettings || editor.IsLoading ||
            editor.IsCheckingCli || editor.AgentProvider != AgentProfile.Custom || !SameSharedWorkspaceContext(active, checkedContext)) return false;
        var applied = ApplyAgentEditorFields(editor.CreateSettingsSnapshot(), active);
        try { _ = AgentProfile.BuildInvocation(applied); }
        catch (ArgumentException) { return false; }
        if (!await ApplyAgentFieldsAndSaveAsync(applied)) return false;
        await SaveActivatedCliVerificationAsync(editor, applied);
        return true;
    }

    /// <summary>Invalidates editor proof when its shared workspace or trust scope changes.</summary>
    public void InvalidateAgentEditorContext()
    {
        if (!_isAgentProfileEditor) return;
        CancelCliCheck();
        CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
    }

    private async Task<bool> ApplyAgentFieldsAndSaveAsync(BridgeSettings applied)
    {
        _restoringSettingsDraft = true;
        try
        {
            AgentProvider = applied.AgentProvider;
            AgentExecutable = applied.AgentExecutable;
            AgentArguments = applied.AgentArguments;
            UseAppServer = applied.AgentMode == "app-server";
            AgentModel = applied.AgentModel ?? "";
            AgentReasoningEffort = applied.AgentReasoningEffort;
            AgentPermissionMode = applied.AgentPermissionMode;
            AllowAgentNetwork = applied.AllowAgentNetwork;
        }
        finally { _restoringSettingsDraft = false; }
        RefreshSettingsDraftChanges(notifyEdited: true);
        if (!await SavePendingSettingsAsync(retrySensitiveConfirmation: true)) return false;
        return true;
    }

    private static bool SameSharedWorkspaceContext(BridgeSettings active, BridgeSettings checkedContext) =>
        string.Equals(active.WorkspaceRoot, checkedContext.WorkspaceRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExecutionPolicy.NormalizeTrustedRoots(active.TrustedWorkspaceRoots),
            ExecutionPolicy.NormalizeTrustedRoots(checkedContext.TrustedWorkspaceRoots), StringComparison.OrdinalIgnoreCase);

    private static BridgeSettings ApplyAgentEditorFields(BridgeSettings source, BridgeSettings target) => target with
    {
        AgentProvider = source.AgentProvider,
        AgentExecutable = source.AgentExecutable,
        AgentArguments = source.AgentArguments,
        AgentMode = source.AgentMode,
        AgentModel = source.AgentModel,
        AgentReasoningEffort = source.AgentReasoningEffort,
        AgentPermissionMode = source.AgentPermissionMode,
        AllowAgentNetwork = source.AllowAgentNetwork
    };

    /// <summary>Refreshes diagnostics only when this editor still describes the active configuration.</summary>
    public async Task SynchronizeAgentEditorCliVerificationAsync(MainPageViewModel editor)
    {
        if (_isAgentProfileEditor || _closing || !IsSettingsLoaded || IsLoading || IsCheckingCli ||
            !editor._isAgentProfileEditor || editor.IsLoading || editor.IsCheckingCli || !editor.IsSettingsLoaded) return;
        var active = CreateSettingsSnapshot();
        var source = editor.CreateSettingsSnapshot();
        var signature = CurrentCliSignature();
        var connectionSignature = CurrentConnectionSignature();
        var editorGeneration = editor._cliCheckGeneration;
        var editorSignature = editor.CurrentCliSignature();
        var editorConnectionSignature = editor.CurrentConnectionSignature();
        try
        {
            // Cache context checks the resolved executable, arguments, mode, workspace and trust;
            // the connection signature also prevents model/permission drafts from affecting the overview.
            if ((signature != editorSignature && !CliVerificationStore.HasSameContext(active, source)) ||
                CreateConnectionSignature(active, editor.CliVersion, editor.CliExecutablePath) != editor.CurrentConnectionSignature()) return;
            var cached = await LoadCliVerificationAsync(active);
            if (_closing || IsCheckingCli || signature != CurrentCliSignature() || connectionSignature != CurrentConnectionSignature() ||
                editor.IsCheckingCli || editorGeneration != editor._cliCheckGeneration || editorSignature != editor.CurrentCliSignature() ||
                editorConnectionSignature != editor.CurrentConnectionSignature()) return;
            if (cached is not null && editor.HasCurrentCliCheck() && cached.CheckedAtUtc == editor._cliCheckedAt &&
                cached.Probe.Version == editor.CliVersion && cached.Probe.Executable == editor.CliExecutablePath)
                ApplyCliVerification(cached, signature, restored: false);
            else
            {
                CancelCliCheck();
                InvalidateModelCatalog();
                CliVersion = ViewModelText.Get("NotChecked");
                CliExecutablePath = "";
                CliCheckFailed = editor.CliCheckFailed;
                CliCheckSummary = editor.CliCheckSummary;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Optional diagnostic synchronization cannot activate a profile or change its permissions.
        }
    }

    private async Task SaveActivatedCliVerificationAsync(MainPageViewModel editor, BridgeSettings applied)
    {
        try
        {
            var sourceSettings = editor.CreateSettingsSnapshot();
            var cached = await editor.LoadCliVerificationAsync(sourceSettings);
            if (cached is null || cached.ConnectionSignature != editor.CurrentConnectionSignature()) return;
            var transferred = cached with
            {
                ConnectionSignature = CreateConnectionSignature(applied, cached.Probe.Version, cached.Probe.Executable)
            };
            await ProviderCliVerificationStore(applied).SaveAsync(applied, transferred);
            CliVersion = cached.Probe.Version;
            CliExecutablePath = cached.Probe.Executable;
            await RestoreCliVerificationAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            CliCheckSummary = ViewModelText.Get("CliVerificationSaveFailed");
        }
    }

}
