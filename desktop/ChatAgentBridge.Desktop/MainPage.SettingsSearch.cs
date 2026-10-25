using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop;

/// <summary>One searchable settings row: what it is called and where it lives.</summary>
public sealed class SettingsSearchEntry
{
    internal SettingsSearchEntry(string title, string location, string pageTag, string? scope, FrameworkElement target, IReadOnlyList<DependencyObject> expanders)
    {
        Title = title;
        Location = location;
        PageTag = pageTag;
        Scope = scope;
        Target = target;
        Expanders = expanders;
    }

    public string Title { get; }
    public string Location { get; }
    internal string PageTag { get; }
    internal string? Scope { get; }
    internal FrameworkElement Target { get; }
    internal IReadOnlyList<DependencyObject> Expanders { get; }
    public override string ToString() => Title;
}

public sealed partial class MainPage
{
    private const int MaxSearchResults = 8;
    private const int MaxLabelLength = 60;
    private IReadOnlyList<SettingsSearchEntry>? _settingsSearchIndex;
    private bool _openingSearchResult;
    private readonly Dictionary<Control, (Brush? Brush, Thickness Thickness, Microsoft.UI.Dispatching.DispatcherQueueTimer Timer)> _searchHighlights =
        new(ReferenceEqualityComparer.Instance);

    public bool CanSearchSettings => ViewModel.IsSettingsLoaded && !ViewModel.IsOnboardingActive;

    /// <summary>Rebuilds the index when the search box gains focus, so visibility and the active provider are current.</summary>
    public void RefreshSettingsSearchIndex() => _settingsSearchIndex = null;

    public IReadOnlyList<SettingsSearchEntry> SearchSettings(string query)
    {
        query = query.Trim();
        if (!CanSearchSettings || query.Length == 0) return [];
        _settingsSearchIndex ??= BuildSettingsSearchIndex();
        return _settingsSearchIndex
            .Select(entry => (entry, rank: Rank(entry.Title, query)))
            .Where(match => match.rank >= 0)
            .OrderBy(match => match.rank)
            .Take(MaxSearchResults)
            .Select(match => match.entry)
            .ToArray();

        // Titles that start with the query come before titles that only contain it.
        static int Rank(string title, string query) =>
            title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 :
            title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 1 : -1;
    }

    public async void OpenSettingsSearchResult(SettingsSearchEntry entry)
    {
        if (!CanSearchSettings || _openingSearchResult) return;
        _openingSearchResult = true;
        try
        {
            // Leaving unsaved settings may ask first; finish the jump only once the page is really shown.
            if (!await NavigateToPageAsync(entry.PageTag)) return;
            // The scope only changes which profile or service is displayed; it never changes the agent used to run.
            if (entry.PageTag == "agent" && entry.Scope is { } agentScope)
            {
                // Opening the page for the first time loads a profile; switch scope only once that load has settled.
                await WaitForAgentEditorIdleAsync();
                if (agentScope != _agentSettingsScope && !await SelectAgentSettingsScopeAsync(agentScope)) return;
            }
            if (entry.PageTag == "connections" && entry.Scope is { } connectionScope) SetConnectionSettingsScope(connectionScope);
            foreach (var expander in entry.Expanders)
            {
                if (expander is SettingsExpander settingsExpander) settingsExpander.IsExpanded = true;
                else if (expander is Expander plain) plain.IsExpanded = true;
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RevealSearchTarget(entry, attempt: 0));
        }
        finally { _openingSearchResult = false; }
    }

    private async Task WaitForAgentEditorIdleAsync()
    {
        for (var waited = 0; waited < 5000 && (_changingAgentScope || _agentProfileDetails is { CanChangeSelection: false }); waited += 100)
            await Task.Delay(100);
    }

    private void RevealSearchTarget(SettingsSearchEntry entry, int attempt)
    {
        if (attempt == 0)
            entry.Target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = true });
        // An input takes focus, which draws its own highlight; a card is outlined briefly instead.
        if (entry.Target is SettingsCard or SettingsExpander)
        {
            FlashSearchTarget(entry.Target);
            return;
        }
        // Right after a page switch the profile may still be loading with its inputs disabled; retry briefly.
        if (FirstInput(entry.Target) is not { } input || input.Focus(FocusState.Keyboard) || attempt >= 10) return;
        var retry = DispatcherQueue.CreateTimer();
        retry.Interval = TimeSpan.FromMilliseconds(150);
        retry.IsRepeating = false;
        retry.Tick += (_, _) => RevealSearchTarget(entry, attempt + 1);
        retry.Start();
    }

    // Briefly outline the found row in the accent color so the eye lands on it. Choosing it again while it is
    // outlined only restarts the timer, so the original border is captured once and always restored.
    private void FlashSearchTarget(FrameworkElement target)
    {
        if (target is not Control control) return;
        if (!_searchHighlights.TryGetValue(control, out var highlight))
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1600);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (!_searchHighlights.Remove(control, out var original)) return;
                control.BorderBrush = original.Brush;
                control.BorderThickness = original.Thickness;
            };
            highlight = (control.BorderBrush, control.BorderThickness, timer);
            _searchHighlights[control] = highlight;
            control.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            control.BorderThickness = new Thickness(2);
        }
        highlight.Timer.Stop();
        highlight.Timer.Start();
    }

    private static Control? FirstInput(DependencyObject root)
    {
        if (root is TextBox or PasswordBox or ComboBox or ToggleSwitch or NumberBox) return (Control)root;
        foreach (var child in Children(root))
            if (child is DependencyObject node && node is not UIElement { Visibility: Visibility.Collapsed } && FirstInput(node) is { } input) return input;
        return null;
    }

    private List<SettingsSearchEntry> BuildSettingsSearchIndex()
    {
        var entries = new List<SettingsSearchEntry>();
        var seen = new HashSet<(string, string)>();
        // One result per control: a visible label (found at its panel, before the input itself) wins over an accessible name.
        var targets = new HashSet<FrameworkElement>(ReferenceEqualityComparer.Instance);
        // Each root fixes the page and, inside the two settings pages, the left-menu scope the rows belong to.
        var contexts = new Dictionary<DependencyObject, (string Page, string? Scope)>(ReferenceEqualityComparer.Instance)
        {
            [GeneralPagePanel] = ("overview", null),
            [WorkbenchPagePanel] = ("workbench", null),
            [AgentPagePanel] = ("agent", "common"),
            [ConnectionsPagePanel] = ("connections", "common"),
            [AboutPage] = ("about", null)
        };
        if (_agentScopeCommonGroup is not null) contexts[_agentScopeCommonGroup] = ("agent", "common");
        // Provider settings belong to the profile shown in the editor, which may differ from the agent used to run.
        if (_agentProfileDetails is not null) contexts[_agentProfileDetails] = ("agent", DisplayedAgentProfile());
        if (_connectionScopeCommonGroup is not null) contexts[_connectionScopeCommonGroup] = ("connections", "common");
        if (_connectionScopeMattermostGroup is not null) contexts[_connectionScopeMattermostGroup] = ("connections", "mattermost");
        if (_connectionScopeSlackGroup is not null) contexts[_connectionScopeSlackGroup] = ("connections", "slack");
        if (_connectionScopeDiscordGroup is not null) contexts[_connectionScopeDiscordGroup] = ("connections", "discord");

        foreach (var root in new DependencyObject[] { GeneralPagePanel, WorkbenchPagePanel, AgentPagePanel, ConnectionsPagePanel, AboutPage })
            Walk(root, contexts[root], [], depth: 0);
        return entries;

        void Walk(object? node, (string Page, string? Scope) context, List<DependencyObject> expanders, int depth)
        {
            if (node is not DependencyObject element || depth > 64) return;
            if (contexts.TryGetValue(element, out var fixedContext)) context = fixedContext;
            // Pages and scopes are collapsed while another one is shown; anything else that is collapsed is unavailable now.
            else if (element is UIElement { Visibility: Visibility.Collapsed }) return;
            if (SearchableRow(element) is ({ } title, { } target)) Add(title, target, context, expanders);
            // A short text placed just before an input names it, whatever its style: "CLI executable" beside its box.
            if (element is Panel panel)
                foreach (var (label, input) in LabelledInputs(panel)) Add(label, input, context, expanders);

            var inner = expanders;
            if (element is SettingsExpander or Expander) inner = [.. expanders, element];
            foreach (var child in Children(element)) Walk(child, context, inner, depth + 1);
        }

        void Add(string title, FrameworkElement target, (string Page, string? Scope) context, List<DependencyObject> expanders)
        {
            if (!targets.Contains(target) && seen.Add((title, context.Page + "/" + context.Scope)) && targets.Add(target))
                entries.Add(new SettingsSearchEntry(title, LocationText(context.Page, context.Scope), context.Page, context.Scope, target, expanders.ToArray()));
        }
    }

    private string DisplayedAgentProfile() =>
        _agentSettingsScope != "common" ? _agentSettingsScope :
        _agentProfileDetails?.CurrentProvider is { Length: > 0 } shown ? shown : ViewModel.AgentProvider;

    private static IEnumerable<(string Label, Control Input)> LabelledInputs(Panel panel)
    {
        var children = panel.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not TextBlock { Visibility: Visibility.Visible } label || LabelText(label.Text) is not { } text) continue;
            // The next visible element that is not more text must hold the input this label names.
            var next = children.Skip(index + 1).FirstOrDefault(child => child.Visibility == Visibility.Visible && child is not TextBlock);
            if (next is not null && FirstInput(next) is { } input) yield return (text, input);
        }
    }

    // A label is a short name, not a help sentence.
    private static string? LabelText(string? text) =>
        Clean(text) is { Length: <= MaxLabelLength } name && !name.Contains('\n') && !name.Contains('。') && !name.Contains(". ") && !name.EndsWith('.')
            ? name : null;

    private static IEnumerable<object?> Children(DependencyObject element) => element switch
    {
        SettingsExpander expander => expander.Items.Prepend(expander.Content),
        Panel panel => panel.Children,
        NavigationView navigation => [navigation.Content],
        ScrollViewer scroll => [scroll.Content],
        Border border => [border.Child],
        UserControl user => [user.Content],
        ContentControl content => [content.Content],
        _ => []
    };

    // A row is a settings card, or an input named by its own header or its accessible name.
    private static (string? Title, FrameworkElement? Target) SearchableRow(DependencyObject element) => element switch
    {
        SettingsCard or SettingsExpander => (HeaderText(element), element as FrameworkElement),
        Control input when ReferenceEquals(FirstInput(input), input) =>
            (LabelText(InputHeader(input)) ?? LabelText(AutomationProperties.GetName(input)), input),
        _ => (null, null)
    };

    private static string? InputHeader(Control input) => input switch
    {
        TextBox { Header: string header } => header,
        PasswordBox { Header: string header } => header,
        ComboBox { Header: string header } => header,
        ToggleSwitch { Header: string header } => header,
        NumberBox { Header: string header } => header,
        _ => null
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? HeaderText(DependencyObject element)
    {
        var header = element switch { SettingsCard card => card.Header, SettingsExpander expander => expander.Header, _ => null };
        var text = header switch
        {
            string value => value,
            TextBlock block => block.Text,
            _ => element is UIElement ui ? AutomationProperties.GetName(ui) : null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private string LocationText(string pageTag, string? scope)
    {
        var page = MainNavigation.MenuItems.Concat(MainNavigation.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag as string == pageTag)?.Content as string ?? pageTag;
        var scopes = pageTag switch { "agent" => _agentScopeNavigation, "connections" => _connectionScopeNavigation, _ => null };
        var scopeItem = scopes?.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == scope);
        return scopeItem is null ? page : $"{page} › {AutomationProperties.GetName(scopeItem)}";
    }
}
