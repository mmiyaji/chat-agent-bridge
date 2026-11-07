using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private static readonly string[] ChatProviders = ["slack", "mattermost", "discord"];
    private AllowedUserListModel? _slackAllowedUsers;
    private AllowedUserListModel? _mattermostAllowedUsers;
    private AllowedUserListModel? _discordAllowedUsers;
    private CancellationTokenSource? _allowedUserPictures;

    public AllowedUserListModel SlackAllowedUsers => _slackAllowedUsers ??=
        new("slack", () => SlackAllowedUserIds, value => SlackAllowedUserIds = value, ChatAccessPolicy.NormalizeAllowedUsers);
    public AllowedUserListModel MattermostAllowedUsers => _mattermostAllowedUsers ??=
        new("mattermost", () => MattermostAllowedUserIds, value => MattermostAllowedUserIds = value, ChatAccessPolicy.NormalizeAllowedUsers);
    public AllowedUserListModel DiscordAllowedUsers => _discordAllowedUsers ??=
        new("discord", () => DiscordAllowedUserIds, value => DiscordAllowedUserIds = value, ChatAccessPolicy.NormalizeDiscordSnowflakes);

    partial void OnSlackAllowedUserIdsChanged(string value) => RefreshAllowedUserRows(SlackAllowedUsers);
    partial void OnMattermostAllowedUserIdsChanged(string value) => RefreshAllowedUserRows(MattermostAllowedUsers);
    partial void OnDiscordAllowedUserIdsChanged(string value) => RefreshAllowedUserRows(DiscordAllowedUsers);

    private void RefreshAllowedUserRows(AllowedUserListModel model)
    {
        model.Refresh();
        StartAllowedUserPictures([model]);
    }

    /// <summary>Reloads names and how each user was added, then looks up profile pictures in the background.</summary>
    public async Task RefreshAllowedUserDetailsAsync()
    {
        IReadOnlyList<AllowedUserRecord> records;
        try { records = await Task.Run(() => new AllowedUserDirectory(_settingsStore.DirectoryPath).LoadAsync()); }
        catch (Exception) { return; }
        AllowedUserListModel[] models = [SlackAllowedUsers, MattermostAllowedUsers, DiscordAllowedUsers];
        foreach (var model in models) model.SetRecords(records);
        StartAllowedUserPictures(models);
    }

    private void StartAllowedUserPictures(IEnumerable<AllowedUserListModel> models)
    {
        if (_settings is not { } settings) return;
        var rows = models.SelectMany(model => model.Rows).Where(row => row.AvatarPath is null).Take(40).ToArray();
        if (rows.Length == 0) return;
        _allowedUserPictures?.Cancel();
        var cancellation = _allowedUserPictures = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var directory = _settingsStore.DirectoryPath;
        _ = Task.Run(async () =>
        {
            using var cache = new ChatProfileImageCache(directory);
            foreach (var row in rows)
            {
                if (cancellation.IsCancellationRequested) return;
                try
                {
                    var record = row.Record;
                    var key = ChatSessionScope.Create(row.Provider, record?.ChannelId ?? "", record?.ThreadId ?? "", row.UserId,
                        row.Provider == "mattermost" ? record?.Origin ?? settings.MattermostServerUrl : null);
                    var path = await cache.ResolveAsync(settings, key, cancellation.Token);
                    if (path is not null) dispatcher?.TryEnqueue(() => row.AvatarPath = path);
                }
                catch (Exception)
                {
                    // Pictures are optional; the row keeps its initials.
                }
            }
        });
    }

    /// <summary>
    /// Takes allowed users that a chat access approval saved while settings may be open. An untouched
    /// list follows the saved value; an edited list keeps the edit and gains the newly allowed IDs, so a
    /// later save neither drops the approval nor discards the user's typing.
    /// </summary>
    public void ApplySavedAllowedUsers(BridgeSettings saved)
    {
        if (_settings is not { } previous) return;
        MattermostAllowedUserIds = Merge(MattermostAllowedUserIds, previous.MattermostAllowedUserIds, saved.MattermostAllowedUserIds);
        SlackAllowedUserIds = Merge(SlackAllowedUserIds, previous.SlackAllowedUserIds, saved.SlackAllowedUserIds);
        DiscordAllowedUserIds = Merge(DiscordAllowedUserIds, previous.DiscordAllowedUserIds, saved.DiscordAllowedUserIds);
        _settings = previous with
        {
            MattermostAllowedUserIds = saved.MattermostAllowedUserIds,
            SlackAllowedUserIds = saved.SlackAllowedUserIds,
            DiscordAllowedUserIds = saved.DiscordAllowedUserIds
        };
        OnPropertyChanged(nameof(PersistedSettings));
        RefreshSettingsDraftChanges();
        _ = RefreshAllowedUserDetailsAsync();

        static string Merge(string draft, string previousSaved, string saved)
        {
            if (draft == previousSaved) return saved;
            var added = SplitIds(saved).Except(SplitIds(previousSaved), StringComparer.Ordinal)
                .Where(id => !SplitIds(draft).Contains(id, StringComparer.Ordinal)).ToArray();
            if (added.Length == 0) return draft;
            return string.IsNullOrWhiteSpace(draft)
                ? string.Join(Environment.NewLine, added)
                : draft.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, added);
        }
    }

    private async Task AuditAllowedUserChangesAsync(BridgeSettings before, BridgeSettings after)
    {
        var actor = Environment.UserName;
        var audit = new AuditStore(_settingsStore.DirectoryPath);
        var directory = new AllowedUserDirectory(_settingsStore.DirectoryPath);
        foreach (var provider in ChatProviders)
        {
            var model = provider switch { "slack" => SlackAllowedUsers, "mattermost" => MattermostAllowedUsers, _ => DiscordAllowedUsers };
            var previous = ChatAccessPolicy.AllowedUserIds(before, provider);
            var current = ChatAccessPolicy.AllowedUserIds(after, provider);
            foreach (var (kind, ids) in new[]
            {
                ("allowed_user_added", current.Except(previous, StringComparer.Ordinal)),
                ("allowed_user_removed", previous.Except(current, StringComparer.Ordinal))
            })
            {
                foreach (var id in ids)
                {
                    model.PendingNames.TryGetValue(id, out var name);
                    try
                    {
                        await audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, null, provider, "", "")
                        {
                            Access = new AuditAccessSubject(id, name, actor, AuditAccessSubject.SettingsMethod)
                        });
                        if (kind == "allowed_user_added")
                            await directory.RecordAddedAsync(new AllowedUserRecord(provider, id, name, DateTimeOffset.UtcNow,
                                AllowedUserDirectory.SettingsMethod, actor,
                                provider == "mattermost" ? after.MattermostServerUrl : null));
                        else
                            await directory.RecordRemovedAsync(provider, id);
                    }
                    catch (Exception)
                    {
                        // The settings are already saved. A log or detail failure must not report the save as failed.
                    }
                }
            }
            model.ClearPendingNames();
        }
        _ = RefreshAllowedUserDetailsAsync();
    }

    private static string[] SplitIds(string value) => value.Split([',', ';', ' ', '\t', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
