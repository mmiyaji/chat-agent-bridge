using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private bool _restoringSettingsDraft;
    private bool _savingSettings;

    public Func<BridgeSettings, BridgeSettings, Task<bool>>? ConfirmSensitiveSettingsAsync { get; set; }
    public event Action? SettingsDraftEdited;

    [ObservableProperty]
    public partial bool IsSensitiveConfirmationPending { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedSettings)), NotifyPropertyChangedFor(nameof(CanSavePendingSettings))]
    public partial bool HasAgentSettingsChanges { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedSettings)), NotifyPropertyChangedFor(nameof(CanSavePendingSettings))]
    public partial bool HasChatSettingsChanges { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedSettings)), NotifyPropertyChangedFor(nameof(CanSavePendingSettings))]
    public partial bool HasGeneralSettingsChanges { get; private set; }

    public bool HasUnsavedSettings => HasAgentSettingsChanges || HasChatSettingsChanges || HasGeneralSettingsChanges;
    public bool CanSavePendingSettings => HasUnsavedSettings && CanSaveSettings() && !SaveSettingsCommand.IsRunning;

    public async Task<bool> SavePendingSettingsAsync(bool retrySensitiveConfirmation = false)
    {
        // A navigation/exit request can arrive while the visible Save action is still
        // running. Resolve against its completed result, never an earlier success flag.
        if (SaveSettingsCommand.IsRunning)
        {
            if (SaveSettingsCommand.ExecutionTask is not { } pendingSave) return false;
            await pendingSave;
            return _lastSaveSucceeded && IsSettingsLoaded && !HasUnsavedSettings;
        }
        if (retrySensitiveConfirmation) AllowSensitiveSettingsConfirmationRetry();
        if (!HasUnsavedSettings) return true;
        if (!CanSavePendingSettings || SaveGeneralSettingsCommand.IsRunning || _savingSettings) return false;
        await SaveSettingsCommand.ExecuteAsync(null);
        return _lastSaveSucceeded && IsSettingsLoaded && !HasUnsavedSettings;
    }

    public void AllowSensitiveSettingsConfirmationRetry()
    {
        IsSensitiveConfirmationPending = false;
    }

    public async Task<bool> DiscardPendingSettingsAsync()
    {
        if (!HasUnsavedSettings) return IsSettingsLoaded;
        if (_closing || !CanReloadSettings() || SaveSettingsCommand.IsRunning ||
            SaveGeneralSettingsCommand.IsRunning || _savingSettings) return false;
        await LoadAsync();
        return IsSettingsLoaded && !HasUnsavedSettings;
    }

    private void RefreshSettingsDraftChanges(bool notifyEdited = false)
    {
        if (_restoringSettingsDraft || _savingSettings || !IsSettingsLoaded || _settings is null) return;
        var draft = CreateSettingsSnapshot();
        var changes = SettingsDraftChanges.Compare(_settings, draft,
            FileAttachmentMaxSizeMb, FileAttachmentMaxCount, FileAttachmentMaxTotalSizeMb);
        HasAgentSettingsChanges = changes.Agent;
        HasChatSettingsChanges = changes.Chat;
        HasGeneralSettingsChanges = changes.General;
        if (!IsLoading) SetupDraftNotice = HasUnsavedSettings ? ViewModelText.Get("UnsavedSetupChanges") : "";
        if (notifyEdited) SettingsDraftEdited?.Invoke();
    }

    private async Task<bool> ConfirmSensitiveDraftAsync(BridgeSettings baseline, BridgeSettings snapshot)
    {
        if (IsOnboardingActive || !SettingsSensitiveChanges.Compare(baseline, snapshot).HasChanges) return true;
        var rollback = CaptureSettingsDraftRollback(baseline);
        var accepted = false;
        IsSensitiveConfirmationPending = true;
        try
        {
            accepted = ConfirmSensitiveSettingsAsync is { } confirm && await confirm(baseline, snapshot);
            return accepted;
        }
        finally
        {
            // Cancel, an unavailable dialog, and dialog failures all reject the submitted
            // transaction. A later ordinary edit must never resurrect rejected settings.
            try
            {
                if (!accepted) rollback();
            }
            finally
            {
                AllowSensitiveSettingsConfirmationRetry();
            }
        }
    }

    private Action CaptureSettingsDraftRollback(BridgeSettings baseline)
    {
        var submittedDraft = CreateSettingsSnapshot();
        var submittedConnectionStatus = ConnectionRegistrationStatus;
        List<Action> restore = [];
        void Track<T>(Func<T> read, Action<T> write, T saved)
        {
            // Capture raw editor values, including whitespace and NumberBox doubles.
            // Snapshot normalization would mistake some edits made during the dialog
            // for the submitted value and overwrite them on cancellation.
            var submitted = read();
            restore.Add(() =>
            {
                if (EqualityComparer<T>.Default.Equals(read(), submitted)) write(saved);
            });
        }

        Track(() => WorkspaceRoot, value => WorkspaceRoot = value, baseline.WorkspaceRoot);
        Track(() => AgentProvider, value => AgentProvider = value, baseline.AgentProvider);
        Track(() => AgentExecutable, value => AgentExecutable = value, baseline.AgentExecutable);
        Track(() => AgentArguments, value => AgentArguments = value, baseline.AgentArguments);
        Track(() => AgentModel, value => AgentModel = value, baseline.AgentModel ?? "");
        Track(() => AgentReasoningEffort, value => AgentReasoningEffort = value, baseline.AgentReasoningEffort);
        Track(() => UseAppServer, value => UseAppServer = value, baseline.AgentMode == "app-server");
        Track(() => RequireApproval, value => RequireApproval = value, baseline.RequireApproval);
        Track(() => AgentPermissionMode, value => AgentPermissionMode = value, baseline.AgentPermissionMode);
        Track(() => AllowAgentNetwork, value => AllowAgentNetwork = value, baseline.AllowAgentNetwork);
        Track(() => TrustedWorkspaceRoots, value => TrustedWorkspaceRoots = value, baseline.TrustedWorkspaceRoots);
        Track(() => AutoConnectProvider, value => AutoConnectProvider = value, baseline.AutoConnectProvider);
        Track(() => MattermostServerUrl, value => MattermostServerUrl = value, baseline.MattermostServerUrl);
        Track(() => MattermostChannelId, value => MattermostChannelId = value, baseline.MattermostChannelId);
        Track(() => MattermostToken, SetMattermostToken, baseline.MattermostToken);
        Track(() => MattermostBypassProxy, value => MattermostBypassProxy = value, baseline.MattermostBypassProxy);
        Track(() => PostConnectionNotice, value => PostConnectionNotice = value, baseline.PostConnectionNotice);
        Track(() => SlackBotToken, value => SlackBotToken = value, baseline.SlackBotToken);
        Track(() => SlackAppToken, value => SlackAppToken = value, baseline.SlackAppToken);
        Track(() => DiscordBotToken, value => DiscordBotToken = value, baseline.DiscordBotToken);
        Track(() => MattermostAllowedUserIds, value => MattermostAllowedUserIds = value, baseline.MattermostAllowedUserIds);
        Track(() => SlackAllowedUserIds, value => SlackAllowedUserIds = value, baseline.SlackAllowedUserIds);
        Track(() => DiscordAllowedUserIds, value => DiscordAllowedUserIds = value, baseline.DiscordAllowedUserIds);
        Track(() => DiscordAllowedGuildIds, value => DiscordAllowedGuildIds = value, baseline.DiscordAllowedGuildIds);
        Track(() => DiscordAllowedChannelIds, value => DiscordAllowedChannelIds = value, baseline.DiscordAllowedChannelIds);
        Track(() => DiscordAllowedThreadIds, value => DiscordAllowedThreadIds = value, baseline.DiscordAllowedThreadIds);
        Track(() => DiscordAllowDirectMessages, value => DiscordAllowDirectMessages = value, baseline.DiscordAllowDirectMessages);
        Track(() => ShareApprovalsInChat, value => ShareApprovalsInChat = value, baseline.ShareApprovalsInChat);
        Track(() => ShareAgentQuestionsInChat, value => ShareAgentQuestionsInChat = value, baseline.ShareAgentQuestionsInChat);
        Track(() => ChatApprovalMode, value => ChatApprovalMode = value, baseline.ChatApprovalMode);
        Track(() => ChatResultMode, value => ChatResultMode = value, baseline.ChatResultMode);
        Track(() => ChatFollowUpMode, value => ChatFollowUpMode = value, baseline.ChatFollowUpMode);
        Track(() => SlackAllowedChannelIds, value => SlackAllowedChannelIds = value, baseline.SlackAllowedChannelIds);
        Track(() => SlackAllowedThreadIds, value => SlackAllowedThreadIds = value, baseline.SlackAllowedThreadIds);
        Track(() => MattermostAllowedThreadIds, value => MattermostAllowedThreadIds = value, baseline.MattermostAllowedThreadIds);
        Track(() => FileAttachmentApprovalMode, value => FileAttachmentApprovalMode = value, baseline.FileAttachmentApprovalMode);
        Track(() => FileAttachmentMaxSizeMb, value => FileAttachmentMaxSizeMb = value, (double)baseline.FileAttachmentMaxSizeMb);
        Track(() => FileAttachmentMaxCount, value => FileAttachmentMaxCount = value, (double)baseline.FileAttachmentMaxCount);
        Track(() => FileAttachmentMaxTotalSizeMb, value => FileAttachmentMaxTotalSizeMb = value, (double)baseline.FileAttachmentMaxTotalSizeMb);

        return () =>
        {
            _restoringSettingsDraft = true;
            try
            {
                foreach (var restoreField in restore) restoreField();
            }
            finally
            {
                _restoringSettingsDraft = false;
                _effortModel = NormalizeModel(AgentModel);
                PopulateGlobalEfforts();
                UpdateSavedAgentSummary();
                if (SettingsDraftChanges.Compare(submittedDraft, CreateSettingsSnapshot()).Agent &&
                    ConnectionRegistrationStatus == submittedConnectionStatus)
                    ConnectionRegistrationStatus = "";
                RefreshSettingsDraftChanges();
            }
        };
    }

    private static bool IsSettingsDraftProperty(string? propertyName) => propertyName is
        nameof(WorkspaceRoot) or nameof(AgentExecutable) or nameof(AgentArguments) or nameof(AgentProvider) or
        nameof(AgentModel) or nameof(AgentReasoningEffort) or nameof(UseAppServer) or nameof(RequireApproval) or
        nameof(AgentPermissionMode) or nameof(AllowAgentNetwork) or nameof(TrustedWorkspaceRoots) or
        nameof(AutoConnectProvider) or nameof(MattermostServerUrl) or nameof(MattermostChannelId) or
        nameof(MattermostBypassProxy) or nameof(PostConnectionNotice) or nameof(SlackBotToken) or
        nameof(SlackAppToken) or nameof(MattermostAllowedUserIds) or nameof(SlackAllowedUserIds) or
        nameof(DiscordBotToken) or nameof(DiscordAllowedUserIds) or nameof(DiscordAllowedGuildIds) or
        nameof(DiscordAllowedChannelIds) or nameof(DiscordAllowedThreadIds) or nameof(DiscordAllowDirectMessages) or
        nameof(ShareApprovalsInChat) or nameof(ShareAgentQuestionsInChat) or nameof(ChatApprovalMode) or
        nameof(ChatResultMode) or nameof(ChatFollowUpMode) or nameof(SlackAllowedChannelIds) or nameof(SlackAllowedThreadIds) or
        nameof(MattermostAllowedThreadIds) or nameof(FileAttachmentApprovalMode) or
        nameof(FileAttachmentMaxSizeMb) or nameof(FileAttachmentMaxCount) or nameof(FileAttachmentMaxTotalSizeMb);
}
