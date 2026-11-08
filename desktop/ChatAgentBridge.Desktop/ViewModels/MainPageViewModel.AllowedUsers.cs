using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private static readonly string[] ChatProviders = ["slack", "mattermost", "discord"];
    private int _allowedUserNameRefreshGeneration;
    private AllowedUserListModel? _slackAllowedUsers;
    private AllowedUserListModel? _mattermostAllowedUsers;
    private AllowedUserListModel? _discordAllowedUsers;
    private CancellationTokenSource? _allowedUserPictures;

    private AllowedUserTableModel? _allowedUserTable;
    public AllowedUserTableModel AllowedUserTable => _allowedUserTable ??=
        new(MattermostAllowedUsers, SlackAllowedUsers, DiscordAllowedUsers);

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
        await ResolveAllowedUserNamesAsync(models);
        StartAllowedUserPictures(models);
    }

    private async Task ResolveAllowedUserNamesAsync(AllowedUserListModel[] models)
    {
        if (_settings is not { } settings) return;
        var generation = Interlocked.Increment(ref _allowedUserNameRefreshGeneration);
        var rows = models.SelectMany(model => model.Rows)
            .Where(row => (row.Provider != "discord" || AllowedUserListModel.SplitIds(settings.DiscordAllowedChannelIds).Length > 0) &&
                (row.Provider != "mattermost" || IsMattermostBaseUrl(settings.MattermostServerUrl)))
            .Take(100).ToArray();
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slack"] = "slack",
            ["mattermost"] = MattermostNameScope(settings.MattermostServerUrl),
            ["discord"] = "discord:" + (AllowedUserListModel.SplitIds(settings.DiscordAllowedChannelIds).FirstOrDefault() ?? "")
        };
        var noNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models) model.SetResolvedNames(scopes[model.Provider], noNames);
        if (rows.Length == 0) return;

        try
        {
            var keys = rows.Select(row => row.Provider switch
            {
                "mattermost" => ChatSessionScope.Create("mattermost", "", "", row.UserId, settings.MattermostServerUrl),
                "slack" => ChatSessionScope.Create("slack", "", "", row.UserId),
                "discord" => ChatSessionScope.Create("discord", AllowedUserListModel.SplitIds(settings.DiscordAllowedChannelIds).FirstOrDefault() ?? "", "", row.UserId),
                _ => ChatSessionScope.Create(row.Provider, "", "", row.UserId)
            }).ToArray();
            using var identities = new ChatIdentityDirectory(_settingsStore.DirectoryPath);
            var cached = await identities.ReadCachedAsync(keys);
            ApplyNames(cached);
            var resolved = await identities.ResolveAsync(settings, keys);
            ApplyNames(resolved);
        }
        catch (Exception)
        {
            // The ID remains visible if the chat service is unavailable or profile lookup is denied.
        }

        void ApplyNames(IReadOnlyDictionary<SessionKey, ChatIdentityLabels> labels)
        {
            if (generation != Volatile.Read(ref _allowedUserNameRefreshGeneration) || !HasSameIdentitySettings(settings, _settings)) return;
            foreach (var model in models)
            {
                var names = rows.Where(row => row.Provider == model.Provider)
                    .Select(row => (row.UserId, Name: labels.FirstOrDefault(pair => pair.Key.UserId == row.UserId && pair.Key.Provider == row.Provider).Value?.UserName))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.UserId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Name!, StringComparer.Ordinal);
                model.SetResolvedNames(scopes[model.Provider], names);
            }
        }

        static string MattermostNameScope(string url)
        {
            try { return ChatSessionScope.Create("mattermost", "", "", "scope", url).Origin ?? "mattermost:" + url.Trim(); }
            catch (Exception) { return "mattermost:" + url.Trim(); }
        }
    }

    private static bool IsMattermostBaseUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool HasSameIdentitySettings(BridgeSettings expected, BridgeSettings? current) => current is not null &&
        string.Equals(expected.MattermostServerUrl, current.MattermostServerUrl, StringComparison.Ordinal) &&
        string.Equals(expected.MattermostToken, current.MattermostToken, StringComparison.Ordinal) &&
        string.Equals(expected.SlackBotToken, current.SlackBotToken, StringComparison.Ordinal) &&
        string.Equals(expected.DiscordBotToken, current.DiscordBotToken, StringComparison.Ordinal) &&
        string.Equals(expected.DiscordAllowedChannelIds, current.DiscordAllowedChannelIds, StringComparison.Ordinal);

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
