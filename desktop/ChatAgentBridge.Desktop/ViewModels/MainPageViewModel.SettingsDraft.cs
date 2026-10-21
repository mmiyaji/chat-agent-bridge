using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private bool _restoringSettingsDraft;
    private bool _savingSettings;
    private RejectedSensitiveDraft? _rejectedSensitiveDraft;

    private sealed record RejectedSensitiveDraft(BridgeSettings Settings, double AttachmentSize,
        double AttachmentCount, double AttachmentTotal);

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
        _rejectedSensitiveDraft = null;
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
        if (_rejectedSensitiveDraft is not null && (!MatchesRejectedSensitiveDraft(draft,
                FileAttachmentMaxSizeMb, FileAttachmentMaxCount, FileAttachmentMaxTotalSizeMb) ||
                SettingsSensitiveChanges.Equivalent(_settings, draft) &&
                FileAttachmentMaxSizeMb == _settings.FileAttachmentMaxSizeMb &&
                FileAttachmentMaxCount == _settings.FileAttachmentMaxCount &&
                FileAttachmentMaxTotalSizeMb == _settings.FileAttachmentMaxTotalSizeMb))
            AllowSensitiveSettingsConfirmationRetry();
        if (!IsLoading) SetupDraftNotice = HasUnsavedSettings ? ViewModelText.Get("UnsavedSetupChanges") : "";
        if (notifyEdited) SettingsDraftEdited?.Invoke();
    }

    private bool MatchesRejectedSensitiveDraft(BridgeSettings draft, double size, double count, double total) =>
        _rejectedSensitiveDraft is { } rejected && SettingsSensitiveChanges.Equivalent(rejected.Settings, draft) &&
        rejected.AttachmentSize.Equals(size) && rejected.AttachmentCount.Equals(count) && rejected.AttachmentTotal.Equals(total);

    private async Task<bool> ConfirmSensitiveDraftAsync(BridgeSettings baseline, BridgeSettings snapshot,
        BridgeSettings draft, double size, double count, double total)
    {
        if (IsOnboardingActive || !SettingsSensitiveChanges.Compare(baseline, snapshot).HasChanges) return true;
        if (MatchesRejectedSensitiveDraft(draft, size, count, total)) return false;

        // Retain the rejected projection even if the callback is unavailable or throws.
        // Ordinary edits must never turn an unconfirmed permission/token change into a save.
        _rejectedSensitiveDraft = new(draft, size, count, total);
        IsSensitiveConfirmationPending = true;
        if (ConfirmSensitiveSettingsAsync is not { } confirm || !await confirm(baseline, snapshot)) return false;
        AllowSensitiveSettingsConfirmationRetry();
        return true;
    }

    private static bool IsSettingsDraftProperty(string? propertyName) => propertyName is
        nameof(WorkspaceRoot) or nameof(AgentExecutable) or nameof(AgentArguments) or nameof(AgentProvider) or
        nameof(AgentModel) or nameof(AgentReasoningEffort) or nameof(UseAppServer) or nameof(RequireApproval) or
        nameof(AgentPermissionMode) or nameof(AllowAgentNetwork) or nameof(TrustedWorkspaceRoots) or
        nameof(AutoConnectProvider) or nameof(MattermostServerUrl) or nameof(MattermostChannelId) or
        nameof(MattermostBypassProxy) or nameof(PostConnectionNotice) or nameof(SlackBotToken) or
        nameof(SlackAppToken) or nameof(MattermostAllowedUserIds) or nameof(SlackAllowedUserIds) or
        nameof(ShareApprovalsInChat) or nameof(ShareAgentQuestionsInChat) or nameof(ChatApprovalMode) or
        nameof(ChatResultMode) or nameof(SlackAllowedChannelIds) or nameof(SlackAllowedThreadIds) or
        nameof(MattermostAllowedThreadIds) or nameof(FileAttachmentApprovalMode) or
        nameof(FileAttachmentMaxSizeMb) or nameof(FileAttachmentMaxCount) or nameof(FileAttachmentMaxTotalSizeMb);
}
