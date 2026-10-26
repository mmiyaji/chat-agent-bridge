using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxPage
{
    private async Task<bool> ConfirmReconnectAsync(CancellationToken cancellationToken)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = DesktopText.Get("InboxReconnectConfirmTitle"),
            Content = new TextBlock { Text = DesktopText.Get("InboxReconnectConfirmBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = DesktopText.Get("InboxReconnectLabel"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "ChatReconnectConfirmation");
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            try { dialog.Hide(); }
            catch (InvalidOperationException) { }
        }));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }
}
