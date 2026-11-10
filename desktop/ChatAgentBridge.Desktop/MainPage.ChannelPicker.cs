using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private bool _channelPickerOpen;

    private async void SearchMattermostChannels_Click(object sender, RoutedEventArgs e) => await RunChannelPickerAsync(async () =>
    {
        var selected = await ChatChannelPickerDialog.LoadAndShowAsync(XamlRoot, "Mattermost", CreateChannelPickerSettings(), new[] { ViewModel.MattermostChannelId }, allowMultiple: false);
        if (selected?.Selected.FirstOrDefault() is { } choice) ViewModel.MattermostChannelId = choice.Id;
    });

    private async void SearchSlackChannels_Click(object sender, RoutedEventArgs e) => await RunChannelPickerAsync(async () =>
    {
        var selected = await ChatChannelPickerDialog.LoadAndShowAsync(XamlRoot, "Slack", CreateChannelPickerSettings(), ParseIds(ViewModel.SlackAllowedChannelIds));
        if (selected is not null) ViewModel.SlackAllowedChannelIds = ReplaceVisibleIds(ViewModel.SlackAllowedChannelIds, selected.Channels, selected.Selected, discord: false);
    });

    private async void SearchDiscordChannels_Click(object sender, RoutedEventArgs e) => await RunChannelPickerAsync(async () =>
    {
        var selected = await ChatChannelPickerDialog.LoadAndShowAsync(XamlRoot, "Discord", CreateChannelPickerSettings(), ParseIds(ViewModel.DiscordAllowedChannelIds));
        if (selected is not null) ViewModel.DiscordAllowedChannelIds = ReplaceVisibleIds(ViewModel.DiscordAllowedChannelIds, selected.Channels, selected.Selected, discord: true);
    });

    private async Task RunChannelPickerAsync(Func<Task> showPicker)
    {
        if (_channelPickerOpen) return;
        _channelPickerOpen = true;
        try
        {
            if (await ViewModel.SavePendingSettingsAsync(retrySensitiveConfirmation: true))
                await showPicker();
        }
        finally
        {
            _channelPickerOpen = false;
        }
    }

    private BridgeSettings CreateChannelPickerSettings()
    {
        return new BridgeSettings
        {
            MattermostServerUrl = ViewModel.MattermostServerUrl,
            MattermostToken = ViewModel.MattermostToken,
            MattermostBypassProxy = ViewModel.MattermostBypassProxy,
            SlackBotToken = ViewModel.SlackBotToken,
            DiscordBotToken = ViewModel.DiscordBotToken,
            DiscordAllowedGuildIds = ViewModel.DiscordAllowedGuildIds
        };
    }

    private static string[] ParseIds(string value) => value.Split(['\r', '\n', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ReplaceVisibleIds(string current, IReadOnlyList<ChatChannelChoice> visible, IEnumerable<ChatChannelChoice> selected, bool discord)
    {
        var visibleIds = visible.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var values = ParseIds(current).Where(id => !visibleIds.Contains(id)).Concat(selected.Select(c => c.Id)).Distinct(StringComparer.Ordinal);
        var joined = string.Join(Environment.NewLine, values);
        return discord ? ChatAccessPolicy.NormalizeDiscordSnowflakes(joined) : ChatAccessPolicy.NormalizeScopes(joined);
    }
}
