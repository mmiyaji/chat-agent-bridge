using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

internal sealed record ChatChannelPickerResult(IReadOnlyList<ChatChannelChoice> Channels, IReadOnlyList<ChatChannelChoice> Selected);

internal static class ChatChannelPickerDialog
{
    public static async Task<ChatChannelPickerResult?> LoadAndShowAsync(
        XamlRoot root, string provider, BridgeSettings settings, IEnumerable<string> alreadySelected, bool allowMultiple = true)
    {
        var loading = new ContentDialog
        {
            Title = DesktopText.Get("ChannelPickerLoadingTitle"),
            Content = new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center },
            CloseButtonText = DesktopText.Get("Cancel"),
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = root
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var userClosed = false;
        var hideRequested = false;
        loading.Closed += (_, _) =>
        {
            if (hideRequested) return;
            userClosed = true;
            cancellation.Cancel();
        };
        var fetch = ChatChannelDirectory.ListAsync(provider, settings, cancellation.Token);
        var loadingTask = loading.ShowAsync().AsTask();
        ChatChannelDirectoryResult directory;
        try
        {
            directory = await fetch;
        }
        catch (OperationCanceledException)
        {
            hideRequested = true;
            loading.Hide();
            await loadingTask;
            if (userClosed) return null;
            await ShowErrorAsync(root, "ChannelPickerTimedOut");
            return null;
        }
        catch (Exception ex)
        {
            hideRequested = true;
            loading.Hide();
            await loadingTask;
            var key = ex.Message switch
            {
                "TokenRequired" => "ChannelPickerTokenRequired",
                "ServerUrlInvalid" => "ChannelPickerServerUrlInvalid",
                "SlackMissingScope" => "ChannelPickerSlackMissingScope",
                "DiscordGuildRequired" => "ChannelPickerDiscordGuildRequired",
                "DiscordGuildIdsInvalid" => "ChannelPickerDiscordGuildIdsInvalid",
                "DiscordTooManyGuilds" => "ChannelPickerDiscordTooManyGuilds",
                "ResponseTooLarge" => "ChannelPickerResponseTooLarge",
                _ => "ChannelPickerConnectionFailed"
            };
            await ShowErrorAsync(root, key);
            return null;
        }
        hideRequested = true;
        loading.Hide();
        await loadingTask;
        var selected = await ShowAsync(root, provider, directory.ConnectionName, directory.Channels, alreadySelected, allowMultiple);
        return selected is null ? null : new ChatChannelPickerResult(directory.Channels, selected);
    }

    private static async Task ShowErrorAsync(XamlRoot root, string key)
    {
        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("ChannelPickerErrorTitle"),
            Content = new TextBlock { Text = DesktopText.Get(key), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = DesktopText.Get("Close"),
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = root
        };
        await dialog.ShowAsync();
    }

    public static async Task<IReadOnlyList<ChatChannelChoice>?> ShowAsync(XamlRoot root, string provider, string connectionName, IReadOnlyList<ChatChannelChoice> channels, IEnumerable<string> alreadySelected, bool allowMultiple = true)
    {
        var selected = new HashSet<string>(alreadySelected.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        ChatChannelChoice? activeChannel = null;
        var search = new TextBox { PlaceholderText = DesktopText.Get("ChannelPickerSearch"), Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(search, "ChannelPickerSearchBox");
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(count, "ChannelPickerSelectedCount");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, IsItemClickEnabled = false };
        AutomationProperties.SetAutomationId(list, "ChannelPickerResults");
        var noMatches = new TextBlock { Text = DesktopText.Get("ChannelPickerNoMatches"), Visibility = Visibility.Collapsed, Margin = new Thickness(8, 12, 8, 0), TextWrapping = TextWrapping.Wrap };
        var connectionLabel = new TextBlock { Text = DesktopText.Get("ChannelPickerConnectionLabel", connectionName), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(connectionLabel, "ChannelPickerConnectionName");
        var detailsName = new TextBlock { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(detailsName, "ChannelPickerDetailsName");
        var detailsContext = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(detailsContext, "ChannelPickerDetailsContext");
        var detailsDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(detailsDescription, "ChannelPickerDetailsDescription");
        var detailsMemberCount = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(detailsMemberCount, "ChannelPickerMemberCount");
        var detailsId = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(detailsId, "ChannelPickerDetailsId");
        var allowChannel = new CheckBox { Content = DesktopText.Get(allowMultiple ? "ChannelPickerAllowChannel" : "ChannelPickerUseChannel"), IsEnabled = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(allowChannel, "ChannelPickerAllowToggle");
        var rowChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        var isUpdatingToggle = false;
        var isRefreshingList = false;
        void RefreshCount() => count.Text = DesktopText.Get("ChannelPickerSelectedCount", selected.Count);
        void SyncSelectionControls()
        {
            isUpdatingToggle = true;
            foreach (var (id, check) in rowChecks)
            {
                check.IsChecked = selected.Contains(id);
            }
            allowChannel.IsChecked = activeChannel is not null && selected.Contains(activeChannel.Id);
            isUpdatingToggle = false;
            RefreshCount();
        }
        void ShowDetails(ChatChannelChoice? channel)
        {
            activeChannel = channel;
            var hasChannel = channel is not null;
            detailsName.Text = hasChannel ? channel!.Name : DesktopText.Get("ChannelPickerDetailsEmpty");
            detailsContext.Text = hasChannel ? FormatContext(provider, channel!.Context) : "";
            detailsDescription.Text = hasChannel ? channel!.Description ?? "" : "";
            detailsDescription.Visibility = hasChannel && !string.IsNullOrWhiteSpace(channel!.Description) ? Visibility.Visible : Visibility.Collapsed;
            detailsMemberCount.Text = hasChannel && channel!.MemberCount is int members ? DesktopText.Get("ChannelPickerMemberCount", members) : "";
            detailsMemberCount.Visibility = hasChannel && channel!.MemberCount.HasValue ? Visibility.Visible : Visibility.Collapsed;
            detailsId.Text = hasChannel ? DesktopText.Get("ChannelPickerIdentifier", channel!.Id) : "";
            allowChannel.IsEnabled = hasChannel;
            allowChannel.Visibility = hasChannel ? Visibility.Visible : Visibility.Collapsed;
            isUpdatingToggle = true;
            allowChannel.IsChecked = hasChannel && selected.Contains(channel!.Id);
            isUpdatingToggle = false;
        }
        void Refresh()
        {
            var previousActiveId = activeChannel?.Id;
            var query = search.Text.Trim();
            var filtered = channels.Where(c => query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || c.Context.Contains(query, StringComparison.CurrentCultureIgnoreCase) || c.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            isRefreshingList = true;
            list.Items.Clear();
            rowChecks.Clear();
            foreach (var channel in filtered)
            {
                var item = new ListViewItem { Tag = channel };
                AutomationProperties.SetName(item, channel.Name + " — " + FormatContext(provider, channel.Context));
                AutomationProperties.SetAutomationId(item, "ChannelPickerRow_" + channel.Id);
                var row = new Grid { ColumnSpacing = 8, MinHeight = 68, VerticalAlignment = VerticalAlignment.Center };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var check = new CheckBox { IsChecked = selected.Contains(channel.Id), Width = 32, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(0) };
                AutomationProperties.SetAutomationId(check, "ChannelPickerOption_" + channel.Id);
                AutomationProperties.SetName(check, DesktopText.Get("ChannelPickerAllowNamedChannel", channel.Name));
                check.Checked += (_, _) => SetSelected(channel, true);
                check.Unchecked += (_, _) => SetSelected(channel, false);
                row.Children.Add(check);
                var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(new TextBlock { Text = channel.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
                var supportingText = string.IsNullOrWhiteSpace(channel.Description) ? FormatContext(provider, channel.Context) : channel.Description;
                labels.Children.Add(new TextBlock { Text = supportingText, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.78, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(labels, 1);
                row.Children.Add(labels);
                var context = new TextBlock { Text = FormatContext(provider, channel.Context), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                Grid.SetColumn(context, 2);
                row.Children.Add(context);
                item.Content = row;
                rowChecks[channel.Id] = check;
                list.Items.Add(item);
            }
            noMatches.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var initial = filtered.FirstOrDefault(c => c.Id == previousActiveId)
                ?? filtered.FirstOrDefault(c => selected.Contains(c.Id))
                ?? filtered.FirstOrDefault();
            if (initial is not null)
            {
                var item = list.Items.OfType<ListViewItem>().FirstOrDefault(i => ((ChatChannelChoice)i.Tag).Id == initial.Id);
                list.SelectedItem = item;
                ShowDetails(initial);
            }
            else
            {
                ShowDetails(null);
            }
            isRefreshingList = false;
            SyncSelectionControls();
        }
        search.TextChanged += (_, _) => Refresh();
        list.SelectionChanged += (_, _) =>
        {
            if (isRefreshingList) return;
            var channel = (list.SelectedItem as ListViewItem)?.Tag as ChatChannelChoice;
            ShowDetails(channel);
        };
        void SetSelected(ChatChannelChoice channel, bool isSelected)
        {
            if (isUpdatingToggle) return;
            if (isSelected)
            {
                if (!allowMultiple) selected.Clear();
                selected.Add(channel.Id);
                activeChannel = channel;
                ShowDetails(channel);
            }
            else selected.Remove(channel.Id);
            SyncSelectionControls();
        }
        allowChannel.Checked += (_, _) => { if (activeChannel is not null) SetSelected(activeChannel, true); };
        allowChannel.Unchecked += (_, _) => { if (activeChannel is not null) SetSelected(activeChannel, false); };

        var leftPane = new Grid { RowSpacing = 8 };
        leftPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(search, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(noMatches, 1);
        Grid.SetRow(count, 2);
        leftPane.Children.Add(search);
        leftPane.Children.Add(list);
        leftPane.Children.Add(noMatches);
        leftPane.Children.Add(count);

        var detailContent = new StackPanel { Spacing = 12, Margin = new Thickness(18, 8, 8, 8) };
        detailContent.Children.Add(detailsName);
        detailContent.Children.Add(detailsContext);
        detailContent.Children.Add(detailsDescription);
        detailContent.Children.Add(detailsMemberCount);
        detailContent.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 4, 0, 4)
        });
        detailContent.Children.Add(allowChannel);
        detailContent.Children.Add(detailsId);
        var detailsPane = new ScrollViewer { Content = detailContent, VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(detailsPane, "ChannelPickerDetails");
        var narrowLayout = root.Size.Width < 760;
        var contentWidth = Math.Max(280, Math.Min(780, root.Size.Width - (narrowLayout ? 120 : 96)));
        var panes = new Grid { ColumnSpacing = 0, Width = contentWidth, Height = narrowLayout ? 390 : 400 };
        if (narrowLayout)
        {
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            list.MaxHeight = 210;
            Grid.SetRow(detailsPane, 1);
            var horizontalDivider = new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(horizontalDivider, 1);
            detailContent.Margin = new Thickness(8, 12, 8, 4);
            panes.Children.Add(leftPane);
            panes.Children.Add(horizontalDivider);
            panes.Children.Add(detailsPane);
        }
        else
        {
            leftPane.Children.Clear();
            panes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
            Grid.SetRow(search, 0);
            Grid.SetColumn(search, 0);
            Grid.SetRow(list, 1);
            Grid.SetColumn(list, 0);
            Grid.SetRow(noMatches, 1);
            Grid.SetColumn(noMatches, 0);
            Grid.SetRow(count, 2);
            Grid.SetColumn(count, 0);
            panes.Children.Add(search);
            panes.Children.Add(list);
            panes.Children.Add(noMatches);
            panes.Children.Add(count);
            var verticalDivider = new Border
            {
                Width = 1,
                Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRow(verticalDivider, 1);
            Grid.SetRowSpan(verticalDivider, 2);
            Grid.SetColumn(verticalDivider, 1);
            panes.Children.Add(verticalDivider);
            Grid.SetRow(detailsPane, 1);
            Grid.SetRowSpan(detailsPane, 2);
            Grid.SetColumn(detailsPane, 2);
            panes.Children.Add(detailsPane);
        }

        var body = new StackPanel { Spacing = 8, Width = contentWidth };
        body.Children.Add(connectionLabel);
        body.Children.Add(new TextBlock { Text = DesktopText.Get("ChannelPickerSubtitle"), TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        body.Children.Add(panes);
        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("ChannelPickerTitle", provider),
            Content = body,
            PrimaryButtonText = DesktopText.Get("Apply"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = root
        };
        dialog.Resources["ContentDialogMaxWidth"] = Math.Max(320, Math.Min(920, root.Size.Width - 32));
        Refresh();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return channels.Where(c => selected.Contains(c.Id)).ToArray();
    }

    private static string FormatContext(string provider, string context)
    {
        if (provider.Equals("Slack", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Equals("Public channel", StringComparison.OrdinalIgnoreCase)) return DesktopText.Get("ChannelPickerPublic");
            if (context.Equals("Private channel", StringComparison.OrdinalIgnoreCase)) return DesktopText.Get("ChannelPickerPrivate");
        }
        if (provider.Equals("Mattermost", StringComparison.OrdinalIgnoreCase))
            return DesktopText.Get("ChannelPickerTeam", context);
        return context;
    }
}
