using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ChatAgentBridge_Desktop.Localization;

/// <summary>Reads the app's .resw strings with an explicit language, independent of the view's cached x:Uid values.</summary>
internal static class MrtText
{
    private static readonly ResourceManager ResourceManager = new();
    private static readonly ResourceMap Resources = ResourceManager.MainResourceMap.GetSubtree("Resources");

    public static string Get(string key, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var context = ResourceManager.CreateResourceContext();
        context.QualifierValues["Language"] = culture.Name;
        // PRI stores x:Uid property separators as path segments. Dots inside an
        // attached property's [using:Namespace] qualifier remain literal.
        var bracketStart = key.IndexOf('[');
        var bracketEnd = key.IndexOf(']');
        var path = bracketStart >= 0 && bracketEnd > bracketStart
            ? key[..bracketStart].Replace('.', '/') + key[bracketStart..(bracketEnd + 1)] + key[(bracketEnd + 1)..].Replace('.', '/')
            : key.Replace('.', '/');
        return Resources.TryGetValue(path, context)?.ValueAsString ?? key;
    }

    public static void Refresh(DependencyObject root, CultureInfo culture)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is FrameworkElement element)
                ApplyElement(element, culture);
            if (current is ComboBox combo)
                foreach (var item in combo.Items.OfType<ComboBoxItem>()) ApplyElement(item, culture);
            if (current is RadioButtons radios)
                foreach (var item in radios.Items.OfType<DependencyObject>()) pending.Push(item);
            // The shell is collapsed while onboarding is visible. Include logical
            // content so labels that have not been templated yet also change.
            if (current is NavigationView navigation)
            {
                foreach (var item in navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<DependencyObject>()) pending.Push(item);
            }
            if (current is Panel panel)
                foreach (var child in panel.Children) pending.Push(child);
            if (current is Border border && border.Child is { } borderChild) pending.Push(borderChild);
            if (current is UserControl userControl && userControl.Content is { } userContent) pending.Push(userContent);
            if (current is ContentControl contentControl && contentControl.Content is DependencyObject content) pending.Push(content);
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static void ApplyElement(FrameworkElement element, CultureInfo culture)
    {
        var id = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrWhiteSpace(id)) return;
        switch (element)
        {
            case NavigationViewItem navigation: Apply(id, "Content", culture, value => navigation.Content = value); break;
            case TextBlock textBlock: Apply(id, "Text", culture, value => textBlock.Text = value); break;
            case Button button: Apply(id, "Content", culture, value => button.Content = value); break;
            case CheckBox checkBox: Apply(id, "Content", culture, value => checkBox.Content = value); break;
            case RadioButton radio: Apply(id, "Content", culture, value => radio.Content = value); break;
            case ComboBox comboBox:
                Apply(id, "Header", culture, value => comboBox.Header = value);
                Apply(id, "PlaceholderText", culture, value => comboBox.PlaceholderText = value);
                break;
            case ComboBoxItem item: Apply(id, "Content", culture, value => item.Content = value); break;
            case PasswordBox passwordBox:
                Apply(id, "Header", culture, value => passwordBox.Header = value);
                Apply(id, "PlaceholderText", culture, value => passwordBox.PlaceholderText = value);
                break;
            case TextBox textBox:
                Apply(id, "Header", culture, value => textBox.Header = value);
                Apply(id, "PlaceholderText", culture, value => textBox.PlaceholderText = value);
                break;
            case ToggleSwitch toggle:
                Apply(id, "Header", culture, value => toggle.Header = value);
                Apply(id, "OnContent", culture, value => toggle.OnContent = value);
                Apply(id, "OffContent", culture, value => toggle.OffContent = value);
                break;
        }
        var automationKey = $"{id}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";
        var automationName = Get(automationKey, culture);
        if (!automationName.Equals(automationKey, StringComparison.Ordinal))
            AutomationProperties.SetName(element, automationName);
    }

    private static void Apply(string id, string property, CultureInfo culture, Action<string> set)
    {
        var key = $"{id}.{property}";
        var value = Get(key, culture);
        if (!value.Equals(key, StringComparison.Ordinal)) set(value);
    }
}
