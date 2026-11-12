using ChatAgentBridge.Core.History;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.AllowedUsers;

/// <summary>Displays the combined allowed-user table.</summary>
public sealed partial class AllowedUserListView : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(AllowedUserTableModel), typeof(AllowedUserListView),
        new PropertyMetadata(null, (sender, _) => ((AllowedUserListView)sender).Bindings.Update()));

    public AllowedUserListView() => InitializeComponent();

    public AllowedUserTableModel Model
    {
        get => (AllowedUserTableModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public event EventHandler<AllowedUserRow>? RemoveUserRequested;
    /// <summary>Raised to show who allowed this user, and when, in the history page.</summary>
    public event EventHandler<AllowedUserRow>? ShowHistoryRequested;

    private void ShowUserHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AllowedUserRow row })
            ShowHistoryRequested?.Invoke(this, row);
    }

    private async void ShowUserDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { DataContext: AllowedUserRow row } button) return;
        var details = new TextBlock
        {
            Text = row.Details,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 360
        };
        var usage = new TextBlock
        {
            Text = DesktopText.Get("AllowedUserUsageLoading"),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(details, "AllowedUserDetailsContent");
        AutomationProperties.SetAutomationId(usage, "AllowedUserUsageContent");
        AutomationProperties.SetLiveSetting(usage, AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 8, MaxWidth = 360 };
        content.Children.Add(details);
        content.Children.Add(usage);
        Presentation.PopupBackdrop.Apply(new Flyout { Content = content }).ShowAt(button);

        try
        {
            var history = await new ExecutionHistoryStore().ListAsync();
            var executions = history.Where(record => record.Source == row.Provider && record.UserId == row.UserId).ToArray();
            var conversations = executions.Where(record => !string.IsNullOrEmpty(record.ChannelId) && !string.IsNullOrEmpty(record.ThreadId))
                .Select(record => (record.ChannelId, record.ThreadId)).Distinct().Count();
            var sessions = executions.Select(record => record.SessionId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count();
            usage.Text = DesktopText.Get("AllowedUserUsageCounts", executions.Length, conversations, sessions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Allowed user history: {ex.GetType().Name}");
            usage.Text = DesktopText.Get("AllowedUserUsageUnavailable");
        }
    }

    private void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AllowedUserRow row })
            RemoveUserRequested?.Invoke(this, row);
    }
}
