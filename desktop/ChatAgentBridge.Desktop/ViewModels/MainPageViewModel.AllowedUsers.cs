using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private static readonly string[] ChatProviders = ["slack", "mattermost", "discord"];

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
        foreach (var provider in ChatProviders)
        {
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
                    try
                    {
                        await audit.AppendAsync(new AuditEvent(DateTimeOffset.UtcNow, kind, null, provider, "", "")
                        {
                            Access = new AuditAccessSubject(id, null, actor, AuditAccessSubject.SettingsMethod)
                        });
                    }
                    catch (Exception)
                    {
                        // The settings are already saved. An audit failure must not report the save as failed.
                    }
                }
            }
        }
    }

    private static string[] SplitIds(string value) => value.Split([',', ';', ' ', '\t', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
