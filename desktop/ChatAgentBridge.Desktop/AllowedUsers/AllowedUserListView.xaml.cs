using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.AllowedUsers;

/// <summary>Lists the allowed users of one chat service with their details, and adds or removes IDs in the settings draft.</summary>
public sealed partial class AllowedUserListView : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(AllowedUserListModel), typeof(AllowedUserListView),
        new PropertyMetadata(null, (sender, _) => ((AllowedUserListView)sender).Bindings.Update()));

    public AllowedUserListView() => InitializeComponent();

    public AllowedUserListModel Model
    {
        get => (AllowedUserListModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private string Prefix => Model?.Provider switch { "slack" => "Slack", "mattermost" => "Mattermost", "discord" => "Discord", _ => "" };
    public string ListAutomationId => Prefix + "AllowedUserList";
    public string AddInputAutomationId => Prefix + "AllowedUserAddInput";
    public string AddButtonAutomationId => Prefix + "AllowedUserAddButton";
}
