using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.AgentSettings;

/// <summary>Routes accessibility selection through the same deferred scope navigation as the UI.</summary>
public sealed class SettingsScopeNavigationItem : NavigationViewItem
{
    private readonly NavigationView? _navigation;
    private readonly Action? _select;
    private ScopePeer? _peer;

    public SettingsScopeNavigationItem()
    {
        DefaultStyleKey = typeof(NavigationViewItem);
        RegisterPropertyChangedCallback(IsSelectedProperty, (_, _) => _peer?.NotifySelectionChanged());
    }

    public SettingsScopeNavigationItem(NavigationView navigation, Action select) : this()
    {
        _navigation = navigation;
        _select = select;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => _peer ??= new ScopePeer(this);

    private sealed class ScopePeer(SettingsScopeNavigationItem item)
        : FrameworkElementAutomationPeer(item), ISelectionItemProvider, IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(NavigationViewItem);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke
                ? this : base.GetPatternCore(patternInterface);

        public bool IsSelected => item.IsSelected;
        public IRawElementProviderSimple SelectionContainer => item._navigation is { } navigation
            ? ProviderFromPeer(CreatePeerForElement(navigation)) : null!;

        // WinUI's native peer invokes a container synchronously. During Left/Top
        // reparenting its parent ItemsRepeater can be null (native AV). Never enter
        // that path; the app's queued navigation also retains draft/permission checks.
        public void Select() => RequestSelection();
        public void AddToSelection() => RequestSelection();
        public void Invoke() => RequestSelection();
        public void RemoveFromSelection() { } // A settings scope is always selected.

        internal void NotifySelectionChanged()
        {
            var change = item.IsSelected ? AutomationEvents.SelectionItemPatternOnElementSelected
                : AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection;
            if (ListenerExists(change)) RaiseAutomationEvent(change);
        }

        private void RequestSelection()
        {
            // The item itself can be temporarily unloaded while Top navigation
            // moves an overflow item. Its logical scope remains a valid request.
            if (item.IsEnabled && item._navigation is { IsLoaded: true })
                item._select?.Invoke();
        }
    }
}
