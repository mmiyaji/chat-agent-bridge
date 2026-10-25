using System.ComponentModel;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private const string ChatConnectTip = "chat-connect";
    private FeatureTipsStore? _featureTips;
    private bool _featureTipsAttached;
    private bool _chatConnectTipShown;

    // A Gallery-style one-time tip: once setup is done, point at Chat until the user has connected or dismissed it.
    private void InitializeFeatureTips()
    {
        if (_featureTipsAttached) return;
        _featureTipsAttached = true;
        _featureTips = new FeatureTipsStore(new SettingsStore());
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.IsOnboardingActive) && !ViewModel.IsOnboardingActive)
                _ = ShowChatConnectTipAsync();
        };
        _ = ShowChatConnectTipAsync();
    }

    private async Task ShowChatConnectTipAsync()
    {
        if (_chatConnectTipShown || _featureTips is null || App.IsNotificationOnlyLaunch || !ViewModel.IsSettingsLoaded ||
            ViewModel.IsOnboardingActive || InboxPage.ViewModel.IsConnected ||
            // A minimal pane hides the Chat item the tip would point at.
            MainNavigation.DisplayMode == NavigationViewDisplayMode.Minimal) return;
        _chatConnectTipShown = true;
        try { if (await _featureTips.HasSeenAsync(ChatConnectTip)) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        // A restored update prompt waits for open popups. Resolve its local state
        // before showing a non-blocking tip, so the tip cannot hold that prompt open.
        if (Deployment.DeploymentRuntime.IsPortable)
        {
            await App.PortableUpdates.InitializeAsync();
            if (App.PortableUpdates.IsDownloaded) return;
        }
        if (InboxPage.ViewModel.IsConnected || Content is not Panel root) return;

        var chat = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "chat");
        var tip = new TeachingTip
        {
            Target = chat,
            PreferredPlacement = TeachingTipPlacementMode.Right,
            Title = DesktopText.Get("TipChatConnectTitle"),
            Subtitle = DesktopText.Get("TipChatConnectBody"),
            ActionButtonContent = DesktopText.Get("TipOpenChat"),
            CloseButtonContent = DesktopText.Get("TipGotIt")
        };
        AutomationProperties.SetAutomationId(tip, "ChatConnectTip");
        tip.ActionButtonClick += (_, _) =>
        {
            tip.IsOpen = false;
            if (MainNavigation.IsEnabled) MainNavigation.SelectedItem = chat;
        };
        tip.Closed += async (_, _) =>
        {
            root.Children.Remove(tip);
            try { await _featureTips.MarkSeenAsync(ChatConnectTip); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        root.Children.Add(tip);
        tip.IsOpen = true;
    }
}
